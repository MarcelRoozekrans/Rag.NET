using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class HydeBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = "content", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static RetrievalContext MakeCtx(RetrievalOptions options, string query = "test query") =>
        new() { Query = query, Options = options };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    private static IEmbeddingGenerator<string, Embedding<float>> EmbedderReturning(params float[][] vectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
                vectors.Select(v => new Embedding<float>(v)).ToList()));
        return embedder;
    }

    // ── Multi-hypothesis averaging ────────────────────────────────────────────

    [Fact]
    public async Task MultiHypothesis_SetsEmbeddingOverride_AveragedAndNormalized()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateManyAsync(Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["hypo a", "hypo b", "hypo c"]));
        var embedder = EmbedderReturning([1f, 0f], [0f, 1f], [1f, 0f]);

        var sut = new HydeBehavior
        {
            HydeGenerator = generator,
            Embedder = embedder,
            Options = new HydeOptions { HypothesisCount = 3 },
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        RetrievalContext? captured = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                captured = innerCtx;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>([MakeResult("doc-1", 0, 0.9)]);
            };

        await sut.HandleAsync(ctx, ct, next);

        Assert.NotNull(captured);
        Assert.False(captured.Options.UseHyde);
        Assert.Null(captured.Options.EmbeddingTextOverride);
        Assert.NotNull(captured.Options.EmbeddingOverride);

        // mean = (2/3, 1/3); L2-normalized = (2, 1) / sqrt(5)
        var vector = captured.Options.EmbeddingOverride.Value.ToArray();
        Assert.Equal(2, vector.Length);
        Assert.Equal(2f / MathF.Sqrt(5f), vector[0], 5);
        Assert.Equal(1f / MathF.Sqrt(5f), vector[1], 5);
    }

    [Fact]
    public async Task SingleHypothesis_UsesTextOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("the single hypothesis"));
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var sut = new HydeBehavior
        {
            HydeGenerator = generator,
            Embedder = embedder,
            Options = new HydeOptions { HypothesisCount = 1 },
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        RetrievalContext? captured = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                captured = innerCtx;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
            };

        await sut.HandleAsync(ctx, ct, next);

        Assert.NotNull(captured);
        Assert.Equal("the single hypothesis", captured.Options.EmbeddingTextOverride);
        Assert.Null(captured.Options.EmbeddingOverride);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        await generator.DidNotReceive().GenerateManyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoEmbedder_FallsBackToTextOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("the single hypothesis"));

        var sut = new HydeBehavior
        {
            HydeGenerator = generator,
            Embedder = null,
            Options = new HydeOptions { HypothesisCount = 3 },
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        RetrievalContext? captured = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                captured = innerCtx;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
            };

        await sut.HandleAsync(ctx, ct, next);

        Assert.NotNull(captured);
        Assert.Equal("the single hypothesis", captured.Options.EmbeddingTextOverride);
        Assert.Null(captured.Options.EmbeddingOverride);
        await generator.DidNotReceive().GenerateManyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratorFails_FallsThroughToPlainQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateManyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("model down"));
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var sut = new HydeBehavior
        {
            HydeGenerator = generator,
            Embedder = embedder,
            Options = new HydeOptions { HypothesisCount = 3 },
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        RetrievalContext? captured = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                captured = innerCtx;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
            };

        await sut.HandleAsync(ctx, ct, next);

        Assert.NotNull(captured);
        Assert.False(captured.Options.UseHyde);
        Assert.Null(captured.Options.EmbeddingTextOverride);
        Assert.Null(captured.Options.EmbeddingOverride);
    }

    [Fact]
    public async Task ZeroNormAverage_FallsBackToFirstDocTextOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateManyAsync(Arg.Any<string>(), 2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["hypo a", "hypo b"]));
        // Opposite vectors → mean is the zero vector → cannot normalize
        var embedder = EmbedderReturning([1f, 0f], [-1f, 0f]);

        var sut = new HydeBehavior
        {
            HydeGenerator = generator,
            Embedder = embedder,
            Options = new HydeOptions { HypothesisCount = 2 },
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        RetrievalContext? captured = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                captured = innerCtx;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
            };

        await sut.HandleAsync(ctx, ct, next);

        Assert.NotNull(captured);
        Assert.Null(captured.Options.EmbeddingOverride);
        Assert.Equal("hypo a", captured.Options.EmbeddingTextOverride);
    }

    // ── EmbeddingOverride consumption ─────────────────────────────────────────

    [Fact]
    public async Task VectorStoreBehavior_EmbeddingOverride_SkipsEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var overrideVector = new ReadOnlyMemory<float>([0.6f, 0.8f]);

        ReadOnlyMemory<float> searchedVector = default;
        vectorStore.SearchAsync(
                Arg.Do<ReadOnlyMemory<float>>(v => searchedVector = v),
                Arg.Any<SearchOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-1", 0, 0.9) });

        var sut = new VectorStoreBehavior { VectorStore = vectorStore, Embedder = embedder };
        var ctx = MakeCtx(new RetrievalOptions { EmbeddingOverride = overrideVector });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("terminal"));

        Assert.Single(output);
        Assert.Equal(overrideVector.ToArray(), searchedVector.ToArray());
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsembleBehavior_EmbeddingOverride_SkipsEmbedderForDenseArm()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25 = Substitute.For<IBm25Index>();
        var overrideVector = new ReadOnlyMemory<float>([0.6f, 0.8f]);

        ReadOnlyMemory<float> searchedVector = default;
        vectorStore.SearchAsync(
                Arg.Do<ReadOnlyMemory<float>>(v => searchedVector = v),
                Arg.Any<SearchOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-1", 0, 0.9) });
        bm25.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<(TextChunk chunk, double score)>());

        var sut = new EnsembleBehavior { VectorStore = vectorStore, Embedder = embedder, Bm25Index = bm25 };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, EmbeddingOverride = overrideVector });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.Single(output);
        Assert.Equal(overrideVector.ToArray(), searchedVector.ToArray());
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbeddingCacheBehavior_OverrideSet_PassesThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var mockCache = Substitute.For<HybridCache>();
        var sut = new EmbeddingCacheBehavior
        {
            Cache = mockCache,
            CachingOptions = new CachingOptions(),
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseCacheEmbedding = true,
            EmbeddingOverride = new ReadOnlyMemory<float>([0.6f, 0.8f]),
        });

        var expected = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var nextCalled = false;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(expected);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.True(nextCalled);
        Assert.Same(expected, output);
        await mockCache.DidNotReceive().GetOrCreateAsync<List<SearchResult>>(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, ValueTask<List<SearchResult>>>>(),
            Arg.Any<HybridCacheEntryOptions?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }
}
