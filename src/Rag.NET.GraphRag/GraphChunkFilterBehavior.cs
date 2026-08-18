using Rag.NET.Models;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag;

/// <summary>
/// Removes the synthetic chunks GraphRAG indexes — entities, relationships and community reports —
/// from what retrieval returns, after the graph behaviours have used them.
/// </summary>
/// <remarks>
/// <para>
/// <b>#247, the largest cost any measurement in this repository has found.</b>
/// <see cref="GraphEntityExtractionBehavior"/> and <see cref="CommunityDetectionBehavior"/> embed
/// their output into the <i>same</i> vector store as the article chunks — on MultiHop-RAG, 299,916
/// entity and relationship chunks plus 3,587 reports beside 17,648 article chunks — and dense
/// retrieval treats them as peers of the text. Measured:
/// </para>
/// <list type="bullet">
/// <item>rankings: 0.63967 nDCG@10 over the article-only store, 0.59658 over the graph store with
/// depth and chunking held constant — <b>−0.043 is pollution alone</b>;</item>
/// <item>answers: 0.350 accuracy against <b>0.138</b> over the graph store — <b>−0.21</b>, because
/// a six-chunk window fills with entity and report text instead of article text.</item>
/// </list>
/// <para>
/// <b>Filtering recovers all of it.</b> The measured <c>filtered</c> arm reproduced the article-only
/// <c>dense</c> arm <i>to four decimals on both scoring rules</i>. And the mechanism was confirmed
/// independently rather than inferred from the delta: the run generated 4 answers of 50 and the
/// other 46 hit a prompt-keyed cache, so for 46 of 50 queries the filtered context was
/// <b>byte-identical</b> to the article-only context. The synthetic chunks are pure displacement —
/// they evict article chunks from the window without changing which article chunks would win it.
/// </para>
/// <para>
/// <b>Position matters and is not incidental.</b> This runs <i>outside</i> the graph search
/// behaviours, so they still see every synthetic chunk and can traverse, blend and summarise with
/// them; only what reaches the caller is filtered. Placing it inside would starve the thing it is
/// meant to make usable.
/// </para>
/// <para>
/// <b><c>global_answer</c> is deliberately kept.</b> <see cref="GraphGlobalSearchBehavior"/> tags its
/// synthesised answer with <c>graph_type</c> like everything else, so a filter that went by the tag
/// alone would delete global search's entire output. What is filtered is the <i>indexed</i> synthetic
/// corpus, not everything the graph produces.
/// </para>
/// </remarks>
/// <param name="options">Supplies the toggle and the over-fetch multiplier.</param>
public sealed class GraphChunkFilterBehavior(GraphRagRetrievalOptions options) : IRetrievalBehavior
{
    /// <summary>The tags this removes. <c>global_answer</c> is absent on purpose — see the remarks.</summary>
    private static readonly string[] IndexedSyntheticKinds =
        ["entity", "relationship", "community_report"];

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(next);

        if (!options.FilterGraphChunksFromResults)
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }

        // Over-fetch, then filter, then cut — the same shape MmrBehavior uses, and the only option
        // in #247 that needs no store change, no filter-contract change and no re-indexing. The tags
        // it reads are already written at ingest, so an existing store needs nothing done to it.
        var requested = ctx.Options.TopK;
        var candidateCount = requested * options.GraphChunkOverFetchFactor;

        var candidates = await next(
            ctx with { Options = ctx.Options with { TopK = candidateCount } }, ct).ConfigureAwait(false);

        var kept = new List<SearchResult>(Math.Min(requested, candidates.Count));
        for (var i = 0; i < candidates.Count && kept.Count < requested; i++)
        {
            if (!IsIndexedSynthetic(candidates[i]))
            {
                kept.Add(candidates[i]);
            }
        }

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.filter");
        activity?.SetTag("graphrag.filter.requested", requested);
        activity?.SetTag("graphrag.filter.fetched", candidates.Count);
        activity?.SetTag("graphrag.filter.kept", kept.Count);

        // Under-filling is the one way this can quietly cost recall: on a store where synthetic
        // chunks outnumber article chunks — 17:1 on MultiHop-RAG — an over-fetch factor that is too
        // small returns fewer results than the caller asked for. Tagged so it is visible in a trace
        // rather than inferred from a short answer.
        activity?.SetTag("graphrag.filter.underfilled", kept.Count < requested && candidates.Count >= candidateCount);

        return kept;
    }

    private static bool IsIndexedSynthetic(SearchResult result)
    {
        if (!result.Chunk.Metadata.TryGetValue("graph_type", out var graphType))
        {
            return false;
        }

        var value = graphType.ToString();
        foreach (var kind in IndexedSyntheticKinds)
        {
            if (string.Equals(value, kind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
