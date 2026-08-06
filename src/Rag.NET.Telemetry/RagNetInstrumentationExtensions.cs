using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Rag.NET.Telemetry;

/// <summary>
/// Wires the OpenTelemetry SDK onto every signal Rag.NET actually emits.
/// </summary>
/// <remarks>
/// <para>
/// Core (<c>Rag.NET</c>) and its satellites emit spans and metrics through the in-box
/// <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>
/// APIs and take no OpenTelemetry SDK dependency themselves — see
/// <c>docs/reference/opentelemetry.md</c>. This package is where that SDK dependency lives
/// instead: <see cref="AddRagNetInstrumentation"/> registers the shared <c>"Rag.NET"</c>
/// <see cref="System.Diagnostics.ActivitySource"/>, <b>both</b> Rag.NET meters, and the resource
/// attributes that identify this instrumentation to a backend.
/// </para>
/// <para>
/// <b>The trap this closes:</b> a consumer who hand-wires <c>.AddMeter("Rag.NET")</c> — the
/// quick-setup snippet in <c>docs/reference/opentelemetry.md</c> — silently misses every counter
/// <c>ShadowTelemetry</c> publishes under the second, undocumented meter name
/// <c>"Rag.NET.Evaluation"</c> (<c>src/Rag.NET.Evaluation/Shadow/ShadowTelemetry.cs</c>). Calling
/// this method instead of hand-wiring both names is the whole reason the package exists.
/// </para>
/// </remarks>
public static class RagNetInstrumentationExtensions
{
    /// <summary>
    /// The name shared by core's <see cref="System.Diagnostics.ActivitySource"/> and its
    /// <see cref="System.Diagnostics.Metrics.Meter"/> — <c>src/Shared/RagTelemetrySource.cs</c>'s
    /// <c>RagTelemetrySource.Name</c>, restated here as this package's own constant because that
    /// type is internal to the assemblies that link it and this package links none of them (see
    /// the csproj comment).
    /// </summary>
    internal const string SourceName = "Rag.NET";

    /// <summary>
    /// The shadow-capture pipeline's own meter name — <c>ShadowTelemetry.MeterName</c>, internal
    /// to <c>Rag.NET.Evaluation</c> and documented nowhere else. This is the meter a consumer
    /// following only <c>docs/reference/opentelemetry.md</c>'s quick-setup snippet misses.
    /// </summary>
    internal const string EvaluationMeterName = "Rag.NET.Evaluation";

    /// <summary>
    /// The version stamped on the <c>telemetry.distro.*</c> resource attributes below — pinned
    /// independently of this package's own NuGet version, the same "1.0.0" schema pin
    /// <c>RagTelemetrySource.Version</c> uses for every linked <see cref="System.Diagnostics.ActivitySource"/>.
    /// </summary>
    internal const string DistroVersion = "1.0.0";

    /// <summary>
    /// Registers Rag.NET's tracing and metrics onto <paramref name="services"/>: the shared
    /// <c>"Rag.NET"</c> <see cref="System.Diagnostics.ActivitySource"/>, the <c>"Rag.NET"</c> and
    /// <c>"Rag.NET.Evaluation"</c> meters, and the <c>telemetry.distro.name</c> /
    /// <c>telemetry.distro.version</c> resource attributes — the OpenTelemetry Semantic
    /// Conventions' pair for naming the instrumentation distribution that configured a signal,
    /// as distinct from <c>telemetry.sdk.*</c> (the bare SDK, which the SDK sets itself) and
    /// <c>service.*</c> (the caller's own application identity, which this package must not set).
    /// </summary>
    /// <param name="services">The service collection to register OpenTelemetry onto.</param>
    /// <returns>
    /// The <see cref="OpenTelemetryBuilder"/>, so the caller can chain an exporter onto tracing
    /// and metrics — this package registers no exporter of its own; see
    /// <c>docs/reference/opentelemetry.md</c> for exporter examples.
    /// </returns>
    public static OpenTelemetryBuilder AddRagNetInstrumentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(
            [
                new KeyValuePair<string, object>("telemetry.distro.name", SourceName),
                new KeyValuePair<string, object>("telemetry.distro.version", DistroVersion),
            ]))
            .WithTracing(tracing => tracing.AddSource(SourceName))
            .WithMetrics(metrics => metrics
                .AddMeter(SourceName)
                .AddMeter(EvaluationMeterName));
    }
}
