using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class CorrectiveRagBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, int chunkIndex, double score, string text = "relevant content about topic") =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static RetrievalContext MakeCtx(RetrievalOptions options, string query = "test query") =>
        new() { Query = query, Options = options };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    // ── Flag off / no IWebSearch ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_FlagOff_PassesThroughUnchanged()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new CorrectiveRagBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseCrag = false });

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task HandleAsync_NoWebSearch_PassesThroughUnchanged()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new CorrectiveRagBehavior { WebSearch = null };
        var ctx = MakeCtx(new RetrievalOptions { UseCrag = true });

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── Above threshold: no web search triggered ─────────────────────────────

    [Fact]
    public async Task HandleAsync_HighRelevance_DoesNotTriggerWebSearch()
    {
        // Heuristic: query tokens in chunk text → high score → no web search
        var webSearch = Substitute.For<IWebSearch>();
        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        // Query: "test query" — both tokens present in chunk text
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, text: "test query relevant content"),
            MakeResult("doc-2", 0, 0.8, text: "test query more content"),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        await webSearch.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal("false", ctx.Extensions["crag_triggered"]);
    }

    // ── Below threshold: Replace mode ────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LowRelevance_Replace_ReturnsWebResults()
    {
        var webResult = MakeResult("web-1", 0, 0.95, "web search result");
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([webResult]));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        // Vector results with no query tokens → low heuristic score
        var vectorResults = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz"),
        };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f, CragFallbackMode = CragFallbackMode.Replace },
            query: "specific topic search");

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(vectorResults));

        Assert.Contains(webResult, output);
        Assert.DoesNotContain(vectorResults[0], output);
        Assert.Equal("true", ctx.Extensions["crag_triggered"]);
    }

    // ── Below threshold: Append mode ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LowRelevance_Append_ReturnsBothResults()
    {
        var webResult = MakeResult("web-1", 0, 0.95, "web search result");
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([webResult]));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        var vectorResult = MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz");
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f, CragFallbackMode = CragFallbackMode.Append },
            query: "specific topic search");

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning([vectorResult]));

        Assert.Contains(vectorResult, output);
        Assert.Contains(webResult, output);
        Assert.Equal(2, output.Count);
    }

    // ── Web search throws: graceful degradation ───────────────────────────────

    [Fact]
    public async Task HandleAsync_WebSearchThrows_ReturnsOriginalVectorResults()
    {
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        var vectorResults = new List<SearchResult> { MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz") };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f },
            query: "specific topic search");

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(vectorResults));

        Assert.Same(vectorResults, output);
    }

    [Fact]
    public async Task HandleAsync_WebSearchCancelled_PropagatesCancellation()
    {
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        var vectorResults = new List<SearchResult> { MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz") };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f },
            query: "specific topic search");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(vectorResults)).AsTask());
    }

    // ── Heuristic scoring unit tests ──────────────────────────────────────────

    [Fact]
    public void ScoreWithHeuristic_EmptyResults_ReturnsZero()
    {
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("test query", []);
        Assert.Equal(0f, score);
    }

    [Fact]
    public void ScoreWithHeuristic_AllMatching_ReturnsOne()
    {
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "test query content"),
            MakeResult("doc-2", 0, 0.8, "test query more"),
        };
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("test query", results);
        Assert.Equal(1f, score);
    }

    [Fact]
    public void ScoreWithHeuristic_EmptyQuery_ReturnsZero()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9, "some content") };
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("", results);
        Assert.Equal(0f, score);
    }

    [Fact]
    public void ScoreWithHeuristic_NoneMatching_ReturnsZero()
    {
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "xyz abc def ghi jkl"),
        };
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("specific topic", results);
        Assert.Equal(0f, score);
    }

    // ── LLM scoring path ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LlmScoring_AllRelevant_DoesNotTriggerWebSearch()
    {
        var webSearch = Substitute.For<IWebSearch>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "relevant")));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch, ChatClient = chatClient };
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9, text: "unrelated xyz") };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f },
            query: "specific topic");

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        await webSearch.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal("false", ctx.Extensions["crag_triggered"]);
    }

    [Fact]
    public async Task HandleAsync_LlmScoring_AllIrrelevant_TriggersWebSearch()
    {
        var webResult = MakeResult("web-1", 0, 0.95, "web result");
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([webResult]));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "irrelevant")));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch, ChatClient = chatClient };
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.1, text: "content") };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f },
            query: "specific topic");

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        Assert.Equal("true", ctx.Extensions["crag_triggered"]);
        Assert.Contains(webResult, output);
    }

    [Fact]
    public async Task HandleAsync_LlmScoringThrows_FallsBackToHeuristic()
    {
        var webSearch = Substitute.For<IWebSearch>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("llm error"));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch, ChatClient = chatClient };
        // High heuristic relevance: query tokens present in chunk text
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, text: "specific topic content"),
        };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f },
            query: "specific topic");

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        // Heuristic score is high → no web search
        await webSearch.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal("false", ctx.Extensions["crag_triggered"]);
    }
}
