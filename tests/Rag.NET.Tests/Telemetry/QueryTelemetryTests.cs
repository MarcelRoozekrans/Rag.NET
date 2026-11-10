using System.Collections.Concurrent;
using System.Diagnostics;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Telemetry;

/// <summary>
/// The <c>ragnet.query</c> span that <see cref="RagPipeline"/> opens around an ask. When the ask
/// fails in the pipeline itself, before the retriever's or answer engine's own spans could record
/// it, the query span has to carry the error.
/// </summary>
[Collection("Telemetry")]
public class QueryTelemetryTests
{
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();
    private readonly RagPipeline _sut;

    public QueryTelemetryTests()
    {
        _sut = new RagPipeline(_retriever, Substitute.For<IIngestor>(), Substitute.For<IAnswerEngine>());
    }

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

    private static Activity QuerySpan(ConcurrentBag<Activity> activities, Activity parent) =>
        Assert.Single(activities, a =>
            a.TraceId == parent.TraceId && string.Equals(a.OperationName, "ragnet.query", StringComparison.Ordinal));

    private static RagOptions ConflictingOptions() => new() { TopK = 5, Retrieval = new RetrievalOptions() };

    private void ArrangeRetrievalFailure() =>
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                new RagError.StorageFailed(new InvalidOperationException("store down")))));

    [Fact]
    public async Task AskAsync_ConflictingRetrievalOptions_MarksQuerySpanAsError()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;
        using var parent = new Activity("test-parent").Start();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.AskAsync("q", ConflictingOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(ActivityStatusCode.Error, QuerySpan(activities, parent).Status);
    }

    [Fact]
    public async Task AskStreamingAsync_ConflictingRetrievalOptions_MarksQuerySpanAsError()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;
        using var parent = new Activity("test-parent").Start();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var __ in _sut.AskStreamingAsync("q", ConflictingOptions(), TestContext.Current.CancellationToken)) { }
        });

        Assert.Equal(ActivityStatusCode.Error, QuerySpan(activities, parent).Status);
    }

    [Fact]
    public async Task AskAsync_RetrievalFailure_MarksQuerySpanAsError()
    {
        ArrangeRetrievalFailure();
        var (activities, listener) = CreateListener();
        using var _ = listener;
        using var parent = new Activity("test-parent").Start();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AskAsync("q", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ActivityStatusCode.Error, QuerySpan(activities, parent).Status);
    }

    [Fact]
    public async Task AskStreamingAsync_RetrievalFailure_MarksQuerySpanAsError()
    {
        ArrangeRetrievalFailure();
        var (activities, listener) = CreateListener();
        using var _ = listener;
        using var parent = new Activity("test-parent").Start();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var __ in _sut.AskStreamingAsync("q", cancellationToken: TestContext.Current.CancellationToken)) { }
        });

        Assert.Equal(ActivityStatusCode.Error, QuerySpan(activities, parent).Status);
    }
}
