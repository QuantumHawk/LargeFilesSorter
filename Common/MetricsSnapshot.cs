namespace Common
{
    public sealed class MetricsSnapshot
    {
        public long InputBytesRead { get; set; }
        public long OutputBytesWritten { get; set; }
        public long ValidLines { get; set; }
        public long InvalidLines { get; set; }
        public long ChunkFilesCreated { get; set; }
        public long MergePassesCompleted { get; set; }
        public long RecordsMerged { get; set; }
        public long PeakManagedMemoryBytes { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}