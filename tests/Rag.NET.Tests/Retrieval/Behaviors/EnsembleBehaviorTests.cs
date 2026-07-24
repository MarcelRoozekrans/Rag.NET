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

    [Fact]
    public async Task HandleAsync_Bm25ThrowsOperationCanceled_PropagatesException()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Throws<OperationCanceledException>();

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException()).AsTask());
    }

    // ── Sparse (SPLADE) third arm ────────────────────────────────────────────

    private static SparseVector MakeSparse() =>
        new() { Indices = new[] { 1, 5 }, Values = new[] { 0.5f, 0.7f } };

    /// <summary>
    /// Hand-written fake: substituting <see cref="ISparseEmbeddingGenerator.GenerateAsync"/>
    /// (a <see cref="ValueTask{T}"/> member) via NSubstitute trips EPS06 (hidden struct copy).
    /// </summary>
    private sealed class FakeSparseGenerator : ISparseEmbeddingGenerator
    {
        private readonly Func<string, SparseVector> _generate;

        public FakeSparseGenerator(Func<string, SparseVector> generate) => _generate = generate;

        public int Calls { get; private set; }

        public ValueTask<SparseVector> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(_generate(text));
        }
    }

    private static (IVectorStore Store, IEmbeddingGenerator<string, Embedding<float>> Embedder, IBm25Index Bm25)
        MakeSparseCapableArms(IReadOnlyList<SearchResult> denseResults, IReadOnlyList<SearchResult> sparseResults)
    {
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        ((ISparseSearchable)store).SearchSparseAsync(Arg.Any<SparseVector>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(sparseResults);
        bm25.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        return (store, embedder, bm25);
    }

    [Fact]
    public async Task HandleAsync_SparseArm_FusesAllThreeArms()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeSparseCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-sparse", 0, 4.0)]);
        var generator = new FakeSparseGenerator(_ => MakeSparse());

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = generator,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-dense", StringComparison.Ordinal));
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-bm25", StringComparison.Ordinal));
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-sparse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_NoSparseGenerator_TwoArmResultUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeSparseCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-sparse", 0, 4.0)]);

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = null,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.DoesNotContain(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-sparse", StringComparison.Ordinal));
        await ((ISparseSearchable)store).DidNotReceive().SearchSparseAsync(
            Arg.Any<SparseVector>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StoreNotSparseSearchable_TwoArmResultUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>(); // no ISparseSearchable
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();
        var generator = new FakeSparseGenerator(_ => MakeSparse());

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25,
            SparseGenerator = generator,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.NotEmpty(output);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task HandleAsync_UseSparseSearchFalse_SparseArmDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeSparseCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-sparse", 0, 4.0)]);
        var generator = new FakeSparseGenerator(_ => MakeSparse());

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = generator,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5, UseSparseSearch = false });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.DoesNotContain(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-sparse", StringComparison.Ordinal));
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task HandleAsync_SparseEncoderFails_DenseAndBm25StillServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeSparseCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-sparse", 0, 4.0)]);
        var generator = new FakeSparseGenerator(_ => throw new InvalidOperationException("encoder down"));

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = generator,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-dense", StringComparison.Ordinal));
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-bm25", StringComparison.Ordinal));
        Assert.DoesNotContain(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-sparse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_SparseSearchFails_DenseAndBm25StillServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeSparseCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-sparse", 0, 4.0)]);
        ((ISparseSearchable)store).SearchSparseAsync(Arg.Any<SparseVector>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("sparse index down"));
        var generator = new FakeSparseGenerator(_ => MakeSparse());

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = generator,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-dense", StringComparison.Ordinal));
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-bm25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_EmptySparseQueryVector_SparseArmSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeSparseCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-sparse", 0, 4.0)]);
        var generator = new FakeSparseGenerator(_ => SparseVector.Empty);

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = generator,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.NotEmpty(output);
        await ((ISparseSearchable)store).DidNotReceive().SearchSparseAsync(
            Arg.Any<SparseVector>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Bm25ReturnsEmpty_ReturnsDenseResults()
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
            .Returns(Array.Empty<(TextChunk, double)>());

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.NotEmpty(output);
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-dense", StringComparison.Ordinal));
    }
}
