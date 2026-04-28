using System.Diagnostics;
using System.Text;
using Common;
using LargeFileSorter.Telemetry;

namespace LargeFileSorter
{
    /// <summary>
    /// Phase 3: streams the final sorted binary chunk through a <see cref="ChunkFileReader"/>
    /// and writes a UTF-8 text file, writing the number and text directly to the
    /// <see cref="StreamWriter"/> to avoid the intermediate <c>record.ToString()</c>
    /// string allocation present in the original code.
    /// Output byte totals are recorded in <see cref="SortMetrics"/> at the end rather
    /// than per-line, keeping the hot path allocation-free.
    /// </summary>
    internal sealed class FinalizePhase
    {
        private readonly SortOptions      _options;
        private readonly SortMetrics      _metrics;
        private readonly ProgressReporter _progress;

        public FinalizePhase(SortOptions options, SortMetrics metrics)
        {
            _options  = options;
            _metrics  = metrics;
            _progress = new ProgressReporter("finalize");
        }

        public void Execute(string binaryChunkPath, string textOutputPath)
        {
            using var activity   = SorterTelemetry.ActivitySource.StartActivity("sort.finalize");
            long linesWritten = 0;
            var  started      = Stopwatch.StartNew();

            using var chunkReader = new ChunkFileReader(binaryChunkPath, _options.ReaderBufferSize);

            using var outputStream = new FileStream(
                textOutputPath,
                FileMode.Create, FileAccess.Write, FileShare.None,
                _options.FinalWriteBufferBytes);

            using var writer = new StreamWriter(
                outputStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                _options.FinalWriteBufferBytes);

            while (chunkReader.TryRead(out Record record))
            {
                // Decode UTF-8 → string only here at final output, nowhere else in the pipeline
                writer.Write(record.Number);
                writer.Write(". ");
                writer.WriteLine(System.Text.Encoding.UTF8.GetString(record.Utf8Text));

                linesWritten++;

                if (linesWritten % 1_000_000 == 0)
                {
                    double rate = linesWritten / started.Elapsed.TotalSeconds;
                    _progress.Report($"lines={linesWritten:n0}, rate≈{rate:n0} lines/s");
                }
            }

            writer.Flush();

            long totalBytes = outputStream.Position;
            _metrics.AddOutputBytes(totalBytes);

            // ── OTel metrics ─────────────────────────────────────────────────────
            SorterTelemetry.FinalizePhaseDuration.Record(started.Elapsed.TotalSeconds);
            SorterTelemetry.LinesWritten.Add(linesWritten);
            SorterTelemetry.OutputBytesWritten.Add(totalBytes);
            activity?.SetTag("lines_written",  linesWritten);
            activity?.SetTag("output_bytes",   totalBytes);

            _progress.Report(
                $"done: lines={linesWritten:n0}, output≈{ProgressReporter.FormatBytes(totalBytes)}",
                force: true);
        }
    }
}
