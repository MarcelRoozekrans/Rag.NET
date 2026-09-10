using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Search;
using Rag.NET.Storage;
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
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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

    // ── Native hybrid dispatch (IHybridSearchable) ───────────────────────────

    /// <summary>
    /// A store substitute that also implements <see cref="IHybridSearchable"/>, with both its
    /// dense and native-hybrid searches stubbed so a test can assert on <em>which</em> was
    /// called, not just on the result shape.
    /// </summary>
    private static (IVectorStore Store, IEmbeddingGenerator<string, Embedding<float>> Embedder, IBm25Index Bm25)
        MakeHybridCapableArms(IReadOnlyList<SearchResult> denseResults, IReadOnlyList<SearchResult> nativeResults)
    {
        var store = Substitute.For<IVectorStore, IHybridSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        ((IHybridSearchable)store).HybridSearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(nativeResults);
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        return (store, embedder, bm25);
    }

    [Fact]
    public async Task HandleAsync_HybridSearchableStore_DispatchesNative()
    {
        var ct = TestContext.Current.CancellationToken;
        var native = new List<SearchResult> { MakeResult("doc-native", 0, 0.03) };
        var (store, embedder, bm25) = MakeHybridCapableArms([MakeResult("doc-dense", 0, 0.9)], native);

        var sut = new EnsembleBehavior { Embedder = embedder, VectorStore = store, Bm25Index = bm25 };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        // The store's native result is returned as-is: no client-side fusion ran on top of it.
        Assert.Same(native, output);
        await ((IHybridSearchable)store).Received(1).HybridSearchAsync(
            "test query", Arg.Any<ReadOnlyMemory<float>>(), Arg.Is<SearchOptions>(o => o!.TopK == 5), ct);
        await store.DidNotReceive().SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        bm25.DidNotReceive().Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>());
    }

    [Fact]
    public async Task HandleAsync_StoreNotHybridSearchable_UsesClientFusion()
    {
        var ct = TestContext.Current.CancellationToken;

        var store = Substitute.For<IVectorStore>(); // no IHybridSearchable
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior { Embedder = embedder, VectorStore = store, Bm25Index = bm25 };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        // Client fusion ran: the dense search and the BM25 arm were both consulted.
        await store.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        bm25.Received(1).Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>());
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-bm25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_EnsembleOptionsSupplied_KeepsClientFusion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeHybridCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-native", 0, 0.03)]);

        var sut = new EnsembleBehavior { Embedder = embedder, VectorStore = store, Bm25Index = bm25 };
        // Default-valued EnsembleOptions still expresses weighting intent: supplying the
        // object at all keeps the client-side path, where the weights actually apply.
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            EnsembleOptions = new EnsembleOptions(),
        });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        await ((IHybridSearchable)store).DidNotReceive().HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        await store.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-native", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_MinScoreConfigured_KeepsClientFusion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, embedder, bm25) = MakeHybridCapableArms(
            [MakeResult("doc-dense", 0, 0.9)], [MakeResult("doc-native", 0, 0.03)]);

        var sut = new EnsembleBehavior { Embedder = embedder, VectorStore = store, Bm25Index = bm25 };
        // A similarity-tuned MinScore against a native fusion scale (Azure hybrid: RRF values
        // around 0.016) would silently empty the results — so it keeps the client path, where
        // MinScore applies to the dense arm's similarity scores as it always has.
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5, MinScore = 0.5 });

        await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        await ((IHybridSearchable)store).DidNotReceive().HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        await store.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SparseArmWouldRun_KeepsClientFusion()
    {
        var ct = TestContext.Current.CancellationToken;

        var store = Substitute.For<IVectorStore, IHybridSearchable, ISparseSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        ((ISparseSearchable)store).SearchSparseAsync(Arg.Any<SparseVector>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-sparse", 0, 4.0) });
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = new FakeSparseGenerator(_ => MakeSparse()),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        // Native hybrid cannot run a sparse arm; a sparse arm that would run wins.
        await ((IHybridSearchable)store).DidNotReceive().HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        Assert.Contains(output, r => string.Equals(r.Chunk.DocumentId.ToString(), "doc-sparse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_SparseDisabledPerCall_DispatchesNative()
    {
        var ct = TestContext.Current.CancellationToken;

        var store = Substitute.For<IVectorStore, IHybridSearchable, ISparseSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();
        var native = new List<SearchResult> { MakeResult("doc-native", 0, 0.03) };
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        ((IHybridSearchable)store).HybridSearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(native);

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25,
            SparseGenerator = new FakeSparseGenerator(_ => MakeSparse()),
        };
        // UseSparseSearch = false means the sparse arm would not run, so nothing client-side
        // would do more than the native call: native wins again.
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5, UseSparseSearch = false });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Same(native, output);
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
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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

    // ── MetadataFilter reaches the BM25 arm of client-side hybrid (#350) ─────

    // #350: the BM25 arm never received MetadataFilter, and RrfMerger merged its hits alongside
    // the filtered arms, so a filtered query could return a chunk the filter excluded.
    [Fact]
    public async Task HandleAsync_ClientSideHybrid_PassesMetadataFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        // The third argument is the assertion. That Search was called at all was always true.
        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }

    // CanDispatchNatively returns false when MinScore is non-zero, so a store WITH native hybrid
    // still takes the client-side path -- and still leaked before this fix.
    [Fact]
    public async Task HandleAsync_NativeStoreWithMinScore_StillPassesFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
            MinScore = 0.2,
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }

    // Same again for EnsembleOptions: supplying one at all expresses weighting intent, which the
    // native path cannot honour, so the request falls back to client-side fusion.
    [Fact]
    public async Task HandleAsync_NativeStoreWithEnsembleOptions_StillPassesFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
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
            EnsembleOptions = new EnsembleOptions(),
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }

    // The third client-side trigger: native hybrid cannot run a sparse arm, so a sparse arm that
    // would run keeps the client path even against a store that also implements IHybridSearchable
    // -- see HandleAsync_SparseArmWouldRun_KeepsClientFusion above. The SparseGenerator and
    // ISparseSearchable wiring this needs already exists in this file (MakeSparseCapableArms),
    // so it costs nothing extra to cover it here rather than in InMemoryBm25IndexTests.
    [Fact]
    public async Task HandleAsync_SparseArmForcesClientPath_StillPassesFilterToTheBm25Arm()
    {
        var ct = TestContext.Current.CancellationToken;

        var store = Substitute.For<IVectorStore, IHybridSearchable, ISparseSearchable>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) });
        ((ISparseSearchable)store).SearchSparseAsync(Arg.Any<SparseVector>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-sparse", 0, 4.0) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = bm25Index,
            SparseGenerator = new FakeSparseGenerator(_ => MakeSparse()),
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            },
        });

        _ = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        await ((IHybridSearchable)store).DidNotReceive().HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        bm25Index.Received(1).Search(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<IDictionary<string, MetadataValue>?>(f => f != null && f["tenant"] == "a"));
    }

    // ── End-to-end: a filtered client-side hybrid query never leaks a filtered-out chunk ──────

    // Design §Testing item 1: "a test that fails against today's code: a filtered hybrid query
    // that returns a chunk the filter excludes." The four tests above only assert that the filter
    // argument reaches Bm25Index.Search (NSubstitute interaction assertions) -- they say nothing
    // about what RrfMerger does with the merged output. This test composes the real dense store,
    // the real BM25 index, and the real merge, so it fails if a future refactor (e.g. an RrfMerger
    // change, or a post-merge step that re-adds unfiltered hits) reintroduces the leak, where the
    // interaction tests above would stay green.
    // A chunk tagged with a tenant, for the end-to-end test below. Every chunk gets the same
    // embedding so the dense arm alone would not obviously favour one over the others -- what
    // decides whether "b" leaks is the filter, not the ranking.
    private static (TextChunk Chunk, ReadOnlyMemory<float> Vector) MakeTenantChunk(string docId, string text, string tenant) =>
        (new TextChunk
        {
            DocumentId = new DocumentId(docId),
            ChunkIndex = 0,
            Text = text,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = tenant },
        },
        new ReadOnlyMemory<float>([1f, 0f, 0f]));

    [Fact]
    public async Task HandleAsync_ClientSideHybrid_NeverReturnsAChunkTheFilterExcludes()
    {
        var ct = TestContext.Current.CancellationToken;

        using var vectorStore = new InMemoryVectorStore();
        using var bm25Index = new InMemoryBm25Index();

        var a = MakeTenantChunk("doc-a", "irrelevant filler text", "a");
        // Repeats the query term so BM25 favours this chunk over the others -- if the filter did
        // not reach the BM25 arm, this is the chunk that would leak through.
        var bExcluded = MakeTenantChunk("doc-b", "widget widget widget widget", "b");
        var c = MakeTenantChunk("doc-c", "widget", "a");

        await vectorStore.StoreAsync(
            [
                new EmbeddedChunk { Chunk = a.Chunk, Embedding = a.Vector },
                new EmbeddedChunk { Chunk = bExcluded.Chunk, Embedding = bExcluded.Vector },
                new EmbeddedChunk { Chunk = c.Chunk, Embedding = c.Vector },
            ],
            ct);

        bm25Index.Add(a.Chunk);
        bm25Index.Add(bExcluded.Chunk);
        bm25Index.Add(c.Chunk);

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f, 0f })]));

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 10,
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = "a" },
        }) with { Query = "widget" };

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.NotEmpty(output);
        Assert.DoesNotContain(output, r => r.Chunk.Metadata.TryGetValue("tenant", out var tenant) && tenant == "b");
    }

    /// <summary>
    /// A store whose native hybrid query does something client-side fusion cannot reproduce. The
    /// declaration, not the backend, is what the behaviour reads — this fake names no vendor.
    /// </summary>
    private sealed class FakeRankingHybridStore : IVectorStore, IHybridSearchable
    {
        public string? NativeOnlyCapability => "semantic ranking";

        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
            string textQuery, ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("native", 0, 3.5)]);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("dense", 0, 0.9)]);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A store whose declaration is present but blank — which is what a substitute returns for an
    /// unconfigured <see langword="string"/> property, and what a careless implementer returns.
    /// </summary>
    private sealed class FakeBlankDeclarationHybridStore : IVectorStore, IHybridSearchable
    {
        public string? NativeOnlyCapability => "   ";

        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
            string textQuery, ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("native", 0, 3.5)]);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("dense", 0, 0.9)]);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A decorator that hides <see cref="IHybridSearchable"/> the way <c>ResilientVectorStore</c>
    /// does (#544). Deliberately a local fake rather than a reference to Rag.NET.Resilience: the
    /// diagnostic is package-agnostic and must stay that way.
    /// </summary>
    private sealed class FakeDecoratorOverHybridStore : IVectorStore, IVectorStoreDecorator
    {
        public Type InnerStoreType => typeof(FakeRankingHybridStore);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("dense", 0, 0.9)]);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f, 0f })]));
        return embedder;
    }

    /// <summary>
    /// A store that declares a native-only capability must not be fused client-side: the caller
    /// would receive correct results with the capability silently absent (#539).
    /// </summary>
    [Theory]
    [InlineData("MinScore")]
    [InlineData("EnsembleOptions")]
    [InlineData("UseHybridSearch")]
    public async Task HandleAsync_NativeOnlyCapabilityAndCannotDispatchNatively_Throws(string blocker)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = new FakeRankingHybridStore(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var options = blocker switch
        {
            "MinScore" => new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 },
            "EnsembleOptions" => new RetrievalOptions { UseHybridSearch = true, EnsembleOptions = new EnsembleOptions() },
            _ => new RetrievalOptions { UseHybridSearch = false },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(MakeCtx(options), ct, (_, _) =>
                ValueTask.FromResult<IReadOnlyList<SearchResult>>([])).AsTask());

        Assert.Contains("semantic ranking", ex.Message, StringComparison.Ordinal);
        Assert.Contains(blocker, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And when nothing blocks it, the native path serves the query — the capability is honoured,
    /// not merely guarded. Without this row the theory above would pass against a behaviour that
    /// threw unconditionally.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NativeOnlyCapabilityAndCanDispatchNatively_UsesTheNativePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = new FakeRankingHybridStore(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var output = await sut.HandleAsync(
            MakeCtx(new RetrievalOptions { UseHybridSearch = true }), ct,
            (_, _) => throw new InvalidOperationException("must not call next"));

        var only = Assert.Single(output);
        Assert.Equal(new DocumentId("native"), only.Chunk.DocumentId);
    }

    /// <summary>
    /// The negative control, and the one that keeps this from becoming a behaviour break: a store
    /// declaring nothing is fused client-side exactly as before, MinScore and all.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoNativeOnlyCapability_StillFusesClientSideWithAMinScore()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([MakeResult("doc-1", 0, 0.9)]));

        var bm25 = Substitute.For<IBm25Index>();
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns([MakeBm25Hit("doc-1", 0)]);

        var sut = new EnsembleBehavior { Embedder = MakeEmbedder(), VectorStore = vectorStore, Bm25Index = bm25 };

        var output = await sut.HandleAsync(
            MakeCtx(new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 }), ct,
            (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.NotEmpty(output);
    }

    /// <summary>
    /// A store declaring nothing, with hybrid off, still reaches <c>next</c> — moving the refusal
    /// above the early return must not make that return conditional for ordinary stores.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoNativeOnlyCapabilityAndHybridOff_StillCallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EnsembleBehavior
        {
            Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var nextCalled = false;
        await sut.HandleAsync(MakeCtx(new RetrievalOptions { UseHybridSearch = false }), ct, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        });

        Assert.True(nextCalled);
    }

    /// <summary>
    /// A decorator that hides the capability cannot be refused — the behaviour has no way to ask
    /// the decorated instance whether ranking is on, because <see cref="IVectorStoreDecorator"/>
    /// deliberately exposes only the inner <see cref="Type"/>. It warns instead (#544).
    /// </summary>
    [Fact]
    public async Task HandleAsync_DecoratorHidesHybridCapability_WarnsNamingTheInnerStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = new FakeDecoratorOverHybridStore(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true }) with { Logger = logger };

        await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(nameof(FakeRankingHybridStore), StringComparison.Ordinal)
            && e.Message.Contains("#544", StringComparison.Ordinal));
    }

    /// <summary>
    /// And an ordinary undecorated store warns about nothing — the diagnostic must not fire for
    /// every store in the library that simply has no native hybrid.
    /// </summary>
    [Fact]
    public async Task HandleAsync_OrdinaryStore_DoesNotWarnAboutDecoration()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true }) with { Logger = logger };

        await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("#544", StringComparison.Ordinal));
    }

    /// <summary>
    /// A blank declaration is treated as absent rather than as an unnamed capability. The value's
    /// whole job is to be quoted into the refusal, and a blank one produces "is configured for ,
    /// which only its native hybrid query performs" — a refusal naming nothing actionable. So the
    /// query takes the ordinary client-side path instead of being refused with an empty reason.
    /// </summary>
    /// <remarks>
    /// Found by six pre-existing tests failing when this guard first shipped: a substituted
    /// <see langword="string"/> property returns <see cref="string.Empty"/>, not
    /// <see langword="null"/>, so every test using a substituted hybrid store was refused with a
    /// garbled message.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_BlankNativeOnlyCapability_IsTreatedAsNoDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var bm25 = Substitute.For<IBm25Index>();
        bm25.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IDictionary<string, MetadataValue>?>())
            .Returns([MakeBm25Hit("dense", 0)]);

        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = new FakeBlankDeclarationHybridStore(),
            Bm25Index = bm25,
        };

        var output = await sut.HandleAsync(
            MakeCtx(new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 }), ct,
            (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.NotEmpty(output);
    }
}
