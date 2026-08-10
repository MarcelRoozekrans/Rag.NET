using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The join and the commit point, exercised through a real <see cref="RagPipeline"/> rather than
/// through hand-nested activities.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that would have caught the two defects Part B left behind: nothing called
/// <c>Commit</c>, so no trace ever reached the ring buffer at all, and the two halves of a query only
/// correlated when the host happened to supply an ambient activity. Both are invisible to a test that
/// nests spans by hand or reads <see cref="ITraceCollector.Current"/> — the first because
/// <c>Current</c> reads a trace that was never committed, the second because hand-nesting supplies
/// exactly the shared parent the pipeline did not have.
/// </para>
/// <para>
/// So the pipeline is assembled for real, from <c>PipelineRetriever</c> (which opens
/// <c>ragnet.retrieve</c>) and <c>ChatAnswerEngine</c> (which opens <c>ragnet.ask</c>), with only the
/// vector store and the chat client faked. Nothing here starts an activity by hand except where the
/// host's own ambient activity is the thing under test.
/// </para>
/// </remarks>
[Collection(ActivityCollection.Name)]
public sealed class TraceCommitTests
{
    private const string Query = "who approved this";
    private const string Answer = "because Bob did";

    [Fact]
    public async Task AnAsk_WithNoAmbientActivity_CommitsOneTraceHoldingBothHalvesOfTheQuery()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildPipeline(collector);

        // The case the design asserted rather than checked. With no ambient activity, ragnet.retrieve
        // and ragnet.ask used to be created as separate roots with different trace ids, so a console
        // app or a worker could not assemble a trace at all.
        Assert.Null(Activity.Current);

        var response = await pipeline.AskAsync(Query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Answer, response.Answer);

        var trace = Assert.Single(buffer.Snapshot());

        // One trace, holding what each of the four capture seams contributed: the behavior's query and
        // chunks, the prompt observer's prompt, the decorator's answer.
        Assert.Equal(Query, trace.Query);
        Assert.Equal("doc-a", Assert.Single(trace.Chunks).DocumentId);
        Assert.Contains(Query, trace.Prompt, StringComparison.Ordinal);
        Assert.Equal(Answer, trace.Answer);

        string[] expectedStages = ["ragnet.retrieve", "ragnet.ask", "ragnet.query"];
        Assert.Equal(expectedStages, trace.Stages.Select(s => s.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ARetrieveOnlyCall_CommitsItsOwnTraceAndLeavesNothingInFlight()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildPipeline(collector);

        var result = await pipeline.RetrieveAsync(Query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        // A retrieval with no ask around it is a whole execution, so it commits on its own rather
        // than waiting for an answer that is not coming. Expiring against the in-flight ceiling
        // instead would have meant a retrieve-only deployment never produced a readable trace.
        var trace = Assert.Single(buffer.Snapshot());

        Assert.Single(trace.Chunks);
        Assert.Null(trace.Answer);

        // ragnet.query wraps a retrieve-only call too, so every public entry point closes its own
        // trace at one identifiable moment — see AFanOutRetrieveOnlyCall_* for the case that made
        // leaving it out wrong rather than merely redundant.
        string[] expectedStages = ["ragnet.retrieve", "ragnet.query"];
        Assert.Equal(expectedStages, trace.Stages.Select(s => s.Name), StringComparer.Ordinal);

        // And nothing is left half-assembled: the ceiling bounds a leak, it does not excuse one.
        Assert.Null(collector.Current(trace.TraceId));
    }

    [Fact]
    public async Task AFanOutRetrieveOnlyCall_CommitsOneTraceHoldingEveryRetrieval()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildFanOutPipeline(collector);

        Assert.Null(Activity.Current);

        var result = await pipeline.RetrieveAsync(Query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        // DeepResearchRetriever decorates IRetriever globally and calls the inner retriever once per
        // sub-question, so one IRagPipeline.RetrieveAsync opens ragnet.retrieve three times: the
        // primary retrieval plus two sub-queries. Without a span around the call each of those was
        // the outermost pipeline span of its own execution — three separate roots here, and so three
        // committed traces holding one stage each.
        var trace = Assert.Single(buffer.Snapshot());

        string[] expectedStages = ["ragnet.retrieve", "ragnet.retrieve", "ragnet.retrieve", "ragnet.query"];
        Assert.Equal(expectedStages, trace.Stages.Select(s => s.Name), StringComparer.Ordinal);

        // Every retrieval's chunks are in the one trace rather than scattered across three.
        string[] expectedChunks = [Query, "sub-question 1", "sub-question 2"];
        Assert.Equal(expectedChunks, trace.Chunks.Select(c => c.DocumentId), StringComparer.Ordinal);

        // The headline question is the one the user asked, not the last sub-query the fan-out
        // generated. Chunks accumulate across retrievals; the query does not. Getting this wrong
        // shows a developer a question nobody typed, in the one artefact they opened because they
        // no longer trust the pipeline — and makes two identical questions hash differently, which
        // is the only field the list route tells traces apart by.
        Assert.Equal(Query, trace.Query);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Query))),
            trace.QueryHash);

        Assert.Null(collector.Current(trace.TraceId));
    }

    [Fact]
    public async Task AFanOutRetrieveOnlyCall_UnderAnAmbientActivity_CommitsOneAddressableTrace()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildFanOutPipeline(collector);

        // The worse half of the same defect. Under a request activity the fragments do not even get
        // distinct trace ids — all three carried the request's — so TryGet's newest-wins rule made
        // GET /ragnet/traces/{id} return the last sub-query's trace and left the primary retrieval's
        // silently unreachable.
        using var ambient = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").Start();

        var result = await pipeline.RetrieveAsync(Query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        var trace = Assert.Single(buffer.Snapshot());

        Assert.Equal(ambient.TraceId.ToHexString(), trace.TraceId);
        Assert.Equal(3, trace.Chunks.Count);
    }

    [Fact]
    public async Task AnAsk_UnderAnAmbientActivity_CommitsOneTraceUnderTheAmbientTraceId()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildPipeline(collector);

        // The ASP.NET shape: a request activity parents everything the pipeline opens. The commit rule
        // has to read the same here — ragnet.query's parent is not a pipeline span, so it is still the
        // outermost one and still the moment the query is over.
        using var ambient = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").Start();

        await pipeline.AskAsync(Query, cancellationToken: TestContext.Current.CancellationToken);

        var trace = Assert.Single(buffer.Snapshot());

        Assert.Equal(ambient.TraceId.ToHexString(), trace.TraceId);
        Assert.Equal(Answer, trace.Answer);
    }

    [Fact]
    public async Task TwoAsks_UnderOneAmbientActivity_CommitTwoTraces()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildPipeline(collector);

        using var ambient = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").Start();

        await pipeline.AskAsync(Query, cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.AskAsync(Query, cancellationToken: TestContext.Current.CancellationToken);

        // Two queries in one request share a trace id, because the trace id is the request's. Each
        // commit takes its own trace out of flight, so the second starts a fresh one rather than
        // appending to a trace that has already been read. TryGet's newest-wins rule is what makes the
        // pair addressable afterwards.
        Assert.Equal(2, buffer.Snapshot().Count);
    }

    [Fact]
    public async Task AStreamedAsk_CommitsOneTraceWithItsChunksAndStages()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildPipeline(collector);

        await foreach (var update in pipeline.AskStreamingAsync(
            Query, cancellationToken: TestContext.Current.CancellationToken))
        {
            // Consumed to the end, so the query span is stopped by the iterator's own dispose and the
            // trace commits. An abandoned enumeration commits when the enumerator is disposed instead.
            _ = update;
        }

        var trace = Assert.Single(buffer.Snapshot());

        string[] expectedStages = ["ragnet.retrieve", "ragnet.ask", "ragnet.query"];
        Assert.Equal(expectedStages, trace.Stages.Select(s => s.Name), StringComparer.Ordinal);
        Assert.Equal(Query, trace.Query);
        Assert.Single(trace.Chunks);

        // No answer, by design: buffering a stream to record it would change the memory profile of the
        // stream being observed, so the decorator passes the streamed overload straight through.
        Assert.Null(trace.Answer);

        // And no prompt, which is NOT by design — see
        // AStreamedAsk_UnderAnAmbientActivity_AlsoCapturesThePrompt for the boundary. ChatAnswerEngine
        // assembles the prompt after its first `yield return`, so the observer runs on the consumer's
        // execution context, where the activity the pipeline started inside its own iterator is not
        // ambient. With no ambient activity of the host's own there is no trace id to file it under.
        Assert.Null(trace.Prompt);
    }

    [Fact]
    public async Task AStreamedAsk_UnderAnAmbientActivity_AlsoCapturesThePrompt()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(EverythingCaptured(), buffer);
        using var listener = new StageActivityListener(collector);
        var pipeline = BuildPipeline(collector);

        // The host's ambient activity survives the trip back to the consumer, because it belongs to
        // the consumer's context rather than to the iterator's — and it carries the same trace id the
        // pipeline's spans descend from. So under ASP.NET, where a streamed answer is actually served,
        // the prompt does join. This test is what makes the null above a boundary rather than a bug
        // with no edge.
        using var ambient = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").Start();

        await foreach (var update in pipeline.AskStreamingAsync(
            Query, cancellationToken: TestContext.Current.CancellationToken))
        {
            _ = update;
        }

        var trace = Assert.Single(buffer.Snapshot());

        Assert.Equal(ambient.TraceId.ToHexString(), trace.TraceId);
        Assert.Contains(Query, trace.Prompt, StringComparison.Ordinal);
        Assert.Null(trace.Answer);
    }

    /// <summary>Options with every content flag on, so a missed field shows up as <see langword="null"/>.</summary>
    /// <returns>Fully-capturing options. Safe here: nothing in these tests is real content.</returns>
    private static RagTraceOptions EverythingCaptured() => new()
    {
        CaptureQueryText = true,
        CaptureChunkText = true,
        CapturePromptText = true,
        CaptureAnswerText = true,
    };

    /// <summary>Builds a real pipeline with only the store and the chat client faked.</summary>
    /// <param name="collector">The collector every capture seam records into.</param>
    /// <returns>A pipeline whose spans are the pipeline's own.</returns>
    private static RagPipeline BuildPipeline(ITraceCollector collector)
    {
        var behavior = new DiagnosticsRetrievalBehavior(collector);

        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> store =
            static (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([ResultFor("doc-a")]);

        var retriever = new PipelineRetriever
        {
            Pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
                (ctx, ct) => behavior.HandleAsync(ctx, ct, store)),
        };

        var engine = new ChatAnswerEngine(
            FakeChatClient(),
            promptObserver: new TracePromptObserver(collector));

        return new RagPipeline(
            retriever,
            Substitute.For<IIngestor>(),
            new DiagnosticsAnswerEngineDecorator(engine, collector));
    }

    /// <summary>Builds a retrieve-only pipeline whose retriever fans one query out into three.</summary>
    /// <param name="collector">The collector every capture seam records into.</param>
    /// <returns>
    /// A pipeline wrapped in <see cref="DeepResearchRetriever"/>, the shape <c>AddRagNet</c> produces
    /// whenever <c>DeepResearchOptions</c> is configured. No answer engine: the point is the
    /// retrieve-only path, which is where the fan-out had nothing above it to close the trace.
    /// </returns>
    private static RagPipeline BuildFanOutPipeline(ITraceCollector collector)
    {
        var behavior = new DiagnosticsRetrievalBehavior(collector);

        // Keyed on the query, so the three retrievals are distinguishable in the trace and
        // DeepResearchRetriever's deduplication does not collapse them into one.
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> store =
            static (ctx, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([ResultFor(ctx.Query)]);

        var inner = new PipelineRetriever
        {
            Pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
                (ctx, ct) => behavior.HandleAsync(ctx, ct, store)),
        };

        return new RagPipeline(
            new DeepResearchRetriever(inner, SufficiencyClient(), new DeepResearchOptions
            {
                // One pass, two sub-queries: three retrievals, which is enough to tell one trace from
                // N without making the expected stage list an exercise in counting.
                MaxDepth = 1,
                SubQueryCount = 2,
            }),
            Substitute.For<IIngestor>());
    }

    /// <summary>A chat client whose sufficiency verdict always asks for the same two sub-queries.</summary>
    /// <returns>The substitute.</returns>
    private static IChatClient SufficiencyClient()
    {
        var chatClient = Substitute.For<IChatClient>();

        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                """{"sufficient":false,"subQueries":["sub-question 1","sub-question 2"]}""")));

        return chatClient;
    }

    /// <summary>A chat client that answers the same thing on both the buffered and streamed paths.</summary>
    /// <returns>The substitute.</returns>
    private static IChatClient FakeChatClient()
    {
        var chatClient = Substitute.For<IChatClient>();

        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)));

        chatClient
            .GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => OneUpdate());

        return chatClient;
    }

    /// <summary>The streamed reply, as a single update.</summary>
    /// <returns>One <see cref="ChatResponseUpdate"/> carrying the whole answer.</returns>
    private static async IAsyncEnumerable<ChatResponseUpdate> OneUpdate()
    {
        yield return new ChatResponseUpdate { Contents = [new TextContent(Answer)] };

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static SearchResult ResultFor(string documentId) => new()
    {
        Score = 0.91,
        Chunk = new TextChunk
        {
            Text = $"body of {documentId}",
            DocumentId = new DocumentId(documentId),
            ChunkIndex = 0,
        },
    };
}
