using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Rag.NET.Telemetry.Tests;

/// <summary>
/// Proves what this package exists for: that <see cref="RagNetInstrumentationExtensions.AddRagNetInstrumentation"/>
/// actually wires an OpenTelemetry <see cref="TracerProvider"/> and <see cref="MeterProvider"/>
/// onto every signal Rag.NET emits — not merely that a <see cref="Meter"/> object with the right
/// name exists somewhere, but that the built providers export what is measured on it. Reading
/// what the in-memory exporters actually received is the only way to distinguish "registered" from
/// "a name that happens to match".
/// </summary>
[Collection("Telemetry")]
public sealed class RagNetInstrumentationExtensionsTests
{
    [Fact]
    public void AddRagNetInstrumentation_RegistersTheSharedSourceAndBothMeters()
    {
        var exportedActivities = new List<Activity>();
        var exportedMetrics = new List<Metric>();
        var services = new ServiceCollection();

        services.AddRagNetInstrumentation()
            .WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities))
            .WithMetrics(metrics => metrics.AddInMemoryExporter(exportedMetrics));

        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        // Fresh instances sharing the well-known names — exactly what core's RagTelemetry and
        // Rag.NET.Evaluation's ShadowTelemetry do from their own assemblies. A listener matches on
        // ActivitySource.Name/Meter.Name, not on object identity: see
        // RagTelemetrySourceCrossAssemblyTests in Rag.NET.Tests for the same mechanism proven
        // across two real assemblies.
        using var coreSource = new ActivitySource(RagNetInstrumentationExtensions.SourceName);
        using (coreSource.StartActivity("telemetry-package.probe"))
        {
            // Started and stopped: only its arrival at the in-memory exporter matters.
        }

        using var coreMeter = new Meter(RagNetInstrumentationExtensions.SourceName);
        coreMeter.CreateCounter<long>("telemetry-package.probe.core").Add(1);

        using var evaluationMeter = new Meter(RagNetInstrumentationExtensions.EvaluationMeterName);
        evaluationMeter.CreateCounter<long>("telemetry-package.probe.evaluation").Add(1);

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        Assert.Contains(exportedActivities, activity =>
            string.Equals(activity.OperationName, "telemetry-package.probe", StringComparison.Ordinal));

        // The assertion the package exists for: both meter names reached the exporter, not just
        // the one docs/reference/opentelemetry.md's quick-setup snippet names.
        Assert.Contains(exportedMetrics, metric =>
            string.Equals(metric.MeterName, RagNetInstrumentationExtensions.SourceName, StringComparison.Ordinal));
        Assert.Contains(exportedMetrics, metric =>
            string.Equals(metric.MeterName, RagNetInstrumentationExtensions.EvaluationMeterName, StringComparison.Ordinal));
    }

    [Fact]
    public void AddRagNetInstrumentation_SetsTheDistroResourceAttributes()
    {
        var services = new ServiceCollection();

        services.AddRagNetInstrumentation();

        using var provider = services.BuildServiceProvider();
        var resource = provider.GetRequiredService<TracerProvider>().GetResource();

        Assert.Contains(
            resource.Attributes,
            attribute => string.Equals(attribute.Key, "telemetry.distro.name", StringComparison.Ordinal) &&
                string.Equals((string?)attribute.Value, "Rag.NET", StringComparison.Ordinal));
        Assert.Contains(
            resource.Attributes,
            attribute => string.Equals(attribute.Key, "telemetry.distro.version", StringComparison.Ordinal));
    }
}
