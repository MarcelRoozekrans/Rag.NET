using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class EmbeddingCacheBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public HybridCache? Cache { get; set; }
    [Inject(Required = false)] public CachingOptions? CachingOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        // EmbeddingOverride carries an already-computed vector — there is no text key to
        // cache under, so the cache is bypassed entirely.
        if (!ctx.Options.UseCacheEmbedding || Cache is null || CachingOptions is null
            || ctx.Options.EmbeddingOverride is { IsEmpty: false })
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }

        var textToEmbed = ctx.Options.EmbeddingTextOverride ?? ctx.Query;
        var cacheKey = CacheKeyGenerator.ForEmbedding(textToEmbed);

        try
        {
            var results = await Cache.GetOrCreateAsync(
                cacheKey,
                async ct2 =>
                {
                    var inner = await next(ctx, ct2).ConfigureAwait(false);
                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = CachingOptions.EmbeddingTtl },
                cancellationToken: ct).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.EmbeddingCacheFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }
}
