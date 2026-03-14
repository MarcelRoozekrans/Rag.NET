using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

public sealed class RerankingRetriever(
    IRetriever inner,
    IReranker reranker,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseReranking)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var candidateCount = opts.CandidateCount ?? opts.TopK * 3;
        var expanded = opts with { TopK = candidateCount, UseReranking = false };

        var searchResults = await inner.RetrieveAsync(query, expanded, cancellationToken).ConfigureAwait(false);

        try
        {
            var reranked = await reranker.RerankAsync(query, searchResults, cancellationToken).ConfigureAwait(false);

            var results = reranked
                .OrderByDescending(r => r.RelevanceScore)
                .Take(opts.TopK)
                .Select(r => r.SearchResult)
                .ToList()
                .AsReadOnly();

            RagPipelineLog.RerankingCompleted(_logger, searchResults.Count, results.Count);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RerankingFailed(_logger, query, ex);
            return searchResults;
        }
    }
}
