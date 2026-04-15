using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class AdaptiveRetrievalBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static RetrievalContext MakeCtx(RetrievalOptions options) =>
        new() { Query = "test query", Options = options };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        CapturingCtx(out Func<RetrievalContext?> getCapture)
    {
        RetrievalContext? captured = null;
        getCapture = () => captured;
        return (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        };
    }

    // ── Flag off ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_FlagOff_PassesThroughUnchanged()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = false });

        var output = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── Heuristic classification ──────────────────────────────────────────────

    [Theory]
    [InlineData("What is RAG?", "simple")]            // 3 words
    [InlineData("Tell me about it", "simple")]         // 4 words ≤ 6
    [InlineData("how does retrieval work", "complex")] // "how"
    [InlineData("compare BM25 and vector search", "complex")] // "compare"
    [InlineData("why is chunking important", "complex")]       // "why"
    [InlineData("explain hybrid retrieval methods", "complex")]// "explain"
    [InlineData("what is the difference between BM25 and cosine similarity", "complex")] // "difference"
    [InlineData("What is RAG and how does it work and also why is it important", "multi_hop")] // ≥2 conjunctions
    public void ClassifyHeuristic_ReturnsExpected(string query, string expected)
    {
        var result = AdaptiveRetrievalBehavior.ClassifyHeuristic(query);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ClassifyHeuristic_AmbiguousLongQuery_ReturnsNull()
    {
        // Long query with no complex/multi_hop signals
        var result = AdaptiveRetrievalBehavior.ClassifyHeuristic(
            "retrieval augmented generation semantic vector database embedding");
        Assert.Null(result);
    }

    // ── Strategy mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SimpleQuery_SetsTopK3_NoMultiQuery_NoHyde()
    {
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        // "What is RAG?" is a short simple query
        ctx = ctx with { Query = "What is RAG?" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next);

        var captured = getCaptured()!;
        Assert.Equal(3, captured.Options.TopK);
        Assert.False(captured.Options.UseMultiQuery);
        Assert.False(captured.Options.UseHyde);
        Assert.Equal("simple", captured.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_ComplexQuery_SetsTopK8_MultiQuery_NoHyde()
    {
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "how does retrieval augmented generation improve LLM accuracy" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next);

        var captured = getCaptured()!;
        Assert.Equal(8, captured.Options.TopK);
        Assert.True(captured.Options.UseMultiQuery);
        Assert.False(captured.Options.UseHyde);
        Assert.Equal("complex", captured.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_MultiHopQuery_SetsTopK10_MultiQuery_Hyde()
    {
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "What is chunking and how does it affect retrieval and also why does it matter for context windows" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next);

        var captured = getCaptured()!;
        Assert.Equal(10, captured.Options.TopK);
        Assert.True(captured.Options.UseMultiQuery);
        Assert.True(captured.Options.UseHyde);
        Assert.Equal("multi_hop", captured.Extensions["adaptive_complexity"]);
    }

    // ── LLM fallback ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_AmbiguousQuery_NoLlm_DefaultsToComplex()
    {
        var sut = new AdaptiveRetrievalBehavior { ChatClient = null };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        // A long query with no keywords → heuristic returns null → no LLM → defaults to complex
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next);

        Assert.Equal("complex", getCaptured()!.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_AmbiguousQuery_LlmReturnsSimple_SetsSimpleOptions()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "simple")));

        var sut = new AdaptiveRetrievalBehavior { ChatClient = chatClient };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next);

        Assert.Equal("simple", getCaptured()!.Extensions["adaptive_complexity"]);
        Assert.Equal(3, getCaptured()!.Options.TopK);
    }

    [Fact]
    public async Task HandleAsync_LlmThrows_DefaultsToComplex()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = new AdaptiveRetrievalBehavior { ChatClient = chatClient };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next);

        Assert.Equal("complex", getCaptured()!.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_LlmCancelled_PropagatesCancellation()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var sut = new AdaptiveRetrievalBehavior { ChatClient = chatClient };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        // Ambiguous query — forces LLM path
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning([])).AsTask());
    }
}
