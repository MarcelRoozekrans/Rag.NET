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

/// <summary>
/// A deliberate refusal is not a model failure: the retrieval behaviours that degrade on a failed
/// call let it through instead, so the query fails (#948). Behaviours with their own test file
/// pin this there; these four have none.
/// </summary>
public class RefusalPropagationTests
{
    private static readonly BudgetExceededException Refusal = new(CostWindow.Day, 1m, 1m);

    private static SearchResult MakeResult(string docId, string text = "content") =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = 0.5 };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> NextReturning(
        params SearchResult[] results) =>
        (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);

    private static IEmbeddingGenerator<string, Embedding<float>> RefusingEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Refusal);
        return embedder;
    }

    [Fact]
    public async Task MultiQuery_ExpansionRefusal_Propagates()
    {
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(Refusal);
        var sut = new MultiQueryBehavior { QueryExpander = expander };
        var ctx = new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseMultiQuery = true } };

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(
            async () => await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(MakeResult("d"))));

        Assert.Same(Refusal, ex);
    }

    [Fact]
    public async Task MultiQuery_VariantRetrievalRefusal_Propagates()
    {
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant-1", "variant-2" });
        var sut = new MultiQueryBehavior { QueryExpander = expander };
        var ctx = new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseMultiQuery = true } };
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next = (inner, _) =>
            string.Equals(inner.Query, "variant-2", StringComparison.Ordinal)
                ? ValueTask.FromException<IReadOnlyList<SearchResult>>(Refusal)
                : ValueTask.FromResult<IReadOnlyList<SearchResult>>([MakeResult(inner.Query)]);

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(
            async () => await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, next));

        Assert.Same(Refusal, ex);
    }

    [Fact]
    public async Task Reranking_RerankerRefusal_Propagates()
    {
        var reranker = Substitute.For<IReranker>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Refusal);
        var sut = new RerankingBehavior { Reranker = reranker };
        var ctx = new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseReranking = true } };

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(
            async () => await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(MakeResult("d"))));

        Assert.Same(Refusal, ex);
    }

    [Fact]
    public async Task Mmr_EmbedderRefusal_Propagates()
    {
        var sut = new MmrBehavior { Embedder = RefusingEmbedder() };
        var ctx = new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseMmr = true, TopK = 1 } };

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(
            async () => await sut.HandleAsync(
                ctx, TestContext.Current.CancellationToken, NextReturning(MakeResult("a"), MakeResult("b"))));

        Assert.Same(Refusal, ex);
    }

    [Fact]
    public async Task RedundancyFilter_EmbedderRefusal_Propagates()
    {
        var sut = new RedundancyFilterBehavior { Embedder = RefusingEmbedder() };
        var ctx = new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseRedundancyFilter = true } };

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(
            async () => await sut.HandleAsync(
                ctx, TestContext.Current.CancellationToken, NextReturning(MakeResult("a"), MakeResult("b"))));

        Assert.Same(Refusal, ex);
    }
}
