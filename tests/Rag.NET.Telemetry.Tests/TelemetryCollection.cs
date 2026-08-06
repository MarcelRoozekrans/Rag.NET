using Xunit;

namespace Rag.NET.Telemetry.Tests;

/// <summary>
/// Serializes execution of every test class in this assembly that builds a
/// <c>TracerProvider</c>/<c>MeterProvider</c> and emits on the shared <c>"Rag.NET"</c> or
/// <c>"Rag.NET.Evaluation"</c> names.
/// </summary>
/// <remarks>
/// The same rationale as core's <c>Rag.NET.Tests.Telemetry.TelemetryCollection</c> and
/// <c>Rag.NET.Evaluation.Tests.Shadow.ShadowTelemetryCollection</c>: a xUnit collection
/// definition is scoped to its own assembly, so this project needs its own copy rather than
/// sharing theirs. <see cref="System.Diagnostics.ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> instances that share a name are heard by every
/// listener subscribed to it, process-wide — two test classes each building their own provider on
/// <c>"Rag.NET"</c> in parallel would otherwise see each other's activities and measurements land
/// in their own in-memory exporter.
/// </remarks>
[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection
{
}
