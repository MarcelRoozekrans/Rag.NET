using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

/// <summary>
/// The embedding and result cache behaviours call the rest of the pipeline inside the cache
/// factory. A failure there is the pipeline's, not the cache's: it must propagate as it is, and
/// the pipeline must not run a second time (#948). Only a failure of the cache itself degrades
/// to calling the pipeline directly.
/// </summary>
public class CacheBehaviorInnerFailureTests
{
    public static TheoryData<string> Behaviors => new() { "embedding", "result" };

    private static (IRetrievalBehavior Sut, RetrievalContext Ctx) Create(string kind, HybridCache cache) => kind switch
    {
        "embedding" => (
            new EmbeddingCacheBehavior { Cache = cache, CachingOptions = new CachingOptions() },
            new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseCacheEmbedding = true } }),
        _ => (
            new ResultCacheBehavior { Cache = cache, CachingOptions = new CachingOptions() },
            new RetrievalContext { Query = "q", Options = new RetrievalOptions { UseCacheResult = true } }),
    };

    private static HybridCache RealCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    [Theory]
    [MemberData(nameof(Behaviors))]
    public async Task InnerFailure_PropagatesWithoutRerunning(string kind)
    {
        var (sut, ctx) = Create(kind, RealCache());
        var failure = new InvalidOperationException("downstream");
        var calls = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sut.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException<IReadOnlyList<SearchResult>>(failure);
            }));

        Assert.Same(failure, ex);
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(Behaviors))]
    public async Task InnerRefusal_Propagates(string kind)
    {
        var (sut, ctx) = Create(kind, RealCache());
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);
        var calls = 0;

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(async () => await sut.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException<IReadOnlyList<SearchResult>>(refusal);
            }));

        Assert.Same(refusal, ex);
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(Behaviors))]
    public async Task CacheFailure_FallsBackToNextOnce(string kind)
    {
        var (sut, ctx) = Create(kind, new ThrowingHybridCache(new InvalidOperationException("cache down")));
        IReadOnlyList<SearchResult> fallback = [];
        var calls = 0;

        var output = await sut.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(fallback);
            });

        Assert.Same(fallback, output);
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(Behaviors))]
    public async Task ConcurrentJoiner_SeesInnerFailure(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, ctx) = Create(kind, RealCache());
        var failure = new InvalidOperationException("downstream");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next = async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            throw failure;
        };

        var first = sut.HandleAsync(ctx, ct, next).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var joiner = sut.HandleAsync(ctx, ct, next).AsTask();
        release.SetResult();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => joiner));
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(Behaviors))]
    public async Task InnerCancellationNotFromCaller_PropagatesWithoutRerunning(string kind)
    {
        // An HttpClient timeout surfaces as a TaskCanceledException while the caller's token is
        // not cancelled. It is the pipeline's failure, so it must not run the pipeline again.
        var (sut, ctx) = Create(kind, RealCache());
        var timeout = new TaskCanceledException("request timed out");
        var calls = 0;

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await sut.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException<IReadOnlyList<SearchResult>>(timeout);
            }));

        Assert.Same(timeout, ex);
        Assert.Equal(1, calls);
    }

    /// <summary>A cache that fails without ever invoking the factory.</summary>
    private sealed class ThrowingHybridCache(Exception exception) : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<T>(exception);

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
}
