using Common;

namespace LargeFileSorter
{
    /// <summary>
    /// k-way merge of a set of sorted binary chunk files into a single sorted chunk file,
    /// using a min-heap (<see cref="PriorityQueue{TElement,TPriority}"/>).
    /// </summary>
    public sealed class MergeEngine
    {
        private readonly SortOptions _options;
        private readonly SortMetrics _metrics;

        public MergeEngine(SortOptions options, SortMetrics metrics)
        {
            _options = options;
            _metrics = metrics;
        }

        public void Merge(IReadOnlyList<string> inputFiles, string outputFile, string progressPrefix)
        {
            var readers = new List<ChunkFileReader>(inputFiles.Count);
            var progress = new ProgressReporter(progressPrefix);
            long writtenRecords = 0;

            var queue = new PriorityQueue<QueueItem, QueueItem>(QueueItemComparer.Instance);

            try
            {
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    var reader = new ChunkFileReader(inputFiles[i], _options.ReaderBufferSize);
                    readers.Add(reader);

                    if (reader.TryRead(out Record record))
                    {
                        var item = new QueueItem(i, record);
                        queue.Enqueue(item, item);
                    }
                }

                using var writer = new ChunkFileWriter(outputFile, _options.ChunkWriteBufferBytes);

                while (queue.Count > 0)
                {
                    QueueItem current = queue.Dequeue();
                    writer.WriteRecord(current.Record);

                    writtenRecords++;
                    _metrics.AddMergedRecords(1);

                    if (writtenRecords % 1_000_000 == 0)
                    {
                        progress.Report($"merged={writtenRecords:n0} records");
                    }

                    if (readers[current.ReaderIndex].TryRead(out Record nextRecord))
                    {
                        var nextItem = new QueueItem(current.ReaderIndex, nextRecord);
                        queue.Enqueue(nextItem, nextItem);
                    }
                }

                progress.Report($"done, merged={writtenRecords:n0} records", force: true);
            }
            finally
            {
                foreach (var reader in readers)
                {
                    reader.Dispose();
                }
            }
        }

        private readonly record struct QueueItem(int ReaderIndex, Record Record);

        private sealed class QueueItemComparer : IComparer<QueueItem>
        {
            public static readonly QueueItemComparer Instance = new();

            public int Compare(QueueItem x, QueueItem y)
            {
                int cmp = RecordComparer.Instance.Compare(x.Record, y.Record);
                // Break ties by reader index to maintain a stable merge order.
                return cmp != 0 ? cmp : x.ReaderIndex.CompareTo(y.ReaderIndex);
            }
        }
    }
}

