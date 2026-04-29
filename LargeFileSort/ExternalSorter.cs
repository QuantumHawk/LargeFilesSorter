using System.Diagnostics;
using Common;
using LargeFileSorter.Telemetry;

namespace LargeFileSorter
{
    /// <summary>
    /// Thin orchestrator for the three-phase external sort:
    /// <list type="number">
    ///   <item><see cref="SplitPhase"/> — read input, sort in-memory chunks, write binary chunk files.</item>
    ///   <item><see cref="MergePhase"/> — multi-pass k-way merge until a single sorted chunk remains.</item>
    ///   <item><see cref="FinalizePhase"/> — convert the final binary chunk to a UTF-8 text file.</item>
    /// </list>
    /// Also handles resume-from-manifest, atomic output replacement, metrics saving,
    /// and temp-file cleanup according to <see cref="TempFilePolicy"/>.
    /// </summary>
    public sealed class ExternalSorter
    {
        private readonly SortOptions      _options;
        private readonly ProgressReporter _progress;
        private readonly SortMetrics      _metrics;
        private readonly TempFileRegistry _tempRegistry;

        private string ManifestPath => Path.Combine(_options.TempDirectory, "sort-manifest.json");

        public ExternalSorter(SortOptions options)
        {
            _options      = options ?? throw new ArgumentNullException(nameof(options));
            _progress     = new ProgressReporter("sorter");
            _metrics      = new SortMetrics();
            _tempRegistry = new TempFileRegistry();
        }

        public void Sort()
        {
            using var rootActivity = SorterTelemetry.ActivitySource.StartActivity("sort");
            rootActivity?.SetTag("input.path",     _options.InputPath);
            rootActivity?.SetTag("output.path",    _options.OutputPath);
            rootActivity?.SetTag("chunk_size_mb",  _options.ChunkSizeMb);
            rootActivity?.SetTag("merge_fan_in",   _options.MergeFanIn);
            rootActivity?.SetTag("max_parallel_chunk_sorters", _options.MaxParallelChunkSorters);

            ValidateOptions();

            Directory.CreateDirectory(_options.TempDirectory);
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath)) ?? ".");

            string finalTempOutput = _options.OutputPath + ".tmp";
            if (File.Exists(finalTempOutput)) File.Delete(finalTempOutput);

            try
            {
                _progress.Report("starting", force: true);

                SortManifest manifest;
                List<string> currentChunkFiles;

                if (_options.ResumeIfManifestExists && File.Exists(ManifestPath))
                {
                    manifest = SortManifestStore.Load(ManifestPath);
                    ValidateManifest(manifest);

                    currentChunkFiles = manifest.CurrentChunkFiles
                        .Where(File.Exists)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    foreach (string file in currentChunkFiles)
                        _tempRegistry.Register(file);

                    _progress.Report(
                        $"resuming from manifest, stage={manifest.Stage}, " +
                        $"pass={manifest.CurrentMergePass}, files={currentChunkFiles.Count}",
                        force: true);
                }
                else
                {
                    manifest = new SortManifest
                    {
                        InputPath         = _options.InputPath,
                        OutputPath        = _options.OutputPath,
                        TempDirectory     = _options.TempDirectory,
                        Stage             = "Splitting",
                        CurrentMergePass  = 0,
                        CurrentChunkFiles = []
                    };
                    SortManifestStore.Save(manifest, ManifestPath);

                    _progress.Report("phase 1: split and sort chunks", force: true);

                    var splitPhase = new SplitPhase(_options, _metrics, _tempRegistry);
                    currentChunkFiles = splitPhase.Execute();

                    // Free input file disk space immediately after Phase 1.
                    // The input is never read again after split, so deleting it
                    // reduces peak disk usage from 3x to 2x the input file size —
                    // making 100 GB sorts feasible on 200 GiB Fargate ephemeral storage.
                    if (_options.DeleteInputAfterSplit)
                    {
                        try
                        {
                            if (File.Exists(_options.InputPath))
                                File.Delete(_options.InputPath);
                            _progress.Report("input file deleted after split (--delete-input-after-split)", force: true);
                        }
                        catch (Exception ex)
                        {
                            _progress.Report($"warning: could not delete input: {ex.Message}", force: true);
                        }
                    }

                    manifest.CurrentChunkFiles = currentChunkFiles;
                    manifest.Stage             = "SplitCompleted";
                    SortManifestStore.Save(manifest, ManifestPath);

                    _progress.Report($"phase 1 done, chunks={currentChunkFiles.Count}", force: true);
                }

                _progress.Report("phase 2: multi-pass merge", force: true);

                var mergePhase = new MergePhase(_options, _metrics, _tempRegistry, ManifestPath);
                string finalBinaryChunk = mergePhase.Execute(currentChunkFiles, manifest);

                manifest.Stage             = "Finalizing";
                manifest.CurrentChunkFiles = [finalBinaryChunk];
                SortManifestStore.Save(manifest, ManifestPath);

                _progress.Report("phase 3: write final text output", force: true);

                var finalizePhase = new FinalizePhase(_options, _metrics);
                finalizePhase.Execute(finalBinaryChunk, finalTempOutput);

                if (_options.OverwriteOutput)
                    File.Move(finalTempOutput, _options.OutputPath, overwrite: true);
                else
                    File.Move(finalTempOutput, _options.OutputPath);

                if (!string.IsNullOrWhiteSpace(_options.MetricsPath))
                    _metrics.Save(_options.MetricsPath!);

                TryDeleteFile(ManifestPath);
                _progress.Report("completed successfully", force: true);

                if (_options.TempFilePolicy != TempFilePolicy.KeepAll)
                {
                    _tempRegistry.DeleteAllSafe();
                    TryDeleteDirectoryIfEmpty(_options.TempDirectory);
                }
            }
            catch
            {
                TryDeleteFile(finalTempOutput);

                // Record the error on the root span so it shows as failed in the trace backend.
                rootActivity?.SetStatus(ActivityStatusCode.Error);

                if (!string.IsNullOrWhiteSpace(_options.MetricsPath))
                {
                    try { _metrics.Save(_options.MetricsPath!); }
                    catch { /* ignore — metrics save on failure is best-effort */ }
                }

                if (_options.TempFilePolicy == TempFilePolicy.DeleteAlways)
                {
                    _tempRegistry.DeleteAllSafe();
                    TryDeleteFile(ManifestPath);
                    TryDeleteDirectoryIfEmpty(_options.TempDirectory);
                }

                throw;
            }
        }

        // ── Validation ────────────────────────────────────────────────────────────

        private void ValidateOptions()
        {
            if (string.IsNullOrWhiteSpace(_options.InputPath))
                throw new ArgumentException("InputPath is required.");

            if (string.IsNullOrWhiteSpace(_options.OutputPath))
                throw new ArgumentException("OutputPath is required.");

            if (string.IsNullOrWhiteSpace(_options.TempDirectory))
                throw new ArgumentException("TempDirectory is required.");

            if (!File.Exists(_options.InputPath))
                throw new FileNotFoundException("Input file not found.", _options.InputPath);

            if (_options.ChunkSizeMb <= 0 && _options.ChunkSizeBytesOverrideForTests is null)
                throw new ArgumentOutOfRangeException(nameof(_options.ChunkSizeMb), "ChunkSizeMb must be > 0.");

            if (_options.ChunkSizeBytesOverrideForTests is long b && b <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(_options.ChunkSizeBytesOverrideForTests), "Must be > 0.");

            if (_options.MergeFanIn < 2)
                throw new ArgumentOutOfRangeException(nameof(_options.MergeFanIn), "MergeFanIn must be >= 2.");

            if (_options.MaxParallelChunkSorters <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.MaxParallelChunkSorters));

            if (_options.ChunkQueueCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.ChunkQueueCapacity));

            if (_options.MaxConcurrentMerges <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.MaxConcurrentMerges));

            if (_options.ReaderBufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.ReaderBufferSize));

            if (_options.WriterBufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.WriterBufferSize));

            if (_options.InputReadBufferBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.InputReadBufferBytes));

            if (_options.ChunkWriteBufferBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.ChunkWriteBufferBytes));

            if (_options.FinalWriteBufferBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.FinalWriteBufferBytes));

            if (_options.InvalidLineMode == InvalidLineMode.LogInvalid
                && string.IsNullOrWhiteSpace(_options.InvalidLinesLogPath))
                throw new InvalidOperationException(
                    "InvalidLinesLogPath must be set when InvalidLineMode=LogInvalid.");

            if (!_options.OverwriteOutput && File.Exists(_options.OutputPath))
                throw new IOException($"Output file already exists: {_options.OutputPath}");

            // ── Path access checks (fail fast before any work is done) ────────────
            ProbeReadAccess(_options.InputPath);

            string outputDir = Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath)) ?? ".";
            ProbeWriteAccess(outputDir, "output directory");

            // Temp directory may not exist yet — create it temporarily just for the probe.
            bool tempCreatedForProbe = false;
            if (!Directory.Exists(_options.TempDirectory))
            {
                Directory.CreateDirectory(_options.TempDirectory);
                tempCreatedForProbe = true;
            }
            try
            {
                ProbeWriteAccess(_options.TempDirectory, "temp directory");
            }
            finally
            {
                if (tempCreatedForProbe)
                    TryDeleteDirectoryIfEmpty(_options.TempDirectory);
            }

            if (_options.InvalidLineMode == InvalidLineMode.LogInvalid)
            {
                string logDir = Path.GetDirectoryName(Path.GetFullPath(_options.InvalidLinesLogPath!)) ?? ".";
                Directory.CreateDirectory(logDir);
                ProbeWriteAccess(logDir, "invalid-log directory");
            }

            if (!string.IsNullOrWhiteSpace(_options.MetricsPath))
            {
                string metricsDir = Path.GetDirectoryName(Path.GetFullPath(_options.MetricsPath!)) ?? ".";
                Directory.CreateDirectory(metricsDir);
                ProbeWriteAccess(metricsDir, "metrics directory");
            }
        }

        /// <summary>Verifies the file can be opened for reading.</summary>
        private static void ProbeReadAccess(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new UnauthorizedAccessException(
                    $"Cannot read input file '{filePath}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies a directory is writable by creating and immediately deleting a probe file.
        /// Throws <see cref="UnauthorizedAccessException"/> with a clear message if access is denied.
        /// </summary>
        private static void ProbeWriteAccess(string directory, string label)
        {
            string probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(probe, []);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new UnauthorizedAccessException(
                    $"Cannot write to {label} '{directory}': {ex.Message}\n" +
                    "Tip: choose a path inside your user folder, e.g. C:\\Users\\<you>\\tmp-sort", ex);
            }
            finally
            {
                TryDeleteFile(probe);
            }
        }

        private void ValidateManifest(SortManifest manifest)
        {
            if (!string.Equals(manifest.InputPath, _options.InputPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Manifest InputPath does not match current options.");

            if (!string.Equals(manifest.OutputPath, _options.OutputPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Manifest OutputPath does not match current options.");

            if (!string.Equals(manifest.TempDirectory, _options.TempDirectory, StringComparison.Ordinal))
                throw new InvalidOperationException("Manifest TempDirectory does not match current options.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* ignore */ }
        }

        private static void TryDeleteDirectoryIfEmpty(string path)
        {
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path, recursive: false);
            }
            catch { /* ignore */ }
        }
    }
}