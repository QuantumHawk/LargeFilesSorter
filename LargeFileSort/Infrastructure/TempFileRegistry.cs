using System.Collections.Concurrent;

namespace LargeFileSorter
{
    /// <summary>
    /// Thread-safe registry of temporary files created during a sort run.
    /// Uses <see cref="ConcurrentDictionary{TKey,TValue}"/> so that parallel
    /// chunk-writer workers can register files without external locking.
    /// </summary>
    public sealed class TempFileRegistry
    {
        private readonly ConcurrentDictionary<string, byte> _files =
            new(StringComparer.Ordinal);

        public void Register(string path) => _files.TryAdd(path, 0);

        public IReadOnlyCollection<string> Files => _files.Keys.ToList();

        public void DeleteAllSafe()
        {
            foreach (string file in _files.Keys)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Best-effort: ignore individual deletion failures.
                }
            }
        }
    }
}

