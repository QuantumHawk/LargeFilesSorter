using System.Text.Json;
using Common;

namespace LargeFileSorter
{
    /// <summary>
    /// Thread-safe sort metrics using <see cref="Interlocked"/> operations on individual
    /// fields instead of a coarse lock, and a CAS loop for peak-memory tracking.
    /// <see cref="JsonSerializerOptions"/> is cached as a static field to avoid
    /// per-save allocations.
    /// </summary>
    public sealed class SortMetrics
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private long _inputBytesRead;
        private long _outputBytesWritten;
        private long _validLines;
        private long _invalidLines;
        private long _chunkFilesCreated;
        private long _mergePassesCompleted;
        private long _recordsMerged;
        private long _peakManagedMemoryBytes;
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        public MetricsSnapshot Snapshot => new MetricsSnapshot
        {
            InputBytesRead        = Volatile.Read(ref _inputBytesRead),
            OutputBytesWritten    = Volatile.Read(ref _outputBytesWritten),
            ValidLines            = Volatile.Read(ref _validLines),
            InvalidLines          = Volatile.Read(ref _invalidLines),
            ChunkFilesCreated     = Volatile.Read(ref _chunkFilesCreated),
            MergePassesCompleted  = Volatile.Read(ref _mergePassesCompleted),
            RecordsMerged         = Volatile.Read(ref _recordsMerged),
            PeakManagedMemoryBytes = Volatile.Read(ref _peakManagedMemoryBytes),
            StartedUtc            = _startedUtc,
            UpdatedUtc            = DateTime.UtcNow
        };

        public void AddInputBytes(long value)    => Interlocked.Add(ref _inputBytesRead, value);
        public void AddOutputBytes(long value)   => Interlocked.Add(ref _outputBytesWritten, value);
        public void AddValidLines(long value)    => Interlocked.Add(ref _validLines, value);
        public void AddInvalidLines(long value)  => Interlocked.Add(ref _invalidLines, value);
        public void AddChunkFiles(long value)    => Interlocked.Add(ref _chunkFilesCreated, value);
        public void AddMergePass()               => Interlocked.Increment(ref _mergePassesCompleted);
        public void AddMergedRecords(long value) => Interlocked.Add(ref _recordsMerged, value);

        public void CaptureMemory()
        {
            long current = GC.GetTotalMemory(forceFullCollection: false);
            long observed;
            do
            {
                observed = Volatile.Read(ref _peakManagedMemoryBytes);
                if (current <= observed) return;
            }
            while (Interlocked.CompareExchange(ref _peakManagedMemoryBytes, current, observed) != observed);
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(Snapshot, JsonOptions));
        }
    }
}

