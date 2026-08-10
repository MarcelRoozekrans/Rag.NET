using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class ParentDocumentRetrievalBehavior : IRetrievalBehavior
{
    private const int OverFetchMultiplier = 3;
    [Inject(Required = false)] public IParentChunkStore? ParentStore { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseParentDocument || ParentStore is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var childResults = await next(
            ctx with { Options = ctx.Options with { TopK = ctx.Options.TopK * OverFetchMultiplier, UseParentDocument = false } },
            ct).ConfigureAwait(false);

        try
        {
            var parentGroups = new Dictionary<string, (SearchResult best, double maxScore)>(StringComparer.Ordinal);
            var noParentResults = new List<SearchResult>();

            foreach (var result in childResults)
            {
                if (!result.Chunk.Metadata.TryGetValue(ParentChunkKeyHelper.ParentKeyMetadata, out var parentKeyValue))
                {
                    noParentResults.Add(result);
                    continue;
                }
                var parentKey = parentKeyValue.ToString();
                if (!parentGroups.TryGetValue(parentKey, out var existing) || result.Score > existing.maxScore)
                    parentGroups[parentKey] = (result, result.Score);
            }

            var results = new List<SearchResult>(parentGroups.Count + noParentResults.Count);

            foreach (var (parentKey, (best, maxScore)) in parentGroups)
            {
                var parts = parentKey.Split(':');
                if (parts.Length == 2
                    && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var pIdx)
                    && ParentStore.TryGet(parts[0], pIdx, out var parentText))
                {
                    results.Add(new SearchResult { Chunk = best.Chunk with { Text = parentText! }, Score = maxScore });
                }
                else
                {
                    results.Add(best);
                }
            }

            results.AddRange(noParentResults);
            results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            if (results.Count > ctx.Options.TopK)
                results.RemoveRange(ctx.Options.TopK, results.Count - ctx.Options.TopK);

            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ParentDocumentFailed(ctx.Logger, ctx.Query, ex);
            return childResults;
        }
    }
}
