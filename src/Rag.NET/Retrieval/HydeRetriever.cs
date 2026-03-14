using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that generates a hypothetical document via <see cref="IHypotheticalDocumentGenerator"/>
/// and passes it as <see cref="RetrievalOptions.EmbeddingTextOverride"/> to the inner retriever.
/// The original query string is preserved for BM25/keyword search.
/// </summary>
public sealed class HydeRetriever(
    IRetriever inner,
    IHypotheticalDocumentGenerator generator,
    ILogger<HydeRetriever>? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger<HydeRetriever>.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseHyde)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        try
        {
            var hypotheticalDoc = await generator.GenerateAsync(query, cancellationToken).ConfigureAwait(false);
            return await inner.RetrieveAsync(
                query,
                opts with { UseHyde = false, EmbeddingTextOverride = hypotheticalDoc },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.HydeGenerationFailed(_logger, query, ex);
            return await inner.RetrieveAsync(query, opts with { UseHyde = false }, cancellationToken).ConfigureAwait(false);
        }
    }
}
