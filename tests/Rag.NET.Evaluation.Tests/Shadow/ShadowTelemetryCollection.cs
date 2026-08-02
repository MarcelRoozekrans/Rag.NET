using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// Serializes every test class that makes the shadow pipeline emit measurements.
/// </summary>
/// <remarks>
/// The same rationale as core's <c>TelemetryCollection</c>: <c>ShadowTelemetry.Meter</c> is a
/// process-global static, so any test that enqueues, drops, persists or abandons a capture emits
/// on it. The telemetry tests subscribe a <c>MetricCollector</c> to that global and assert exact
/// deltas; a queue or consumer test running in parallel would land its own increments inside the
/// delta and flake the assertion. Membership is therefore not just the telemetry tests but every
/// shadow test class that emits.
/// </remarks>
[CollectionDefinition("ShadowTelemetry", DisableParallelization = true)]
public sealed class ShadowTelemetryCollection
{
}
