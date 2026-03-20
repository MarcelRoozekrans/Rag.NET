using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class EmbeddingCacheBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = docId, DocumentId = new DocumentId(docId), ChunkIndex = 0 },
            Score = score
        };

    private static RetrievalContext MakeCtx(string query, RetrievalOptions options) =>
        new() { Query = query, Options = options };

    private static HybridCache BuildRealHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<HybridCache>();
    }

    /// <summary>
    /// Minimal HybridCache fake that throws a non-cancellation exception from GetOrCreateAsync,
    /// to test the fallback path without triggering EPS06 (ValueTask hidden-copy analyzer).
    /// </summary>
    private sealed class ThrowingHybridCache : HybridCache
    {
        private readonly Exception _exception;

        public ThrowingHybridCache(Exception exception) => _exception = exception;

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<T>(_exception);

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    // ── Skip paths ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skip_WhenUseCacheEmbeddingFalse_CallsNextWithoutConsultingCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0.9) };

        var mockCache = Substitute.For<HybridCache>();
        var sut = new EmbeddingCacheBehavior
        {
            Cache = mockCache,
            CachingOptions = new CachingOptions()
        };

        var ctx = MakeCtx("query", new RetrievalOptions { UseCacheEmbedding = false });
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
        // Cache must not have been consulted
        await mockCache.DidNotReceive().GetOrCreateAsync<List<SearchResult>>(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, ValueTask<List<SearchResult>>>>(),
            Arg.Any<HybridCacheEntryOptions?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skip_WhenCacheIsNull_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0.9) };

        var sut = new EmbeddingCacheBehavior
        {
            Cache = null,
            CachingOptions = new CachingOptions()
        };

        var ctx = MakeCtx("query", new RetrievalOptions { UseCacheEmbedding = true });
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
    }

    [Fact]
    public async Task Skip_WhenCachingOptionsIsNull_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0.9) };

        var mockCache = Substitute.For<HybridCache>();
        var sut = new EmbeddingCacheBehavior
        {
            Cache = mockCache,
            CachingOptions = null
        };

        var ctx = MakeCtx("query", new RetrievalOptions { UseCacheEmbedding = true });
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
    }

    // ── Cache hit: next called only once across two identical queries ─────────

    [Fact]
    public async Task CacheHit_OnSecondIdenticalQuery_NextIsNotCalledAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0.9) };

        var hybridCache = BuildRealHybridCache();
        var sut = new EmbeddingCacheBehavior
        {
            Cache = hybridCache,
            CachingOptions = new CachingOptions { EmbeddingTtl = TimeSpan.FromMinutes(5) }
        };

        var options = new RetrievalOptions { UseCacheEmbedding = true };
        var ctx = MakeCtx("same query", options);

        var nextCallCount = 0;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCallCount++;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(expected);
            };

        // First call — cache miss, next should be invoked
        var output1 = await sut.HandleAsync(ctx, ct, next);
        Assert.Equal(1, nextCallCount);
        Assert.Single(output1);

        // Second call with the same query — cache hit, next must NOT be invoked again
        var output2 = await sut.HandleAsync(ctx, ct, next);
        Assert.Equal(1, nextCallCount);
        Assert.Single(output2);
        Assert.Equal(expected[0].Chunk.DocumentId, output2[0].Chunk.DocumentId);
    }

    // ── EmbeddingTextOverride is used as the cache key ────────────────────────

    [Fact]
    public async Task CacheHit_WhenEmbeddingTextOverrideMatches_NextIsNotCalledAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-override", 0.85) };

        var hybridCache = BuildRealHybridCache();
        var sut = new EmbeddingCacheBehavior
        {
            Cache = hybridCache,
            CachingOptions = new CachingOptions { EmbeddingTtl = TimeSpan.FromMinutes(5) }
        };

        var nextCallCount = 0;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCallCount++;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(expected);
            };

        // First call: query "q", override "override" — cache miss
        var ctx1 = MakeCtx("q", new RetrievalOptions
        {
            UseCacheEmbedding = true,
            EmbeddingTextOverride = "override"
        });
        var output1 = await sut.HandleAsync(ctx1, ct, next);
        Assert.Equal(1, nextCallCount);
        Assert.Single(output1);

        // Second call: different query but same override — should be a cache hit
        var ctx2 = MakeCtx("different", new RetrievalOptions
        {
            UseCacheEmbedding = true,
            EmbeddingTextOverride = "override"
        });
        var output2 = await sut.HandleAsync(ctx2, ct, next);
        Assert.Equal(1, nextCallCount); // next not called again
        Assert.Single(output2);
        Assert.Equal(expected[0].Chunk.DocumentId, output2[0].Chunk.DocumentId);
    }

    // ── Exception from cache falls back to next ───────────────────────────────

    [Fact]
    public async Task OnCacheException_FallsBackToNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var fallbackResults = new List<SearchResult> { MakeResult("fallback-doc", 0.7) };

        var sut = new EmbeddingCacheBehavior
        {
            Cache = new ThrowingHybridCache(new InvalidOperationException("Cache exploded")),
            CachingOptions = new CachingOptions()
        };

        var ctx = MakeCtx("query", new RetrievalOptions { UseCacheEmbedding = true });
        var nextCalled = false;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(fallbackResults);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.True(nextCalled);
        Assert.Same(fallbackResults, output);
    }
}
