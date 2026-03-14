using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Retrieval;

public sealed class RedundancyFilterRetriever(
    IRetriever inner,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : IRetriever
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var opts = options ?? new RetrievalOptions();
        if (!opts.UseRedundancyFilter)
            return results;

        return await RedundancyFilter.FilterAsync(results, embeddingGenerator, opts.RedundancyThreshold, cancellationToken)
            .ConfigureAwait(false);
    }
}
