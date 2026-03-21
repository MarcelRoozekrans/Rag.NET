using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class EnsembleBehaviorTests
{
    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static (TextChunk chunk, double score) MakeBm25Hit(string docId, int chunkIndex) =>
        (new TextChunk { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = $"{docId}-{chunkIndex}" }, 1.0);

    private static RetrievalContext MakeCtx(RetrievalOptions options) =>
        new() { Query = "test query", Options = options, Logger = NullLogger.Instance };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    [Fact]
    public async Task HandleAsync_HybridSearchFalse_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new EnsembleBehavior
        {
            Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = false });

        var nextCalled = false;
        var output = await sut.HandleAsync(ctx, ct, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>(expected);
        });

        Assert.True(nextCalled);
        Assert.Same(expected, output);
    }

    [Fact]
    public async Task HandleAsync_HybridSearchTrue_MergesDenseAndBm25()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var denseResults = new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) };

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.NotEmpty(output);
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-dense", StringComparison.Ordinal));
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-bm25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_HybridSearchTrue_Bm25HeavyWeights_RanksBm25First()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        var denseResults = new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) };

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            EnsembleOptions = new EnsembleOptions { DenseWeight = 0.1f, Bm25Weight = 0.9f, K = 60 }
        });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Equal("doc-bm25", output[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public async Task HandleAsync_Bm25Throws_ReturnsOnlyDenseResults()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        var denseResults = new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) };

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Throws(new InvalidOperationException("BM25 failure"));

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Single(output);
        Assert.Equal("doc-dense", output[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public async Task HandleAsync_EnsembleOptionsNull_UsesDefaults_NoThrow()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-A", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Array.Empty<(TextChunk, double)>());

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, EnsembleOptions = null });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.NotEmpty(output);
    }
}
