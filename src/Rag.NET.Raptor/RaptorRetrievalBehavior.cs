using System.Globalization;
using Rag.NET.Models;
using Rag.NET.Retrieval;

namespace Rag.NET.Raptor;

/// <summary>
/// Retrieval behavior that adjusts scoring/filtering based on RAPTOR tree levels.
/// Position: before RerankingBehavior in the retrieval pipeline.
/// </summary>
public sealed class RaptorRetrievalBehavior(RaptorRetrievalOptions options) : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        return options.Mode switch
        {
            RaptorRetrievalMode.Blend => results,
            RaptorRetrievalMode.Boost => ApplyBoost(results),
            RaptorRetrievalMode.Filter => ApplyFilter(results),
            _ => results,
        };
    }

    private IReadOnlyList<SearchResult> ApplyBoost(IReadOnlyList<SearchResult> results)
    {
        return results
            .Select(r =>
            {
                var level = GetRaptorLevel(r);
                return level > 0
                    ? r with { Score = r.Score * options.SummaryBoostFactor }
                    : r;
            })
            .OrderByDescending(r => r.Score)
            .ToList()
            .AsReadOnly();
    }

    private IReadOnlyList<SearchResult> ApplyFilter(IReadOnlyList<SearchResult> results)
    {
        return results
            .Where(r =>
            {
                var level = GetRaptorLevel(r);
                if (options.MinRaptorLevel.HasValue && level < options.MinRaptorLevel.Value)
                    return false;
                if (options.MaxRaptorLevel.HasValue && level > options.MaxRaptorLevel.Value)
                    return false;
                return true;
            })
            .ToList()
            .AsReadOnly();
    }

    private static int GetRaptorLevel(SearchResult r)
        => r.Chunk.Metadata.TryGetValue("raptor_level", out var levelValue)
           && int.TryParse(levelValue.ToString(), CultureInfo.InvariantCulture, out var level)
            ? level
            : 0;
}
