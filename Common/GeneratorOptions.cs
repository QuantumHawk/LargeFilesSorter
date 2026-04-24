namespace Common
{
    public sealed class GeneratorOptions
    {
        public required string OutputPath { get; init; }
        public long TargetSizeMb { get; init; }
        public int DistinctStrings { get; init; } = 10000;
        public int Seed { get; init; } = 12345;
        public int WriterBufferSize { get; init; } = 1 << 20;
    }
}