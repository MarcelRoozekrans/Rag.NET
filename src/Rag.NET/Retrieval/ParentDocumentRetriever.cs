using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that replaces child chunk text with parent chunk text after retrieval.
/// Multiple children sharing the same parent are deduplicated; the parent gets the
/// highest child score.
/// </summary>
public sealed class ParentDocumentRetriever(
    IRetriever inner,
    InMemoryParentChunkStore parentStore,
    ILogger? logger = null) : IRetriever
{
    private const string ParentKeyMetadata = "_parentKey";
    private const int OverFetchMultiplier = 3;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseParentDocument)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        // Over-fetch to compensate for deduplication (multiple children → one parent)
        var expanded = opts with { TopK = opts.TopK * OverFetchMultiplier, UseParentDocument = false };
        var childResults = await inner.RetrieveAsync(query, expanded, cancellationToken).ConfigureAwait(false);

        try
        {
            return ReplaceWithParents(childResults, query, opts.TopK);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ParentDocumentFailed(_logger, query, ex);
            return childResults;
        }
    }

    private List<SearchResult> ReplaceWithParents(
        IReadOnlyList<SearchResult> childResults,
        string query,
        int topK)
    {
        // Group by parent key, taking max score per parent
        var parentGroups = new Dictionary<string, (SearchResult bestChild, double maxScore)>(StringComparer.Ordinal);
        var noParentResults = new List<SearchResult>();

        foreach (var result in childResults)
        {
            if (!result.Chunk.Metadata.TryGetValue(ParentKeyMetadata, out var parentKey))
            {
                noParentResults.Add(result);
                continue;
            }

            if (parentGroups.TryGetValue(parentKey, out var existing))
            {
                if (result.Score > existing.maxScore)
                    parentGroups[parentKey] = (result, result.Score);
            }
            else
            {
                parentGroups[parentKey] = (result, result.Score);
            }
        }

        var results = new List<SearchResult>(parentGroups.Count + noParentResults.Count);

        foreach (var (parentKey, (bestChild, maxScore)) in parentGroups)
        {
            var parts = parentKey.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parentChunkIndex)
                && parentStore.TryGet(parts[0], parentChunkIndex, out var parentText))
            {
                results.Add(new SearchResult
                {
                    Chunk = bestChild.Chunk with { Text = parentText! },
                    Score = maxScore
                });
            }
            else
            {
                // Parent not found — return child as-is
                results.Add(bestChild);
            }
        }

        results.AddRange(noParentResults);
        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        RagPipelineLog.ParentDocumentRetrieved(_logger, query, childResults.Count, results.Count);
        return results;
    }
}
