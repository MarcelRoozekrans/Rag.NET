using System.Diagnostics;
using Rag.NET.Abstractions;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The answer is the other half of a trace. These pin that it is recorded under the ambient trace id,
/// that the streamed path is left alone, and that neither path can be broken by capture.
/// </summary>
public sealed class DiagnosticsAnswerEngineDecoratorTests
{
    [Fact]
    public async Task TheGeneratedAnswer_IsRecordedUnderTheCurrentActivitysTraceId()
    {
        var options = new RagTraceOptions { CaptureAnswerText = true };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);
        var decorator = new DiagnosticsAnswerEngineDecorator(new StubAnswerEngine("the answer"), collector);

        using var activity = StartActivity();

        var response = await decorator.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        var trace = collector.Current(activity.TraceId.ToHexString());
        Assert.NotNull(trace);
        Assert.Equal("the answer", trace.Answer);

        // Pass-through: the caller gets the inner engine's response unchanged.
        Assert.Equal("the answer", response.Answer);
    }

    [Fact]
    public async Task WithDefaultOptions_TheAnswerIsNotRetainedButTheTraceStillExists()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var decorator = new DiagnosticsAnswerEngineDecorator(new StubAnswerEngine("hunter2"), collector);

        using var activity = StartActivity();

        await decorator.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        var trace = collector.Current(activity.TraceId.ToHexString());
        Assert.NotNull(trace);
        Assert.Null(trace.Answer);
    }

    [Fact]
    public async Task WithNoAmbientActivity_TheAnswerIsReturnedAndNothingIsCaptured()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var decorator = new DiagnosticsAnswerEngineDecorator(new StubAnswerEngine("the answer"), collector);

        Assert.Null(Activity.Current);

        var response = await decorator.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("the answer", response.Answer);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task WhenCaptureThrows_TheAnswerIsStillReturned()
    {
        var decorator = new DiagnosticsAnswerEngineDecorator(
            new StubAnswerEngine("the answer"),
            new ThrowingTraceCollector());

        using var activity = StartActivity();

        var response = await decorator.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("the answer", response.Answer);
    }

    [Fact]
    public async Task TheStreamedPath_IsPassedThroughAndRecordsNothing()
    {
        var options = new RagTraceOptions { CaptureAnswerText = true };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);
        var decorator = new DiagnosticsAnswerEngineDecorator(new StubAnswerEngine("streamed"), collector);

        using var activity = StartActivity();

        var deltas = new List<string>();

        await foreach (var update in decorator.AskStreamingAsync(
            "q", [], cancellationToken: TestContext.Current.CancellationToken))
        {
            if (update.TextDelta is not null)
                deltas.Add(update.TextDelta);
        }

        // Buffering a streamed answer to record it would change the memory profile of the stream
        // being observed, so the streaming overload records nothing at all — the prompt seam inside
        // ChatAnswerEngine is what covers streamed executions.
        Assert.Equal(["streamed"], deltas);
        Assert.Null(collector.Current(activity.TraceId.ToHexString()));
    }

    /// <summary>Starts a W3C activity so <c>Activity.Current</c> has a trace id to join on.</summary>
    /// <returns>The started activity; dispose it to restore the previous ambient activity.</returns>
    private static Activity StartActivity()
    {
        var activity = new Activity("test");
        activity.Start();

        return activity;
    }

    /// <summary>An answer engine that returns one fixed answer on both paths.</summary>
    /// <param name="answer">What to answer.</param>
    private sealed class StubAnswerEngine(string answer) : IAnswerEngine
    {
        public Task<RagResponse> AskAsync(
            string query,
            IReadOnlyList<SearchResult> sources,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RagResponse { Answer = answer, Sources = sources });

        public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            IReadOnlyList<SearchResult> sources,
            RagOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new RagStreamingUpdate { TextDelta = answer };
        }
    }
}
