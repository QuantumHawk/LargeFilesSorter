using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LargeFileSorter.Telemetry
{
    /// <summary>
    /// Builds and manages the OpenTelemetry <see cref="TracerProvider"/> and
    /// <see cref="MeterProvider"/> for the lifetime of the process.
    /// <para>
    /// <b>Exporters</b>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Console</b> — always active. Traces print finished spans; metrics print
    ///     on export cycle (every 10 s by default).
    ///   </item>
    ///   <item>
    ///     <b>OTLP</b> — enabled when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set
    ///     (e.g. <c>http://localhost:4317</c> for a local Jaeger / Grafana Tempo).
    ///     The standard <c>OTEL_SERVICE_NAME</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c>
    ///     environment variables are also honoured automatically by the SDK.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// Dispose the returned <see cref="OtelState"/> to flush and shut down both
    /// providers before the process exits.
    /// </para>
    /// </summary>
    internal static class TelemetrySetup
    {
        /// <summary>
        /// Configures and starts tracing + metrics.
        /// The caller must dispose the returned state when the process is done.
        /// </summary>
        public static OtelState Configure()
        {
            bool otlpEnabled = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

            TracerProvider tracerProvider = BuildTracerProvider(otlpEnabled);
            MeterProvider  meterProvider  = BuildMeterProvider(otlpEnabled);

            return new OtelState(tracerProvider, meterProvider);
        }

        private static TracerProvider BuildTracerProvider(bool otlpEnabled)
        {
            var builder = Sdk.CreateTracerProviderBuilder()
                .AddSource(SorterTelemetry.ActivitySourceName)
                .SetSampler(new AlwaysOnSampler())
                .AddConsoleExporter();

            if (otlpEnabled)
                builder = builder.AddOtlpExporter();

            return builder.Build()!;
        }

        private static MeterProvider BuildMeterProvider(bool otlpEnabled)
        {
            var builder = Sdk.CreateMeterProviderBuilder()
                .AddMeter(SorterTelemetry.MeterName)
                .AddConsoleExporter();

            if (otlpEnabled)
                builder = builder.AddOtlpExporter();

            return builder.Build()!;
        }
    }

    /// <summary>
    /// Holds references to both OTel providers and disposes them together.
    /// </summary>
    internal sealed class OtelState : IDisposable
    {
        private readonly TracerProvider _tracerProvider;
        private readonly MeterProvider  _meterProvider;
        private bool _disposed;

        public OtelState(TracerProvider tracerProvider, MeterProvider meterProvider)
        {
            _tracerProvider = tracerProvider;
            _meterProvider  = meterProvider;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _meterProvider.ForceFlush();
            _meterProvider.Dispose();
            _tracerProvider.ForceFlush();
            _tracerProvider.Dispose();
        }
    }
}

