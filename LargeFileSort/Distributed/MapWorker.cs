using Common;

namespace LargeFileSorter
{
    internal sealed class MapWorker
    {
        private readonly DistributedOptions _opts;
        public MapWorker(DistributedOptions opts) => _opts = opts;

        public async Task ExecuteAsync()
        {
            Directory.CreateDirectory(_opts.TempDirectory);
            using var s3 = new S3Transport(_opts.AwsRegion);
            var progress = new ProgressReporter($"map-worker-{_opts.WorkerId}");

            // 1. File size + byte range for this worker
            progress.Report("getting input file size...", force: true);
            long fileSize  = await s3.GetFileSizeAsync(_opts.S3Bucket, _opts.S3InputKey).ConfigureAwait(false);
            long sliceSize = fileSize / _opts.WorkerCount;
            long byteStart = (long)_opts.WorkerId * sliceSize;
            long byteEnd   = _opts.WorkerId == _opts.WorkerCount - 1
                ? fileSize - 1 : byteStart + sliceSize - 1;

            progress.Report(
                $"file={ProgressReporter.FormatBytes(fileSize)}, " +
                $"slice=[{byteStart}..{byteEnd}] ({ProgressReporter.FormatBytes(byteEnd - byteStart + 1)})",
                force: true);

            // 2. Download slice aligned to line boundaries
            string sliceFile = Path.Combine(_opts.TempDirectory, $"slice_{_opts.WorkerId}.txt");
            progress.Report($"downloading slice → {sliceFile}", force: true);
            await s3.DownloadSliceToFileAsync(
                _opts.S3Bucket, _opts.S3InputKey,
                byteStart, byteEnd, fileSize,
                _opts.WorkerId, _opts.WorkerCount, sliceFile).ConfigureAwait(false);

            progress.Report($"slice: {ProgressReporter.FormatBytes(new FileInfo(sliceFile).Length)}", force: true);

            // 3. Sort slice → single sorted binary chunk (split + merge, no finalize)
            string sortTempDir  = Path.Combine(_opts.TempDirectory, $"sort_{_opts.WorkerId}");
            string manifestPath = Path.Combine(sortTempDir, "manifest.json");
            Directory.CreateDirectory(sortTempDir);

            var sortOptions = BuildSortOptions(sliceFile, sortTempDir);
            var metrics     = new SortMetrics();
            var registry    = new TempFileRegistry();

            progress.Report("split phase...", force: true);
            var chunkFiles = new SplitPhase(sortOptions, metrics, registry).Execute();

            progress.Report($"merge phase ({chunkFiles.Count} chunks)...", force: true);
            var manifest = new SortManifest
            {
                InputPath = sliceFile, OutputPath = Path.Combine(sortTempDir, "out.txt"),
                TempDirectory = sortTempDir, Stage = "SplitCompleted",
                CurrentChunkFiles = chunkFiles
            };
            SortManifestStore.Save(manifest, manifestPath);
            string sortedBin = new MergePhase(sortOptions, metrics, registry, manifestPath)
                                     .Execute(chunkFiles, manifest);

            progress.Report($"sorted binary: {ProgressReporter.FormatBytes(new FileInfo(sortedBin).Length)}", force: true);

            // 4. Upload sorted binary part to S3
            string partKey = $"{_opts.S3PartsPrefix}part_{_opts.WorkerId:D6}.bin";
            progress.Report($"uploading → s3://{_opts.S3Bucket}/{partKey}", force: true);
            await s3.UploadFileAsync(_opts.S3Bucket, partKey, sortedBin).ConfigureAwait(false);
            progress.Report($"done — uploaded {partKey}", force: true);
        }

        private SortOptions BuildSortOptions(string inputPath, string tempDir) => new()
        {
            InputPath               = inputPath,
            OutputPath              = Path.Combine(tempDir, "out.txt"),
            TempDirectory           = tempDir,
            ChunkSizeMb             = _opts.ChunkSizeMb,
            MergeFanIn              = _opts.MergeFanIn,
            MaxParallelChunkSorters = _opts.MaxParallelChunkSorters,
            InvalidLineMode         = InvalidLineMode.SkipInvalid,
            VerboseProgress         = true
        };
    }
}

