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
/// Outermost decorator that caches the complete retrieval result (after all
/// post-processing: reranking, redundancy filter, reordering). On cache hit,
/// the entire inner retrieval chain is skipped.
/// </summary>
public sealed class ResultCacheRetriever(
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

        if (!opts.UseCacheResult)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var cacheKey = CacheKeyGenerator.ForResult(query, opts);

        try
        {
            var results = await cache.GetOrCreateAsync(
                cacheKey,
                async ct =>
                {
                    RagPipelineLog.ResultCacheMiss(_logger, query);
                    var innerResults = await inner.RetrieveAsync(query, options, ct).ConfigureAwait(false);
                    return innerResults as List<SearchResult> ?? innerResults.ToList();
                },
                new HybridCacheEntryOptions { Expiration = cachingOptions.ResultTtl },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ResultCacheFailed(_logger, query, ex);
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
