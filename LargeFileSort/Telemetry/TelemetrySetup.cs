using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LargeFileSorter.Telemetry
{
    /// <summary>
    /// Builds and manages the OpenTelemetry <see cref="TracerProvider"/> and
    /// <see cref="MeterProvider"/> for the lifetime of the process.
    /// <para>
    /// <b>Exporter</b>: OTLP (gRPC) — active when the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable is set.
    /// In ECS this is always <c>http://localhost:4317</c>, pointing at the
    /// ADOT Collector sidecar which forwards traces to AWS X-Ray and metrics
    /// to Amazon CloudWatch.
    /// When the variable is absent (local dev without a collector) the providers
    /// are built with no exporters and become a silent no-op.
    /// </para>
    /// <para>
    /// The standard <c>OTEL_SERVICE_NAME</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c>
    /// environment variables are honoured automatically by the SDK.
    /// </para>
    /// </summary>
    internal static class TelemetrySetup
    {
        /// <summary>
        /// Configures and starts tracing + metrics.
        /// Dispose the returned <see cref="OtelState"/> to flush before process exit.
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
                .SetSampler(new AlwaysOnSampler());

            if (otlpEnabled)
                builder = builder.AddOtlpExporter();

            return builder.Build()!;
        }

        private static MeterProvider BuildMeterProvider(bool otlpEnabled)
        {
            var builder = Sdk.CreateMeterProviderBuilder()
                .AddMeter(SorterTelemetry.MeterName);

            if (otlpEnabled)
                builder = builder.AddOtlpExporter();

            return builder.Build()!;
        }
    }

    /// <summary>Holds both OTel providers and disposes them together.</summary>
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

