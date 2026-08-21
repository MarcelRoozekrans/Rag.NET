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
        // Blend never over-fetches. It is the shipped default and pinned figures are measured
        // against it, so it must ask for exactly TopK and return exactly what it is given.
        if (options.Mode is not (RaptorRetrievalMode.Boost or RaptorRetrievalMode.Filter))
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }

        var topK = ctx.Options.TopK;
        var candidates = await next(
            ctx with { Options = ctx.Options with { TopK = CandidateCount(topK) } }, ct).ConfigureAwait(false);

        var adjusted = options.Mode switch
        {
            RaptorRetrievalMode.Boost => ApplyBoost(candidates),
            RaptorRetrievalMode.Filter => ApplyFilter(candidates),
            _ => candidates,
        };

        // Reduced here rather than inside each mode, so both narrow identically and neither can
        // forget. The store may hold fewer than we asked for, so this only ever truncates.
        return adjusted.Count <= topK
            ? adjusted
            : adjusted.Take(topK).ToList().AsReadOnly();
    }

    /// <summary>
    /// How many candidates to fetch before applying the mode, from
    /// <see cref="RaptorRetrievalOptions.CandidateMultiplier"/>.
    /// </summary>
    /// <remarks>
    /// Rounded up, and never below <paramref name="topK"/>: fetching fewer than the caller asked
    /// for is the under-fill the over-fetch exists to remove. At a multiplier of 1.0 this returns
    /// <paramref name="topK"/> exactly, which reproduces the behaviour from before the over-fetch
    /// existed — the control this option keeps reachable by configuration.
    /// </remarks>
    /// <param name="topK">The caller's requested result count.</param>
    /// <returns>The candidate count to request from the next stage.</returns>
    private int CandidateCount(int topK)
    {
        var scaled = System.Math.Ceiling(topK * options.CandidateMultiplier);
        return scaled >= int.MaxValue ? int.MaxValue : System.Math.Max(topK, (int)scaled);
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
