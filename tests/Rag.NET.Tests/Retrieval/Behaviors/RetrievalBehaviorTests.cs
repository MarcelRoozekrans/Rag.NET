using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class RetrievalBehaviorTests
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

    // ── LostInTheMiddleBehavior ───────────────────────────────────────────────

    [Fact]
    public async Task LostInTheMiddle_WhenFlagFalse_ReturnsResultsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
        };

        var sut = new LostInTheMiddleBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseLostInTheMiddleReordering = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task LostInTheMiddle_WhenFlagTrue_ReordersResults()
    {
        var ct = TestContext.Current.CancellationToken;
        // With 4 results: even-indexed (0,2) go left, odd-indexed (1,3) go right
        // Input: doc-1(0.9), doc-2(0.8), doc-3(0.7), doc-4(0.6)
        // Expected: doc-1, doc-3, doc-4, doc-2
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
            MakeResult("doc-4", 0, 0.6),
        };

        var sut = new LostInTheMiddleBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseLostInTheMiddleReordering = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Equal(4, output.Count);
        Assert.Equal("doc-1", output[0].Chunk.DocumentId);
        Assert.Equal("doc-3", output[1].Chunk.DocumentId);
        Assert.Equal("doc-4", output[2].Chunk.DocumentId);
        Assert.Equal("doc-2", output[3].Chunk.DocumentId);
    }

    // ── MmrBehavior ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mmr_WhenFlagFalse_PassesThroughToNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        // Embedder is null! — must not be called when UseMmr = false
        var sut = new MmrBehavior { Embedder = null! };
        var ctx = MakeCtx(new RetrievalOptions { UseMmr = false });

        var nextCalled = false;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.True(nextCalled);
        Assert.Same(results, output);
    }

    // ── RedundancyFilterBehavior ──────────────────────────────────────────────

    [Fact]
    public async Task RedundancyFilter_WhenFlagFalse_ReturnsNextResultsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        // Embedder is null! — must not be called when UseRedundancyFilter = false
        var sut = new RedundancyFilterBehavior { Embedder = null! };
        var ctx = MakeCtx(new RetrievalOptions { UseRedundancyFilter = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── ParentDocumentRetrievalBehavior ───────────────────────────────────────

    [Fact]
    public async Task ParentDocument_WhenStoreNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var sut = new ParentDocumentRetrievalBehavior { ParentStore = null };
        var ctx = MakeCtx(new RetrievalOptions { UseParentDocument = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task ParentDocument_WhenFlagFalse_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var store = new InMemoryParentChunkStore();
        var sut = new ParentDocumentRetrievalBehavior { ParentStore = store };
        var ctx = MakeCtx(new RetrievalOptions { UseParentDocument = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── ResultCacheBehavior ───────────────────────────────────────────────────

    [Fact]
    public async Task ResultCache_WhenFlagFalse_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        // Cache and CachingOptions are null — flag-false short-circuits before touching them
        var sut = new ResultCacheBehavior { Cache = null, CachingOptions = null };
        var ctx = MakeCtx(new RetrievalOptions { UseCacheResult = false });

        var nextCalled = false;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.True(nextCalled);
        Assert.Same(results, output);
    }

    // ── RerankingBehavior ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reranking_WhenRerankerNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        var sut = new RerankingBehavior { Reranker = null };
        var ctx = MakeCtx(new RetrievalOptions { UseReranking = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task Reranking_WhenFlagFalse_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var reranker = Substitute.For<IReranker>();
        var sut = new RerankingBehavior { Reranker = reranker };
        var ctx = MakeCtx(new RetrievalOptions { UseReranking = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
        await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    // ── HydeBehavior ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Hyde_WhenGeneratorNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var sut = new HydeBehavior { HydeGenerator = null };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task Hyde_WhenEnabled_PassesHypotheticalDocAsEmbeddingOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        const string hypotheticalDoc = "This is the hypothetical document.";
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(hypotheticalDoc));

        var sut = new HydeBehavior { HydeGenerator = generator };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        string? capturedOverride = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                capturedOverride = innerCtx.Options.EmbeddingTextOverride;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.Equal(hypotheticalDoc, capturedOverride);
        Assert.Same(results, output);
    }

    // ── MultiQueryBehavior ────────────────────────────────────────────────────

    [Fact]
    public async Task MultiQuery_WhenExpanderNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var sut = new MultiQueryBehavior { QueryExpander = null };
        var ctx = MakeCtx(new RetrievalOptions { UseMultiQuery = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── VectorStoreBehavior ───────────────────────────────────────────────────

    [Fact]
    public async Task VectorStore_WhenDenseSearch_CallsVectorStoreAndReturnsResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = MakeResult("doc-1", 0, 0.95);

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { expected });

        var sut = new VectorStoreBehavior
        {
            VectorStore = vectorStore,
            Embedder = embedder,
        };

        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = false });
        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("Terminal behavior must not call next"));

        Assert.Single(output);
        Assert.Equal(0.95, output[0].Score);
    }

    [Fact]
    public async Task VectorStore_WhenHybridSearch_DoesNotCallBm25_PerformsDenseOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = MakeResult("doc-1", 0, 0.95);

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { expected });

        var sut = new VectorStoreBehavior
        {
            VectorStore = vectorStore,
            Embedder = embedder,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.Single(output);
        bm25Index.DidNotReceive().Search(Arg.Any<string>(), Arg.Any<int>());
    }
}
