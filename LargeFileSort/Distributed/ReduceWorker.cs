using Common;

namespace LargeFileSorter
{
    /// <summary>
    /// Reduce worker: lists all sorted binary parts uploaded by map workers,
    /// downloads them, merges them with <see cref="MergeEngine"/> (k-way min-heap),
    /// writes the final sorted text via <see cref="FinalizePhase"/>,
    /// and uploads the result to S3.
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

            // ── 3. Merge all parts into one sorted binary ─────────────────────
            var sortOptions = BuildSortOptions();
            var metrics     = new SortMetrics();
            string mergedBin = Path.Combine(_opts.TempDirectory, "merged_final.bin");

            progress.Report($"merging {localParts.Count} sorted parts (fan-in={_opts.MergeFanIn})...", force: true);
            new MergeEngine(sortOptions, metrics).Merge(localParts, mergedBin, "reduce-merge");
            progress.Report($"merged: {ProgressReporter.FormatBytes(new FileInfo(mergedBin).Length)}", force: true);

            // ── 4. Finalize: binary → sorted text ─────────────────────────────
            string textOutput = Path.Combine(_opts.TempDirectory, "sorted.txt");
            new FinalizePhase(sortOptions, metrics).Execute(mergedBin, textOutput);
            progress.Report($"finalized: {ProgressReporter.FormatBytes(new FileInfo(textOutput).Length)}", force: true);

            // ── 5. Upload final output to S3 ──────────────────────────────────
            progress.Report($"uploading → s3://{_opts.S3Bucket}/{_opts.S3OutputKey}", force: true);
            await s3.UploadFileAsync(_opts.S3Bucket, _opts.S3OutputKey, textOutput).ConfigureAwait(false);

            // ── 6. Spot-check ─────────────────────────────────────────────────
            Console.WriteLine("=== First 5 lines ===");
            foreach (string line in File.ReadLines(textOutput).Take(5)) Console.WriteLine(line);
            Console.WriteLine("=== Last 5 lines ===");
            foreach (string line in ReadLastLines(textOutput, 5)) Console.WriteLine(line);

            progress.Report($"DONE — s3://{_opts.S3Bucket}/{_opts.S3OutputKey}", force: true);
        }

        private SortOptions BuildSortOptions() => new()
        {
            InputPath     = Path.Combine(_opts.TempDirectory, "dummy.txt"),
            OutputPath    = Path.Combine(_opts.TempDirectory, "sorted.txt"),
            TempDirectory = _opts.TempDirectory,
            MergeFanIn    = _opts.MergeFanIn,
            VerboseProgress = true
        };

        private static IEnumerable<string> ReadLastLines(string path, int count)
        {
            var q = new Queue<string>(count + 1);
            foreach (string line in File.ReadLines(path)) { q.Enqueue(line); if (q.Count > count) q.Dequeue(); }
            return q;
        }
    }
}
