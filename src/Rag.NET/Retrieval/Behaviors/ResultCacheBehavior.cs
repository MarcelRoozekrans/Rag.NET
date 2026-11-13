using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton(As = typeof(ResultCacheBehavior))]
public sealed class ResultCacheBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public HybridCache? Cache { get; set; }
    [Inject(Required = false)] public CachingOptions? CachingOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCacheResult || Cache is null || CachingOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var cacheKey = CacheKeyGenerator.ForResult(ctx.Query, ctx.Options);

        try
        {
            var results = await Cache.GetOrCreateAsync(
                cacheKey,
                async ct2 =>
                {
                    IReadOnlyList<SearchResult> inner;
                    try
                    {
                        inner = await next(ctx, ct2).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InnerPipelineException(ex);
                    }

                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = CachingOptions.ResultTtl },
                cancellationToken: ct).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        // The pipeline failed, not the cache: rethrow its exception rather than run it again.
        catch (InnerPipelineException wrapped)
        {
            ExceptionDispatchInfo.Throw(wrapped.InnerException!);
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.ResultCacheFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }
}
