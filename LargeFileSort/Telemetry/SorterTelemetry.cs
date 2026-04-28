using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LargeFileSorter.Telemetry
{
    /// <summary>
    /// Central registry of all OpenTelemetry instruments used by the sorter pipeline.
    /// <para>
    /// Tracing — one <see cref="Activity"/> span per pipeline phase:
    /// <c>sort</c> (root), <c>sort.split</c>, <c>sort.merge</c>, <c>sort.finalize</c>.
    /// An additional <c>sort.split.chunk</c> span is emitted per sorted chunk so
    /// per-chunk sort times are visible in a trace backend.
    /// </para>
    /// <para>
    /// Metrics — all instruments live under the <c>LargeFileSorter</c> meter:
    /// <list type="bullet">
    ///   <item>Counters: valid/invalid lines, chunks, merge passes, records merged, output lines.</item>
    ///   <item>UpDownCounters: bytes read, bytes written.</item>
    ///   <item>Histograms: per-phase wall-clock duration, per-chunk sort time.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class SorterTelemetry
    {
        public const string ActivitySourceName = "LargeFileSorter";
        public const string MeterName          = "LargeFileSorter";
        private const string Version           = "1.0.0";

        // ── Tracing ──────────────────────────────────────────────────────────────
        public static readonly ActivitySource ActivitySource =
            new(ActivitySourceName, Version);

        // ── Metrics ──────────────────────────────────────────────────────────────
        public static readonly Meter Meter = new(MeterName, Version);

        // -- Counters
        public static readonly Counter<long> ValidLines =
            Meter.CreateCounter<long>(
                "sort.lines.valid", "lines",
                "Number of valid input lines parsed.");

        public static readonly Counter<long> InvalidLines =
            Meter.CreateCounter<long>(
                "sort.lines.invalid", "lines",
                "Number of invalid input lines encountered.");

        public static readonly Counter<long> ChunksCreated =
            Meter.CreateCounter<long>(
                "sort.chunks.created", "chunks",
                "Number of sorted binary chunk files written during the split phase.");

        public static readonly Counter<long> MergePasses =
            Meter.CreateCounter<long>(
                "sort.merge.passes", "passes",
                "Number of merge passes completed.");

        public static readonly Counter<long> RecordsMerged =
            Meter.CreateCounter<long>(
                "sort.records.merged", "records",
                "Total records processed across all merge passes.");

        public static readonly Counter<long> LinesWritten =
            Meter.CreateCounter<long>(
                "sort.lines.written", "lines",
                "Number of lines written to the final sorted output file.");

        // -- UpDownCounters (bytes are cumulative but we want them observable)
        public static readonly UpDownCounter<long> InputBytesRead =
            Meter.CreateUpDownCounter<long>(
                "sort.bytes.input", "bytes",
                "Total bytes read from the input file.");

        public static readonly UpDownCounter<long> OutputBytesWritten =
            Meter.CreateUpDownCounter<long>(
                "sort.bytes.output", "bytes",
                "Total bytes written to the sorted output file.");

        // -- Histograms
        public static readonly Histogram<double> SplitPhaseDuration =
            Meter.CreateHistogram<double>(
                "sort.phase.split.duration", "s",
                "Wall-clock time spent in the split phase (reading + sorting + writing chunks).");

        public static readonly Histogram<double> MergePhaseDuration =
            Meter.CreateHistogram<double>(
                "sort.phase.merge.duration", "s",
                "Wall-clock time spent in the merge phase (all merge passes combined).");

        public static readonly Histogram<double> FinalizePhaseDuration =
            Meter.CreateHistogram<double>(
                "sort.phase.finalize.duration", "s",
                "Wall-clock time spent writing the final text output.");

        public static readonly Histogram<double> ChunkSortDuration =
            Meter.CreateHistogram<double>(
                "sort.chunk.sort.duration", "s",
                "Time to in-memory sort a single chunk.");
    }
}

