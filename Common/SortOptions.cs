namespace Common
{
    public sealed class SortOptions
    {
        public required string InputPath { get; init; }
        public required string OutputPath { get; init; }
        public required string TempDirectory { get; init; }

        public int ChunkSizeMb { get; init; } = 512;

        /// <summary>
        /// Overrides <see cref="ChunkSizeMb"/> with an exact byte value.
        /// Intended for unit tests only — not exposed via the CLI.
        /// </summary>
        public long? ChunkSizeBytesOverrideForTests { get; init; }

        /// <summary>Effective chunk size in bytes, resolved from <see cref="ChunkSizeBytesOverrideForTests"/>
        /// or <see cref="ChunkSizeMb"/>.</summary>
        public long EffectiveChunkSizeBytes =>
            ChunkSizeBytesOverrideForTests ?? (ChunkSizeMb * 1024L * 1024L);

        public int MergeFanIn { get; init; } = 64;

        public int MaxParallelChunkSorters { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
        public int ChunkQueueCapacity { get; init; } = 2;
        public int MaxConcurrentMerges { get; init; } = 2;

        public int ReaderBufferSize { get; init; } = 1 << 20;
        public int WriterBufferSize { get; init; } = 1 << 20;

        public int InputReadBufferBytes { get; init; } = 4 * 1024 * 1024;
        public int ChunkWriteBufferBytes { get; init; } = 1 * 1024 * 1024;
        public int FinalWriteBufferBytes { get; init; } = 1 * 1024 * 1024;

        public bool UseBlockReader { get; init; } = true;

        public InvalidLineMode InvalidLineMode { get; init; } = InvalidLineMode.Strict;
        public string? InvalidLinesLogPath { get; init; }

        /// <summary>
        /// Controls when temporary chunk and merge files are cleaned up.
        /// Default is <see cref="TempFilePolicy.DeleteOnSuccess"/>.
        /// </summary>
        public TempFilePolicy TempFilePolicy { get; init; } = TempFilePolicy.DeleteOnSuccess;

        public bool OverwriteOutput { get; init; } = true;

        public bool ResumeIfManifestExists { get; init; } = true;
        public string? MetricsPath { get; init; }

        /// <summary>
        /// When true, the input file is deleted immediately after Phase 1 (SplitPhase)
        /// completes. The input is never read again after that point, so deleting it
        /// reclaims ~fileSize bytes of disk space — critical for large files on
        /// constrained storage (e.g. Fargate 200 GiB ephemeral storage).
        /// Reduces peak disk usage from 3× to 2× the input file size.
        /// </summary>
        public bool DeleteInputAfterSplit { get; init; } = false;

        public bool VerboseProgress { get; init; } = true;
    }
}