using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that caches retrieval results keyed by the embedding text
/// (i.e., <see cref="RetrievalOptions.EmbeddingTextOverride"/> ?? query).
/// On cache hit, skips the inner retriever entirely (including embedding generation).
/// </summary>
public sealed class EmbeddingCacheRetriever(
    IRetriever inner,
    HybridCache cache,
    CachingOptions cachingOptions,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseCacheEmbedding)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var textToEmbed = opts.EmbeddingTextOverride ?? query;
        var cacheKey = CacheKeyGenerator.ForEmbedding(textToEmbed);

        try
        {
            var results = await cache.GetOrCreateAsync(
                cacheKey,
                async ct =>
                {
                    RagPipelineLog.EmbeddingCacheMiss(_logger, query);
                    var innerResults = await inner.RetrieveAsync(query, options, ct).ConfigureAwait(false);
                    return innerResults as List<SearchResult> ?? innerResults.ToList();
                },
                new HybridCacheEntryOptions { Expiration = cachingOptions.EmbeddingTtl },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.EmbeddingCacheFailed(_logger, query, ex);
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
