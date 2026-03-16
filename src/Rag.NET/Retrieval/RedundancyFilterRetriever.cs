using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that filters near-duplicate results by cosine similarity.
/// </summary>
public sealed partial class RedundancyFilterRetriever(
    IRetriever inner,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redundancy filtering failed, returning unfiltered results")]
    private partial void LogFilteringFailed(Exception ex);

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var opts = options ?? new RetrievalOptions();
        if (!opts.UseRedundancyFilter)
            return results;

        try
        {
            return await RedundancyFilter.FilterAsync(results, embeddingGenerator, opts.RedundancyThreshold, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogFilteringFailed(ex);
            return results;
        }
    }
}
