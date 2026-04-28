namespace LargeFileSorter
{
    /// <summary>
    /// Configuration for distributed (multi-worker) sort mode.
    /// Passed via CLI flags; in ECS each task receives its own WorkerId.
    /// </summary>
    public sealed class DistributedOptions
    {
        // ── Common ────────────────────────────────────────────────────────────
        public required string S3Bucket    { get; init; }
        public required string AwsRegion   { get; init; }
        public string TempDirectory        { get; init; } = "/tmp/sort-work";

        // ── Map mode ──────────────────────────────────────────────────────────
        /// <summary>S3 key of the input file (e.g. "input/input.txt").</summary>
        public string S3InputKey           { get; init; } = "";

        /// <summary>S3 prefix where sorted binary parts are written (e.g. "run1/parts/").</summary>
        public string S3PartsPrefix        { get; init; } = "";

        /// <summary>Zero-based index of this worker.</summary>
        public int WorkerId                { get; init; }

        /// <summary>Total number of map workers.</summary>
        public int WorkerCount             { get; init; } = 4;

        // ── Reduce mode ───────────────────────────────────────────────────────
        /// <summary>S3 key for the final sorted text output (e.g. "output/sorted.txt").</summary>
        public string S3OutputKey          { get; init; } = "";

        // ── Sort tuning (forwarded to ExternalSorter / phases) ────────────────
        public int ChunkSizeMb             { get; init; } = 512;
        public int MergeFanIn              { get; init; } = 32;
        public int MaxParallelChunkSorters { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    }
}

