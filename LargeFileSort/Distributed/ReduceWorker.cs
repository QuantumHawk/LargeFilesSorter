using System.Text;
using Common;

namespace LargeFileSorter
{
    /// <summary>
    /// Reduce worker: lists all sorted binary parts uploaded by map workers,
    /// downloads them, then streams a k-way merge directly to the final sorted text
    /// output — without writing an intermediate merged binary file.
    /// Eliminating the intermediate file saves ~100 GB of peak disk usage,
    /// which is critical when the reduce task runs on 200 GiB Fargate storage.
    /// Each part file is deleted from disk the moment its reader is exhausted,
    /// freeing space progressively during the merge.
    /// </summary>
    internal sealed class ReduceWorker
    {
        private readonly DistributedOptions _opts;

        public ReduceWorker(DistributedOptions opts) => _opts = opts;

        public async Task ExecuteAsync()
        {
            Directory.CreateDirectory(_opts.TempDirectory);

            using var s3 = new S3Transport(_opts.AwsRegion);
            var progress = new ProgressReporter("reduce-worker");

            // ── 1. List all parts ─────────────────────────────────────────────
            progress.Report($"listing parts under s3://{_opts.S3Bucket}/{_opts.S3PartsPrefix}", force: true);
            List<string> partKeys = (await s3.ListKeysAsync(_opts.S3Bucket, _opts.S3PartsPrefix).ConfigureAwait(false))
                .Where(k => k.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (partKeys.Count == 0)
                throw new InvalidOperationException($"No .bin parts found under s3://{_opts.S3Bucket}/{_opts.S3PartsPrefix}");

            progress.Report($"found {partKeys.Count} parts", force: true);

            // ── 2. Download all parts to local temp ───────────────────────────
            string partsDir = Path.Combine(_opts.TempDirectory, "parts");
            Directory.CreateDirectory(partsDir);

            var localParts = new List<string>(partKeys.Count);
            for (int i = 0; i < partKeys.Count; i++)
            {
                string localPath = Path.Combine(partsDir, $"part_{i:D6}.bin");
                progress.Report($"downloading part {i + 1}/{partKeys.Count}: {partKeys[i]}", force: true);
                await s3.DownloadFileAsync(_opts.S3Bucket, partKeys[i], localPath).ConfigureAwait(false);
                localParts.Add(localPath);
            }

            // ── 3. Stream k-way merge directly to text output ────────────────
            // No intermediate merged_final.bin — saves ~100 GB of peak disk usage.
            // Each part file is unlinked from the filesystem as soon as its reader
            // is exhausted; the open file descriptor stays valid on Linux.
            string textOutput = Path.Combine(_opts.TempDirectory, "sorted.txt");
            progress.Report($"streaming merge → {textOutput}", force: true);

            long linesWritten = MergePartsToText(localParts, textOutput, progress);

            progress.Report(
                $"finalized: {linesWritten:n0} lines, {ProgressReporter.FormatBytes(new FileInfo(textOutput).Length)}",
                force: true);

            // ── 4. Upload final output to S3 ──────────────────────────────────
            progress.Report($"uploading → s3://{_opts.S3Bucket}/{_opts.S3OutputKey}", force: true);
            await s3.UploadFileAsync(_opts.S3Bucket, _opts.S3OutputKey, textOutput).ConfigureAwait(false);

            // ── 5. Spot-check ─────────────────────────────────────────────────
            Console.WriteLine("=== First 5 lines ===");
            foreach (string line in File.ReadLines(textOutput).Take(5)) Console.WriteLine(line);
            Console.WriteLine("=== Last 5 lines ===");
            foreach (string line in ReadLastLines(textOutput, 5)) Console.WriteLine(line);

            progress.Report($"DONE — s3://{_opts.S3Bucket}/{_opts.S3OutputKey}", force: true);
        }

        /// <summary>
        /// K-way merge across all binary part files, writing text output directly.
        /// Deletes each part file from disk as soon as its reader is exhausted.
        /// </summary>
        private static long MergePartsToText(
            IReadOnlyList<string> partPaths,
            string textOutputPath,
            ProgressReporter progress)
        {
            var readers = new List<ChunkFileReader>(partPaths.Count);
            // Track which readers are still active (not yet exhausted)
            var active  = new List<(int Index, ChunkFileReader Reader, string Path)>(partPaths.Count);

            var queue = new PriorityQueue<QueueItem, QueueItem>(QueueItemComparer.Instance);

            try
            {
                // Open all readers and seed the priority queue
                for (int i = 0; i < partPaths.Count; i++)
                {
                    var reader = new ChunkFileReader(partPaths[i], bufferSize: 1 << 20);
                    readers.Add(reader);
                    active.Add((i, reader, partPaths[i]));

                    if (reader.TryRead(out Record first))
                        queue.Enqueue(new QueueItem(i, first), new QueueItem(i, first));
                }

                using var outputStream = new FileStream(
                    textOutputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4 * 1024 * 1024);
                using var writer = new StreamWriter(
                    outputStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4 * 1024 * 1024);

                long linesWritten = 0;
                var  started      = System.Diagnostics.Stopwatch.StartNew();

                while (queue.Count > 0)
                {
                    QueueItem item = queue.Dequeue();

                    // Write as text: "Number. Text"
                    writer.Write(item.Record.Number);
                    writer.Write(". ");
                    writer.WriteLine(Encoding.UTF8.GetString(item.Record.Utf8Text));

                    linesWritten++;
                    if (linesWritten % 1_000_000 == 0)
                    {
                        double rate = linesWritten / started.Elapsed.TotalSeconds;
                        progress.Report($"merge lines={linesWritten:n0}, rate≈{rate:n0} lines/s");
                    }

                    ChunkFileReader r = readers[item.ReaderIndex];
                    if (r.TryRead(out Record next))
                    {
                        queue.Enqueue(new QueueItem(item.ReaderIndex, next),
                                      new QueueItem(item.ReaderIndex, next));
                    }
                    else
                    {
                        // Reader exhausted — dispose and delete the part file to free disk space.
                        r.Dispose();
                        TryDeleteFile(partPaths[item.ReaderIndex]);
                    }
                }

                writer.Flush();
                return linesWritten;
            }
            finally
            {
                // Dispose any readers not yet disposed (e.g. on exception)
                foreach (var reader in readers) { try { reader.Dispose(); } catch { } }
                // Best-effort cleanup of part files
                foreach (string p in partPaths) TryDeleteFile(p);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static IEnumerable<string> ReadLastLines(string path, int count)
        {
            var q = new Queue<string>(count + 1);
            foreach (string line in File.ReadLines(path)) { q.Enqueue(line); if (q.Count > count) q.Dequeue(); }
            return q;
        }

        private readonly record struct QueueItem(int ReaderIndex, Record Record);

        private sealed class QueueItemComparer : IComparer<QueueItem>
        {
            public static readonly QueueItemComparer Instance = new();
            public int Compare(QueueItem x, QueueItem y)
            {
                int cmp = RecordComparer.Instance.Compare(x.Record, y.Record);
                return cmp != 0 ? cmp : x.ReaderIndex.CompareTo(y.ReaderIndex);
            }
        }
    }
}
