using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Common;

namespace LargeFileSorter
{
    /// <summary>
    /// Phase 1: reads the input file, partitions it into fixed-size in-memory batches,
    /// sorts each batch, and writes sorted binary chunk files to the temp directory.
    /// <para>
    /// A single producer task reads lines and posts <see cref="SortChunk"/> objects into
    /// a bounded <see cref="Channel{T}"/>. A pool of worker tasks drains the channel,
    /// sorts each chunk, and writes the result to disk in parallel.
    /// A <see cref="CancellationTokenSource"/> lets any failing task cancel the others
    /// cleanly, and <see cref="Task.WhenAll"/> aggregates all exceptions so the first
    /// non-cancellation error is re-thrown with its original stack preserved.
    /// </para>
    /// <para>
    /// Invalid lines — when <see cref="InvalidLineMode.LogInvalid"/> is selected — are
    /// forwarded through a dedicated unbounded channel to a single log-writer task,
    /// avoiding a shared lock in the hot path.
    /// </para>
    /// </summary>
    internal sealed class SplitPhase
    {
        private readonly SortOptions _options;
        private readonly SortMetrics _metrics;
        private readonly TempFileRegistry _tempRegistry;
        private readonly ProgressReporter _progress;

        public SplitPhase(SortOptions options, SortMetrics metrics, TempFileRegistry tempRegistry)
        {
            _options      = options;
            _metrics      = metrics;
            _tempRegistry = tempRegistry;
            _progress     = new ProgressReporter("split");
        }

        private sealed class SortChunk
        {
            public required int         ChunkIndex     { get; init; }
            public required List<Record> Records       { get; init; }
            public required long        EstimatedBytes { get; init; }
        }

        public List<string> Execute()
        {
            long targetChunkBytes = _options.EffectiveChunkSizeBytes;
            var  chunkPaths       = new List<string>();
            var  chunkPathsLock   = new object();

            int workerCount   = Math.Max(1, _options.MaxParallelChunkSorters);
            int queueCapacity = Math.Max(1, _options.ChunkQueueCapacity);

            var channel = Channel.CreateBounded<SortChunk>(new BoundedChannelOptions(queueCapacity)
            {
                SingleWriter                = true,
                SingleReader                = false,
                FullMode                    = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            using var cts = new CancellationTokenSource();

            int nextChunkIndex = 0;

            // Optional invalid-line log: single-consumer channel avoids a shared lock.
            Channel<string>? invalidLogChannel = null;
            Task?            invalidLogTask    = null;

            try
            {
                if (_options.InvalidLineMode == InvalidLineMode.LogInvalid)
                {
                    string logDir = Path.GetDirectoryName(Path.GetFullPath(_options.InvalidLinesLogPath!)) ?? ".";
                    Directory.CreateDirectory(logDir);

                    invalidLogChannel = Channel.CreateUnbounded<string>(
                        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

                    invalidLogTask = Task.Run(async () =>
                    {
                        using var logStream = new FileStream(
                            _options.InvalidLinesLogPath!,
                            FileMode.Create, FileAccess.Write, FileShare.None,
                            _options.WriterBufferSize);

                        using var logWriter = new StreamWriter(
                            logStream,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            _options.WriterBufferSize);

                        await foreach (string line in invalidLogChannel.Reader.ReadAllAsync().ConfigureAwait(false))
                        {
                            logWriter.WriteLine(line);
                        }
                    });
                }

                Task producerTask = Task.Run(async () =>
                {
                    var  records                   = new List<Record>(capacity: 1_000_000);
                    long currentChunkEstimatedBytes = 0;

                    void ProcessLine(string line)
                    {
                        _metrics.AddInputBytes(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);

                        if (LineParser.TryParse(line, out Record record))
                        {
                            records.Add(record);
                            _metrics.AddValidLines(1);
                            currentChunkEstimatedBytes += EstimateRecordBytes(record);
                        }
                        else
                        {
                            _metrics.AddInvalidLines(1);
                            long invalidCount = _metrics.Snapshot.InvalidLines;

                            switch (_options.InvalidLineMode)
                            {
                                case InvalidLineMode.Strict:
                                    throw new FormatException(
                                        $"Invalid input line at count={invalidCount:n0}: [{line}]");

                                case InvalidLineMode.SkipInvalid:
                                    if (invalidCount % 100_000 == 0)
                                        _progress.Report($"invalid skipped={invalidCount:n0}");
                                    break;

                                case InvalidLineMode.LogInvalid:
                                    invalidLogChannel!.Writer.TryWrite(line);
                                    if (invalidCount % 100_000 == 0)
                                        _progress.Report($"invalid logged={invalidCount:n0}");
                                    break;

                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }

                        _metrics.CaptureMemory();
                    }

                    async ValueTask FlushChunkAsync()
                    {
                        if (records.Count == 0) return;

                        var chunk = new SortChunk
                        {
                            ChunkIndex     = nextChunkIndex++,
                            Records        = records,
                            EstimatedBytes = currentChunkEstimatedBytes
                        };

                        await channel.Writer.WriteAsync(chunk, cts.Token).ConfigureAwait(false);

                        records = new List<Record>(capacity: 1_000_000);
                        currentChunkEstimatedBytes = 0;

                        _progress.Report(
                            $"split queued: chunks={nextChunkIndex}, " +
                            $"valid={_metrics.Snapshot.ValidLines:n0}, invalid={_metrics.Snapshot.InvalidLines:n0}, " +
                            $"read≈{ProgressReporter.FormatBytes(_metrics.Snapshot.InputBytesRead)}, " +
                            $"peakMem≈{ProgressReporter.FormatBytes(_metrics.Snapshot.PeakManagedMemoryBytes)}");
                    }

                    try
                    {
                        if (_options.UseBlockReader)
                        {
                            using var reader = new BlockLineReader(_options.InputPath, _options.InputReadBufferBytes);
                            string? line;
                            while (reader.TryReadLine(out line))
                            {
                                if (line is null) continue;
                                ProcessLine(line);
                                if (currentChunkEstimatedBytes >= targetChunkBytes)
                                    await FlushChunkAsync().ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            using var stream = new FileStream(
                                _options.InputPath,
                                FileMode.Open, FileAccess.Read, FileShare.Read,
                                _options.ReaderBufferSize, FileOptions.SequentialScan);

                            using var reader = new StreamReader(
                                stream, Encoding.UTF8,
                                detectEncodingFromByteOrderMarks: true,
                                _options.ReaderBufferSize);

                            string? line;
                            while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
                            {
                                ProcessLine(line);
                                if (currentChunkEstimatedBytes >= targetChunkBytes)
                                    await FlushChunkAsync().ConfigureAwait(false);
                            }
                        }

                        await FlushChunkAsync().ConfigureAwait(false);
                        channel.Writer.TryComplete();
                    }
                    catch (Exception ex)
                    {
                        cts.Cancel();
                        channel.Writer.TryComplete(ex);
                        throw;
                    }
                });

                Task[] workerTasks = Enumerable.Range(0, workerCount)
                    .Select(workerId => Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (SortChunk chunk in channel.Reader
                                .ReadAllAsync(cts.Token).ConfigureAwait(false))
                            {
                                chunk.Records.Sort(RecordComparer.Instance);

                                string chunkPath = Path.Combine(
                                    _options.TempDirectory,
                                    $"chunk_{chunk.ChunkIndex:D8}.bin");

                                using var writer = new ChunkFileWriter(chunkPath, _options.ChunkWriteBufferBytes);
                                foreach (Record record in chunk.Records)
                                {
                                    writer.WriteRecord(record);
                                }

                                _tempRegistry.Register(chunkPath);
                                _metrics.AddChunkFiles(1);

                                lock (chunkPathsLock)
                                {
                                    chunkPaths.Add(chunkPath);
                                }

                                _progress.Report(
                                    $"worker {workerId}: wrote chunk={chunk.ChunkIndex}, " +
                                    $"records={chunk.Records.Count:n0}, " +
                                    $"estSize≈{ProgressReporter.FormatBytes(chunk.EstimatedBytes)}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected when the producer cancels via cts; just propagate.
                            throw;
                        }
                        catch (Exception)
                        {
                            cts.Cancel();
                            throw;
                        }
                    }))
                    .ToArray();

                Task allTasks = Task.WhenAll(workerTasks.Prepend(producerTask));

                try
                {
                    allTasks.GetAwaiter().GetResult();
                }
                catch
                {
                    // Re-throw the first non-cancellation exception with its original stack.
                    if (allTasks.Exception is AggregateException ae)
                    {
                        Exception? real = ae.InnerExceptions
                            .FirstOrDefault(e => e is not OperationCanceledException);
                        if (real is not null)
                        {
                            ExceptionDispatchInfo.Capture(real).Throw();
                        }
                    }

                    throw;
                }

                chunkPaths.Sort(StringComparer.Ordinal);

                _progress.Report(
                    $"split done: chunks={chunkPaths.Count}, " +
                    $"valid={_metrics.Snapshot.ValidLines:n0}, invalid={_metrics.Snapshot.InvalidLines:n0}, " +
                    $"read≈{ProgressReporter.FormatBytes(_metrics.Snapshot.InputBytesRead)}, " +
                    $"peakMem≈{ProgressReporter.FormatBytes(_metrics.Snapshot.PeakManagedMemoryBytes)}",
                    force: true);

                return chunkPaths;
            }
            finally
            {
                // Complete the invalid-log channel and wait for the writer to flush.
                invalidLogChannel?.Writer.TryComplete();
                try { invalidLogTask?.GetAwaiter().GetResult(); }
                catch { /* log-task failure is non-critical */ }
            }
        }

        /// <summary>
        /// Estimates in-memory record size using 1 byte per char (accurate for ASCII,
        /// slight undercount for multi-byte UTF-8) plus fixed overhead.
        /// The previous implementation used <c>sizeof(char) == 2</c> which over-estimated
        /// by ~2× for ASCII, causing chunks to be written at half the target size.
        /// </summary>
        private static long EstimateRecordBytes(Record record) =>
            sizeof(ulong) + record.Utf8Text.Length + 24 + 64; // byte[] header + record overhead
    }
}

