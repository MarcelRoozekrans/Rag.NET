using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
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
                    var inner = await next(ctx, ct2).ConfigureAwait(false);
                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = CachingOptions.ResultTtl },
                cancellationToken: ct).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ResultCacheFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }
}
