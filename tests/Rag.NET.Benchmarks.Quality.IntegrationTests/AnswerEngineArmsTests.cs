using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;
using Xunit.Sdk;

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
        var failures = new AnswerEngineArms.FailureLog();
        var engine = AnswerEngineArms.Create(AnswerArm.ChatEngine, client, retriever: null, failures);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
        failures.AssertNoExceptionWasSwallowed();
    }

    [Fact]
    public async Task MapReduce_MakesOneCallPerChunkPlusOneReduce()
    {
        var client = new CountingChatClient();
        var failures = new AnswerEngineArms.FailureLog();
        var engine = AnswerEngineArms.Create(AnswerArm.MapReduce, client, retriever: null, failures);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ContextChunks + 1, client.Calls);
        failures.AssertNoExceptionWasSwallowed();
    }

    [Fact]
    public async Task Refine_MakesOneCallPerChunk()
    {
        var client = new CountingChatClient();
        var failures = new AnswerEngineArms.FailureLog();
        var engine = AnswerEngineArms.Create(AnswerArm.Refine, client, retriever: null, failures);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ContextChunks, client.Calls);
        failures.AssertNoExceptionWasSwallowed();
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
    /// from a fixed fake answer, not the corpus's real per-query FLARE cost — which is why the
    /// assertion below is <c>&gt;= 1</c> rather than the <c>&lt;= 30</c> the design first promised:
    /// 30 is <c>MaxSentences</c>'s default times the fake's refusal to finish, and pinning it would
    /// pin the library's default and the fake's canned answer rather than this arm's claim. The
    /// claim is the second half of the pair, and it is asserted exactly: zero retrievals.
    /// <para>
    /// <c>failures.SwallowedExceptions</c> is asserted zero, but the total warning count is not:
    /// the fake's "an answer." does not parse as a confidence score, so
    /// <c>SelfAssessmentConfidenceScorer</c> logs <c>ConfidenceScoreUnparsable</c> on every
    /// self-assessment. That warning carries no exception — it is the model's output, not a fault —
    /// which is the distinction <see cref="AnswerEngineArms.FailureLog"/> draws.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FlareFixed_NeverRetrieves()
    {
        var client = new CountingChatClient();
        var failures = new AnswerEngineArms.FailureLog();
        var retriever = new AnswerEngineArms.UnreachableRetriever();
        var engine = AnswerEngineArms.Create(AnswerArm.FlareFixed, client, retriever, failures);

        var response = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.True(client.Calls >= 1, "flarefixed made no LLM call at all.");
        Assert.False(
            retriever.WasCalled,
            "flarefixed's lookahead retrieval fired despite MaxRetrievals = 0 — the arm is no " +
            "longer holding retrieval fixed and its comparison against mapreduce/refine is invalid.");
        failures.AssertNoExceptionWasSwallowed();
        Assert.True(
            failures.Count > 0,
            "the fake's unparsable confidence replies were not counted at all, so the counting " +
            "logger is not reaching the scorer: " + failures.Describe());
    }

    [Fact]
    public void Flare_RequiresARetriever()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentNullException>(
            () => AnswerEngineArms.Create(
                AnswerArm.Flare, client, retriever: null, new AnswerEngineArms.FailureLog()));
    }

    /// <summary>
    /// <c>flarefixed</c> requires one too, and for a sharper reason than <c>flare</c> does: the
    /// factory used to substitute <c>?? new UnreachableRetriever()</c> for a missing one, which
    /// built a stub nobody held a reference to. The arm's only guarantee is a flag on that
    /// instance, so a stub the factory made and dropped was a guarantee that could not be read —
    /// the precise unobservability that took three rounds to close. Refusing the call is what keeps
    /// the flag on an object the caller can assert against.
    /// </summary>
    [Fact]
    public void FlareFixed_RequiresARetriever()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentNullException>(
            () => AnswerEngineArms.Create(
                AnswerArm.FlareFixed, client, retriever: null, new AnswerEngineArms.FailureLog()));
    }

    [Fact]
    public void Create_RejectsAnArmItDoesNotBuild()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => AnswerEngineArms.Create(
                AnswerArm.Dense, client, retriever: null, new AnswerEngineArms.FailureLog()));
    }

    /// <summary>
    /// A failure an engine swallows is counted, and the gate over that counter fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this reproduces.</b> <c>MapReduceAnswerEngine.MapOneAsync</c> catches every
    /// non-cancellation exception, logs it and returns <see langword="null"/>; the reduce step then
    /// synthesises whatever partials survived and the caller gets an ordinary-looking answer. On a
    /// replay run the exception in question is a missing answer-cache entry, so the arm answers
    /// from fewer chunks and reports a lower accuracy that is indistinguishable from a result.
    /// </para>
    /// <para>
    /// <b>Why the call-shape gate cannot catch it.</b> The client below is called
    /// <see cref="ContextChunks"/> + 1 times exactly as a healthy run would be — the counter in the
    /// harness increments before the request is forwarded, so a call that throws is still a call it
    /// counted. Only the log distinguishes the two runs, which is why it must not be discarded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MapReduce_SwallowedMapFailure_IsCountedAndFailsTheGate()
    {
        var client = new MapFailingChatClient();
        var failures = new AnswerEngineArms.FailureLog();
        var engine = AnswerEngineArms.Create(AnswerArm.MapReduce, client, retriever: null, failures);

        var response = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response.Answer);
        Assert.Equal(ContextChunks + 1, client.Calls);
        Assert.Equal(ContextChunks, failures.SwallowedExceptions);
        Assert.Contains("MapReduceAnswerEngine", failures.Describe(), StringComparison.Ordinal);

        var thrown = Assert.ThrowsAny<XunitException>(failures.AssertNoExceptionWasSwallowed);
        Assert.Contains("ENGINE FAILURES SWALLOWED", thrown.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// Throws on every map call and answers the reduce call, standing in for the one failure mode a
    /// replay run actually produces: a missing answer-cache entry inside a per-chunk call.
    /// </summary>
    /// <remarks>
    /// The two call kinds are told apart by <see cref="ReduceMarker"/>, a fragment of
    /// <c>MapReduceAnswerEngine</c>'s default reduce prompt. If that prompt is reworded this test
    /// fails on the marker rather than quietly throwing from the reduce call as well, which is the
    /// louder of the two failures.
    /// </remarks>
    private sealed class MapFailingChatClient : IChatClient
    {
        private const string ReduceMarker = "Synthesize the following partial answers";

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            _ = Interlocked.Increment(ref _calls);

            foreach (var message in messages)
            {
                if (message.Text.Contains(ReduceMarker, StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer.")));
                }
            }

            throw new InvalidOperationException(
                "no cached answer for this prompt — the run is a replay and this entry is missing.");
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
