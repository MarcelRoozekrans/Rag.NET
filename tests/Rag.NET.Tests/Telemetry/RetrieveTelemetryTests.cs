using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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

        var cutoff = DateTime.UtcNow.AddMilliseconds(-50);

        var __ = await retriever.RetrieveAsync("what is RAG?", cancellationToken: TestContext.Current.CancellationToken);

        var span = activities
            .Where(a => a.StartTimeUtc >= cutoff)
            .FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.retrieve", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.NotNull(span.GetTagItem("query.hash")); // 8-char hex SHA-256 prefix — don't assert exact value
        Assert.NotNull(span.GetTagItem("top_k"));
        Assert.Equal("0", span.GetTagItem("result.count")?.ToString()); // empty result from fake store
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
        var cutoff = DateTime.UtcNow.AddMilliseconds(-50);

        var result = await retriever.RetrieveAsync("failing query",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);

        var span = activities
            .Where(a => a.StartTimeUtc >= cutoff)
            .FirstOrDefault(a =>
                string.Equals(a.OperationName, "ragnet.retrieve", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal(ActivityStatusCode.Error, span.Status);

        Assert.Equal(1, errorsCollector.GetMeasurementSnapshot().Sum(m => m.Value) - baseline);
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
