using Xunit;

namespace Rag.NET.Tests.Telemetry;

/// <summary>
/// Serializes execution of all telemetry test classes.
///
/// Rationale: <c>RagTelemetry.ActivitySource</c> and <c>RagTelemetry.Meter</c> are process-global
/// statics. Any test anywhere in the suite that exercises <c>PipelineIngestor</c>,
/// <c>PipelineRetriever</c>, or <c>ChatAnswerEngine</c> emits activities and measurements on them.
/// Tests in this collection subscribe an <c>ActivityListener</c> / <c>MetricCollector</c> to those
/// globals and would otherwise see emissions leak in from other parallel test classes, producing
/// flakes like "Expected: 1, Actual: 3" on metric counters and "Expected: '0' / Actual: '1'" when
/// <c>FirstOrDefault</c> picks up a span from a sibling test.
///
/// Disabling parallelization WITHIN this collection removes intra-telemetry races. Cross-collection
/// pollution (other test classes running in parallel) is handled per-test by filtering activities
/// on <c>StartTimeUtc</c> relative to a pre-act timestamp and by diffing <c>MetricCollector</c>
/// snapshots against a baseline captured before the act.
/// </summary>
[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection
{
}
