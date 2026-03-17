using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that applies Maximal Marginal Relevance selection to retrieved results.
/// Opt-in per call: only active when <see cref="RetrievalOptions.UseMmr"/> is <see langword="true"/>.
/// </summary>
public sealed class MmrRetriever(
    IRetriever inner,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseMmr)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var candidateCount = opts.MmrCandidateCount ?? opts.TopK * 3;
        var expanded = opts with { TopK = candidateCount, UseMmr = false };

        var candidates = await inner.RetrieveAsync(query, expanded, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
            return candidates;

        try
        {
            var selected = await MmrSelector.SelectAsync(
                query, candidates, embeddingGenerator,
                topK: opts.TopK,
                lambda: opts.MmrLambda,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            RagPipelineLog.MmrSelectionCompleted(_logger, candidates.Count, selected.Count);
            return selected;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.MmrSelectionFailed(_logger, query, ex);
            return candidates;
        }
    }
}
