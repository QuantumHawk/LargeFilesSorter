
namespace Common
{
    /// <summary>
    /// Plain data transfer object describing the current state of an in-progress sort.
    /// Persistence (Load / Save) is handled by <see cref="SortManifestStore"/>.
    /// </summary>
    public sealed class SortManifest
    {
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string TempDirectory { get; set; } = "";
        public string Stage { get; set; } = "Started";
        public List<string> CurrentChunkFiles { get; set; } = [];
        public int CurrentMergePass { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}