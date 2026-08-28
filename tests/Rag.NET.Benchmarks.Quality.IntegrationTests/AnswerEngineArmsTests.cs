using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Pins each engine arm's <b>call shape</b> — how many LLM calls it makes for a top-6 context.
/// <para>
/// This is the cost model for a sweep of 2,556 queries, checked with a fake client instead of a
/// bill. If <c>mapreduce</c> ever makes one call it is not doing map-reduce; if it makes forty, the
/// sweep is mispriced. Phase 6.2.1's RAPTOR plan had no equivalent check, which is how an
/// eight-hour estimate built on the wrong workload's rate survived into a plan.
/// </para>
/// </summary>
public sealed class AnswerEngineArmsTests
{
    private const int ContextChunks = 6;

    [Fact]
    public async Task ChatEngine_MakesExactlyOneCall()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.ChatEngine, client, retriever: null);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task MapReduce_MakesOneCallPerChunkPlusOneReduce()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.MapReduce, client, retriever: null);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ContextChunks + 1, client.Calls);
    }

    [Fact]
    public async Task Refine_MakesOneCallPerChunk()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.Refine, client, retriever: null);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ContextChunks, client.Calls);
    }

    /// <summary>
    /// The arm's defining claim, asserted on a recorded flag rather than on the retriever having
    /// thrown: <c>FlareAnswerEngine.TryLookaheadRetrievalAsync</c> catches and swallows every
    /// exception the retriever raises, so a throw alone proves nothing — the engine would keep
    /// running and this test would pass even while lookahead had fired. What actually proves
    /// lookahead stayed off at <c>MaxRetrievals = 0</c> is that
    /// <see cref="AnswerEngineArms.UnreachableRetriever.WasCalled"/>, set before the throw and
    /// therefore unaffected by the swallow, is still <see langword="false"/> afterward.
    /// </summary>
    /// <remarks>
    /// Observed <c>client.Calls</c> against the fake client in this file: 30 — <c>FlareOptions</c>'s
    /// default <c>MaxSentences</c> of 15, each sentence costing two calls (one to generate it, one for
    /// <c>SelfAssessmentConfidenceScorer</c> to self-assess it), because the fake's fixed "an answer."
    /// reply never emits the done-token so the loop never stops early. This is an upper-bound signal
    /// from a fixed fake answer, not the corpus's real per-query FLARE cost.
    /// </remarks>
    [Fact]
    public async Task FlareFixed_NeverRetrieves()
    {
        var client = new CountingChatClient();
        var retriever = new AnswerEngineArms.UnreachableRetriever();
        var engine = AnswerEngineArms.Create(AnswerArm.FlareFixed, client, retriever);

        var response = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.True(client.Calls >= 1, "flarefixed made no LLM call at all.");
        Assert.False(
            retriever.WasCalled,
            "flarefixed's lookahead retrieval fired despite MaxRetrievals = 0 — the arm is no " +
            "longer holding retrieval fixed and its comparison against mapreduce/refine is invalid.");
    }

    [Fact]
    public void Flare_RequiresARetriever()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentNullException>(
            () => AnswerEngineArms.Create(AnswerArm.Flare, client, retriever: null));
    }

    [Fact]
    public void Create_RejectsAnArmItDoesNotBuild()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => AnswerEngineArms.Create(AnswerArm.Dense, client, retriever: null));
    }

    private static IReadOnlyList<SearchResult> Sources()
    {
        var sources = new SearchResult[ContextChunks];
        for (var i = 0; i < ContextChunks; i++)
        {
            sources[i] = new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = FormattableString.Invariant($"context chunk {i}"),
                    DocumentId = new DocumentId(FormattableString.Invariant($"doc-{i}")),
                    ChunkIndex = 0,
                },
                Score = 1.0 - (i * 0.01),
            };
        }

        return sources;
    }

    /// <summary>Counts calls and returns a short fixed answer, so no engine loops on empty output.</summary>
    private sealed class CountingChatClient : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The arms use AskAsync, not streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
