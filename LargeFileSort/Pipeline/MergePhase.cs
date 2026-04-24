using Common;

namespace LargeFileSorter
{
    /// <summary>
    /// Phase 2: performs multi-pass k-way merge on the sorted chunk files produced by
    /// <see cref="SplitPhase"/> until a single sorted binary chunk remains.
    /// <para>
    /// Each pass groups the current set of files into batches of <see cref="SortOptions.MergeFanIn"/>
    /// and merges each batch concurrently (bounded by <see cref="SortOptions.MaxConcurrentMerges"/>).
    /// Intermediate files from the previous pass are deleted after each pass (unless
    /// <see cref="TempFilePolicy.KeepAll"/> is set) to keep disk usage bounded.
    /// The O(n) <c>List.Contains</c> from the original code is replaced with a
    /// <see cref="HashSet{T}"/> lookup.
    /// </para>
    /// </summary>
    internal sealed class MergePhase
    {
        private readonly SortOptions      _options;
        private readonly SortMetrics      _metrics;
        private readonly TempFileRegistry _tempRegistry;
        private readonly string           _manifestPath;
        private readonly ProgressReporter _progress;

        public MergePhase(
            SortOptions options,
            SortMetrics metrics,
            TempFileRegistry tempRegistry,
            string manifestPath)
        {
            _options      = options;
            _metrics      = metrics;
            _tempRegistry = tempRegistry;
            _manifestPath = manifestPath;
            _progress     = new ProgressReporter("merge");
        }

        public string Execute(List<string> initialChunks, SortManifest manifest)
        {
            if (initialChunks.Count == 0)
            {
                string emptyChunk = CreateEmptyChunk();
                _tempRegistry.Register(emptyChunk);
                manifest.CurrentChunkFiles = [emptyChunk];
                manifest.Stage             = "SplitCompleted";
                manifest.CurrentMergePass  = 0;
                SortManifestStore.Save(manifest, _manifestPath);
                return emptyChunk;
            }

            if (initialChunks.Count == 1)
            {
                return initialChunks[0];
            }

            var mergeEngine        = new MergeEngine(_options, _metrics);
            int maxConcurrentMerges = Math.Max(1, _options.MaxConcurrentMerges);

            List<string> currentFiles = initialChunks;
            int          pass         = manifest.CurrentMergePass;

            while (currentFiles.Count > 1)
            {
                pass++;

                var nextFiles     = new List<string>();
                var nextFilesLock = new object();

                var batches = Batch(currentFiles, _options.MergeFanIn)
                    .Select((batch, g) => new { Batch = batch, Group = g })
                    .ToList();

                using var semaphore = new SemaphoreSlim(maxConcurrentMerges, maxConcurrentMerges);
                Exception? mergeException = null;

                Task[] mergeTasks = batches
                    .Select(info => Task.Run(async () =>
                    {
                        await semaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (Volatile.Read(ref mergeException) is not null) return;

                            string mergedFile = Path.Combine(
                                _options.TempDirectory,
                                $"merge_pass{pass:D4}_group{info.Group:D6}.bin");

                            mergeEngine.Merge(
                                info.Batch,
                                mergedFile,
                                $"merge pass={pass} group={info.Group}");

                            _tempRegistry.Register(mergedFile);

                            lock (nextFilesLock) { nextFiles.Add(mergedFile); }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref mergeException, ex, null);
                            throw;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }))
                    .ToArray();

                Task allTasks = Task.WhenAll(mergeTasks);
                try
                {
                    allTasks.GetAwaiter().GetResult();
                }
                catch
                {
                    lock (nextFilesLock)
                    {
                        foreach (string file in nextFiles) TryDeleteFile(file);
                    }

                    if (mergeException is not null) throw mergeException;
                    throw;
                }

                nextFiles.Sort(StringComparer.Ordinal);
                _metrics.AddMergePass();

                manifest.CurrentChunkFiles = nextFiles;
                manifest.CurrentMergePass  = pass;
                manifest.Stage             = "Merging";
                SortManifestStore.Save(manifest, _manifestPath);

                // Delete previous-pass files to reclaim disk space.
                // HashSet replaces the O(n) List.Contains call from the original code.
                if (_options.TempFilePolicy != TempFilePolicy.KeepAll)
                {
                    var nextSet = new HashSet<string>(nextFiles, StringComparer.Ordinal);
                    foreach (string file in currentFiles)
                    {
                        if (!nextSet.Contains(file)) TryDeleteFile(file);
                    }
                }

                _progress.Report(
                    $"pass {pass} done, inputs={currentFiles.Count}, outputs={nextFiles.Count}, " +
                    $"merged={_metrics.Snapshot.RecordsMerged:n0}",
                    force: true);

                currentFiles = nextFiles;
            }

            return currentFiles[0];
        }

        private string CreateEmptyChunk()
        {
            string path = Path.Combine(_options.TempDirectory, "empty_final_chunk.bin");
            // ChunkFileWriter writes a valid binary header on construction.
            using var _ = new ChunkFileWriter(path, _options.ChunkWriteBufferBytes);
            return path;
        }

        private static IEnumerable<List<string>> Batch(List<string> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
            {
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* ignore — best-effort cleanup */ }
        }
    }
}

