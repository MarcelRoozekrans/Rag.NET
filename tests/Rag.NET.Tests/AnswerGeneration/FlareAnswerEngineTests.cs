using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.AnswerGeneration;

public class FlareAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();
    private readonly FakeScorer _scorer = new();

    /// <summary>
    /// Deterministic <see cref="IConfidenceScorer"/> fake — NSubstitute's <c>Returns</c> on
    /// <see cref="ValueTask{T}"/> trips EPS06 (hidden struct copy), so scores are scripted here.
    /// </summary>
    private sealed class FakeScorer : IConfidenceScorer
    {
        private readonly Queue<double> _scores = new();
        public int Calls { get; private set; }
        public Exception? ThrowThis { get; set; }

        public void Script(params double[] scores)
        {
            foreach (var s in scores) _scores.Enqueue(s);
        }

        public ValueTask<double> ScoreAsync(
            string sentence, string partialAnswer, IReadOnlyList<SearchResult> context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ThrowThis is not null) throw ThrowThis;
            // Last scripted score repeats once the queue drains (mirrors NSubstitute semantics).
            return ValueTask.FromResult(_scores.Count > 1 ? _scores.Dequeue() : _scores.Peek());
        }
    }

    private FlareAnswerEngine CreateSut(FlareOptions? options = null) =>
        new(_chatClient, _retriever, _scorer, options ?? new FlareOptions());

    private static SearchResult MakeSource(string text, string docId = "doc-1", int chunkIndex = 0, double score = 0.9) =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex }, Score = score };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private void ScriptChat(params string[] replies)
    {
        var responses = replies.Select(ChatReply).ToArray();
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses[1..]);
    }

    private static Result<IReadOnlyList<SearchResult>, RagError> Ok(params SearchResult[] results) =>
        Result<IReadOnlyList<SearchResult>, RagError>.Success(results);

    private void ScriptRetrieval(params SearchResult[] results) =>
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Ok(results));

    [Fact]
    public async Task AskAsync_HighConfidence_NoRetrievals()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("First sentence.", "Second sentence.", "<DONE>");
        _scorer.Script(0.9);

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("First sentence. Second sentence.", result.Answer);
        Assert.Same(sources, result.Sources);
        _ = await _retriever.DidNotReceive().RetrieveAsync(
            Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_LowConfidence_TriggersRetrievalAndRegeneration()
    {
        var sources = new List<SearchResult> { MakeSource("ctx", "doc-1") };
        ScriptChat("Wrong fact.", "Corrected fact.", "<DONE>");
        _scorer.Script(0.3, 0.9);
        ScriptRetrieval(MakeSource("fresh context", "doc-new", 0, 0.8));

        var result = await CreateSut().AskAsync("original query", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Corrected fact.", result.Answer);
        Assert.Contains(result.Sources, s => string.Equals(s.Chunk.DocumentId.Value, "doc-new", StringComparison.Ordinal));
        Assert.Contains(result.Sources, s => string.Equals(s.Chunk.DocumentId.Value, "doc-1", StringComparison.Ordinal));
        _ = await _retriever.Received(1).RetrieveAsync(
            Arg.Is<string>(q => q!.Contains("original query") && q.Contains("Wrong fact.")),
            Arg.Is<RetrievalOptions>(o => o!.TopK == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_LookaheadDefaults_PlainRetrieval()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Wrong fact.", "Corrected fact.", "<DONE>");
        _scorer.Script(0.3, 0.9);
        ScriptRetrieval(MakeSource("fresh", "doc-new"));

        _ = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        // Lookahead query is already a synthetic document — HyDE / multi-query must be off.
        _ = await _retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o!.TopK == 3 && !o.UseHyde && !o.UseMultiQuery),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_CustomLookaheadRetrievalOptions_UsedVerbatim()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Wrong fact.", "Corrected fact.", "<DONE>");
        _scorer.Script(0.3, 0.9);
        ScriptRetrieval(MakeSource("fresh", "doc-new"));
        var custom = new RetrievalOptions { TopK = 7, UseHyde = true };

        _ = await CreateSut(new FlareOptions { LookaheadRetrievalOptions = custom })
            .AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        // Verbatim: TopK stays 7 (LookaheadTopK is NOT stamped over it), UseHyde stays on.
        _ = await _retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o!.TopK == 7 && o.UseHyde),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_MaxRetrievals_Respected()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("S one.", "S one fixed.", "S two.", "<DONE>");
        _scorer.Script(0.0);
        ScriptRetrieval(MakeSource("fresh", "doc-new"));

        var result = await CreateSut(new FlareOptions { MaxRetrievals = 1 })
            .AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("S one fixed. S two.", result.Answer);
        _ = await _retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_MaxSentences_Stops()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Another sentence."); // never returns <DONE>
        _scorer.Script(0.9);

        var result = await CreateSut(new FlareOptions { MaxSentences = 3 })
            .AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Another sentence. Another sentence. Another sentence.", result.Answer);
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_RetrieverFails_KeepsSentence()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Fact one.", "<DONE>");
        _scorer.Script(0.0, 0.9);
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                new RagError.StorageFailed(new InvalidOperationException("store down"))));

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Fact one.", result.Answer);
        Assert.Same(sources, result.Sources);
        // No regeneration: generation call + <DONE> call only.
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_CustomScorerThrows_TreatedAsConfident()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("A fact.", "<DONE>");
        _scorer.ThrowThis = new InvalidOperationException("custom scorer bug");

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("A fact.", result.Answer);
        _ = await _retriever.DidNotReceive().RetrieveAsync(
            Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_SourcesDeduped_MaxScoreKept()
    {
        var sources = new List<SearchResult> { MakeSource("ctx", "doc-1", 0, 0.5) };
        ScriptChat("Fact.", "Fact again.", "<DONE>");
        _scorer.Script(0.0, 0.9);
        ScriptRetrieval(
            MakeSource("ctx", "doc-1", 0, 0.8),          // duplicate of existing source, higher score
            MakeSource("other", "doc-2", 1, 0.7));

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Sources.Count);
        var doc1 = Assert.Single(result.Sources, s => string.Equals(s.Chunk.DocumentId.Value, "doc-1", StringComparison.Ordinal));
        Assert.Equal(0.8, doc1.Score);
    }

    [Fact]
    public async Task AskAsync_Cancellation_Propagates()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSut().AskAsync("q", sources, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AskAsync_EmptyFirstResponse_YieldsEmptyAnswerGracefully()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("");

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Answer);
        Assert.Same(sources, result.Sources);
        Assert.Equal(0, _scorer.Calls);
    }

    [Fact]
    public async Task AskAsync_ZeroResultLookahead_KeepsSentenceWithoutRegeneration()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Fact one.", "<DONE>");
        _scorer.Script(0.0, 0.9);
        ScriptRetrieval(); // success, but empty

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Fact one.", result.Answer);
        Assert.Same(sources, result.Sources);
        // No regeneration LLM call burned: generation + <DONE> only.
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_EmptyInitialSources_StillGenerates()
    {
        var sources = new List<SearchResult>();
        ScriptChat("Answer.", "<DONE>");
        _scorer.Script(0.9);

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Answer.", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Theory]
    [InlineData("<DONE>")]
    [InlineData("<DONE>.")]
    public async Task AskAsync_ResponseStartingWithDoneToken_Stops(string doneReply)
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Fact one.", doneReply);
        _scorer.Script(0.9);

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Fact one.", result.Answer);
    }

    [Fact]
    public async Task AskAsync_DoneTokenAfterSentence_KeepsSentenceThenStops()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        // Terminator before the token: the first sentence is extracted, the trailing
        // token is discarded with the remainder; the model replies <DONE> next turn.
        ScriptChat("Fact. <DONE>", "<DONE>");
        _scorer.Script(0.9);

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Fact.", result.Answer);
    }

    [Fact]
    public async Task AskAsync_TrailingDoneTokenWithoutTerminator_Stripped()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Fact <DONE>", "<DONE>");
        _scorer.Script(0.9);

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Fact", result.Answer);
    }

    [Fact]
    public async Task AskAsync_SentenceGeneration_BoundsMaxOutputTokens()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Only sentence.", "<DONE>");
        _scorer.Script(0.9);

        _ = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(2).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Is<ChatOptions>(o => o!.MaxOutputTokens == 150),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_MultiSentenceResponse_TakesFirstSentenceOnly()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Alpha is true. Beta is false.", "<DONE>");
        _scorer.Script(0.9);

        var result = await CreateSut().AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Alpha is true.", result.Answer);
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenSingleTextDelta()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        ScriptChat("Only sentence.", "<DONE>");
        _scorer.Script(0.9);

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in CreateSut().AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Equal("Only sentence.", updates[1].TextDelta);
    }

    /// <summary>
    /// A caller's <see cref="RagOptions.SystemPrompt"/> must not displace FLARE's fragment protocol.
    /// </summary>
    /// <remarks>
    /// Regression test for the 2026-08-29 runaway. FLARE generates one sentence per call and feeds the
    /// growing answer back in; a caller instruction such as "End your reply with exactly this sentence"
    /// is a <b>terminal</b> instruction, and applied per fragment it makes the model emit the closing
    /// sentence forever and never emit the DONE token, so the loop also loses its early exit. One
    /// observed response held the same sentence 256 times, 86,091 bytes, against a 3,747-byte maximum
    /// across the 47,151 entries written before that day.
    /// </remarks>
    [Fact]
    public async Task ACallerSystemPrompt_DoesNotDisplaceTheFragmentProtocol()
    {
        var sources = new List<SearchResult> { MakeSource("ctx") };
        var captured = new List<ChatMessage>();
        _chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("<DONE>"))
            .AndDoes(callInfo => captured.AddRange(callInfo.Arg<IList<ChatMessage>>()));

        _ = await CreateSut().AskAsync(
            "q",
            sources,
            new RagOptions { SystemPrompt = "End your reply with exactly: The answer is \"...\"" },
            cancellationToken: TestContext.Current.CancellationToken);

        var system = Assert.Single(captured, m => m.Role == ChatRole.System);
        Assert.Contains("End your reply with exactly", system.Text, StringComparison.Ordinal);
        Assert.Contains("exactly one sentence", system.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<DONE>", system.Text, StringComparison.Ordinal);
    }
}
