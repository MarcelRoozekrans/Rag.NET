using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

public sealed class MultiQueryRetriever(
    IRetriever inner,
    IQueryExpander queryExpander,
    MultiQueryOptions multiQueryOptions,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseMultiQuery)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> variants;
        try
        {
            variants = await queryExpander.ExpandAsync(query, multiQueryOptions.VariantCount, cancellationToken)
                .ConfigureAwait(false);
            RagPipelineLog.QueryExpansionCompleted(_logger, query, variants.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.QueryExpansionFailed(_logger, query, ex);
            variants = [];
        }

        var allQueries = new List<string>(variants.Count + 1) { query };
        allQueries.AddRange(variants);

        var tasks = allQueries.Select(q => inner.RetrieveAsync(q, options, cancellationToken)).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        return allResults
            .SelectMany(r => r)
            .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(opts.TopK)
            .ToList()
            .AsReadOnly();
    }
}
