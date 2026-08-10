using System.Diagnostics;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// What retrieval returned is half of every trace, so what these tests pin is that it is filed under
/// the ambient activity's trace id — and that a pipeline with no ambient activity keeps working.
/// </summary>
public sealed class DiagnosticsRetrievalBehaviorTests
{
    [Fact]
    public async Task RetrievedChunks_AreRecordedUnderTheCurrentActivitysTraceId()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var behavior = new DiagnosticsRetrievalBehavior(collector);

        using var activity = StartActivity();
        var traceId = activity.TraceId.ToHexString();

        var results = await behavior.HandleAsync(
            ContextFor("who approved this"),
            TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(
                [ResultFor("doc-a", 0, 0.91), ResultFor("doc-b", 3, 0.42)]));

        var trace = collector.Current(traceId);
        Assert.NotNull(trace);

        // Structure, under default options: which chunks came back and what they scored.
        string[] expectedDocuments = ["doc-a", "doc-b"];
        Assert.Equal(expectedDocuments, trace.Chunks.Select(c => c.DocumentId), StringComparer.Ordinal);
        Assert.Equal(3, trace.Chunks[1].ChunkIndex);
        Assert.Equal(0.91, trace.Chunks[0].Score);
        Assert.NotEmpty(trace.QueryHash);

        // The behavior is a pass-through: the pipeline gets exactly what the next stage produced.
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task WithContentCaptureOn_TheQueryAndTheChunkTextAreBothRecorded()
    {
        var options = new RagTraceOptions { CaptureQueryText = true, CaptureChunkText = true };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);
        var behavior = new DiagnosticsRetrievalBehavior(collector);

        using var activity = StartActivity();

        await behavior.HandleAsync(
            ContextFor("who approved this"),
            TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([ResultFor("doc-a", 0, 0.91)]));

        var trace = collector.Current(activity.TraceId.ToHexString());
        Assert.NotNull(trace);
        Assert.Equal("who approved this", trace.Query);
        Assert.Equal("body of doc-a", Assert.Single(trace.Chunks).Text);
    }

    [Fact]
    public async Task WithNoAmbientActivity_NothingIsCapturedAndTheResultsStillFlow()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var behavior = new DiagnosticsRetrievalBehavior(collector);

        Assert.Null(Activity.Current);

        var results = await behavior.HandleAsync(
            ContextFor("q"),
            TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([ResultFor("doc-a", 0, 0.5)]));

        // Running without a listener subscribed is a normal way to run. It must be a silent no-op,
        // not a crash and not a fabricated id that fills the buffer with one-fragment traces.
        Assert.Single(results);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task WhenCaptureThrows_TheRetrievalResultsAreStillReturned()
    {
        var behavior = new DiagnosticsRetrievalBehavior(new ThrowingTraceCollector());

        using var activity = StartActivity();

        var results = await behavior.HandleAsync(
            ContextFor("q"),
            TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([ResultFor("doc-a", 0, 0.5)]));

        // A debugger that breaks the pipeline it observes is worse than no debugger.
        Assert.Single(results);
    }

    /// <summary>Starts a W3C activity so <c>Activity.Current</c> has a trace id to join on.</summary>
    /// <returns>The started activity; dispose it to restore the previous ambient activity.</returns>
    private static Activity StartActivity()
    {
        var activity = new Activity("test");
        activity.Start();

        return activity;
    }

    private static RetrievalContext ContextFor(string query) => new()
    {
        Query = query,
        Options = new RetrievalOptions(),
    };

    private static SearchResult ResultFor(string documentId, int chunkIndex, double score) => new()
    {
        Score = score,
        Chunk = new TextChunk
        {
            Text = $"body of {documentId}",
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
        },
    };
}
