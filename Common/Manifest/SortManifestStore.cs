using System.Text.Json;

namespace Common
{
    /// <summary>
    /// Handles persistence of <see cref="SortManifest"/> — load and save, with a cached
    /// <see cref="JsonSerializerOptions"/> instance to avoid per-call allocations.
    /// </summary>
    public static class SortManifestStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static SortManifest Load(string path)
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SortManifest>(json, JsonOptions)
                   ?? throw new InvalidDataException("Failed to deserialize sort manifest.");
        }

        public static void Save(SortManifest manifest, string path)
        {
            manifest.UpdatedUtc = DateTime.UtcNow;
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        }
    }
}

