using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

[Collection("Telemetry")]
public class RetrieveTelemetryTests
{
    private static (ConcurrentBag<Activity> activities, ActivityListener listener) CreateListener()
    {
        var activities = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, RagTelemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    private static PipelineRetriever CreateSut(
        Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>? pipeline = null) =>
        new()
        {
            Pipeline = pipeline ?? new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
                (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([])),
        };

    [Fact]
    public async Task RetrieveAsync_EmitsRetrieveSpan()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var retriever = CreateSut();

        // Start a parent activity so the span emitted by THIS test inherits our TraceId
        // (ActivitySource.StartActivity picks up Activity.Current via AsyncLocal). Filtering
        // by TraceId deterministically excludes spans from concurrently running test classes
        // that hit the same global ActivitySource.
        using var parent = new Activity("test-parent").Start();

        var __ = await retriever.RetrieveAsync("what is RAG?", cancellationToken: TestContext.Current.CancellationToken);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.retrieve", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.NotNull(span.GetTagItem("query.hash")); // 8-char hex SHA-256 prefix — don't assert exact value
        Assert.NotNull(span.GetTagItem("top.k"));
        Assert.Equal("0", span.GetTagItem("result.count")?.ToString()); // empty result from fake store
    }

    /// <summary>
    /// The span tag and the log scope are the query hash's only two readers, and they have
    /// independent lifetimes — a listener without a logger, a logger without a listener, or
    /// both. Because the hash is now computed only when one of them exists, the case where
    /// both do is the one that pins the remaining requirement: they share a single computed
    /// value rather than each hashing the query for themselves.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_WithListenerAndLogger_SpanTagAndLogScopeCarryTheSameHash()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var logger = new FakeLogger<PipelineRetriever>();
        var pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>((ctx, _) =>
        {
            ScopeProbeLog.Emit(ctx.Logger);
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        });
        var retriever = new PipelineRetriever { Pipeline = pipeline, Logger = logger };
        using var parent = new Activity("test-parent").Start();

        var __ = await retriever.RetrieveAsync("what is RAG?",
            cancellationToken: TestContext.Current.CancellationToken);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.retrieve", StringComparison.Ordinal));
        Assert.NotNull(span);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        var scopeState = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object>>>(
            Assert.Single(record.Scopes));
        var scopeHash = Assert.Single(scopeState).Value;

        Assert.Equal(PipelineRetriever.HashQuery("what is RAG?"), scopeHash);
        Assert.Equal(scopeHash, span.GetTagItem("query.hash"));
    }

    [Fact]
    public async Task RetrieveAsync_OnError_SetsSpanStatusAndIncrementsCounter()
    {
        using var errorsCollector = new MetricCollector<long>(RagTelemetry.Meter, "ragnet.retrieve.errors");
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var throwingPipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
            (_, _) => throw new InvalidOperationException("vector store unavailable"));
        var retriever = CreateSut(throwingPipeline);

        // Baseline any measurements accumulated by parallel test classes between the collector's
        // construction and this test's act — assert on the delta instead of the absolute sum.
        var baseline = errorsCollector.GetMeasurementSnapshot().Sum(m => m.Value);
        // TraceId-parent filtering: deterministic span discrimination under test parallelism.
        using var parent = new Activity("test-parent").Start();

        var result = await retriever.RetrieveAsync("failing query",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .FirstOrDefault(a =>
                string.Equals(a.OperationName, "ragnet.retrieve", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal(ActivityStatusCode.Error, span.Status);

        // >= 1 rather than == 1: the counter is process-global and untagged, so a parallel
        // test class erroring through RetrieveAsync during our act can also increment it.
        var delta = errorsCollector.GetMeasurementSnapshot().Sum(m => m.Value) - baseline;
        Assert.True(delta >= 1, $"expected at least one retrieve error recorded, delta={delta}");
    }

    [Fact]
    public async Task RetrieveAsync_RecordsRetrieveDuration()
    {
        using var durationCollector = new MetricCollector<double>(RagTelemetry.Meter, "ragnet.retrieve.duration");

        var retriever = CreateSut();

        // Baseline measurement count before the act. Other parallel tests that call RetrieveAsync
        // through unrelated code paths also record on this global histogram.
        var baselineCount = durationCollector.GetMeasurementSnapshot().Count;

        var _ = await retriever.RetrieveAsync("what is RAG?",
            cancellationToken: TestContext.Current.CancellationToken);

        var measurements = durationCollector.GetMeasurementSnapshot();
        // At least one new measurement recorded by this test. Using >= to tolerate parallel
        // test classes that also call RetrieveAsync concurrently (process-global Meter).
        Assert.True(measurements.Count > baselineCount,
            $"expected new measurement after RetrieveAsync (baseline={baselineCount}, actual={measurements.Count})");
        // All durations (including ours) are stopwatch elapsed values, so non-negative.
        Assert.All(measurements, m => Assert.True(m.Value >= 0));
    }
}
