using Microsoft.ML.Tokenizers;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

/// <summary>
/// Bounds retrieved context by <b>length</b>, dropping the lowest-ranked chunks until the set fits
/// <see cref="Rag.NET.Models.Options.RetrievalOptions.MaxContextTokens"/>.
/// <para>
/// <b>Why counting chunks was never a length bound.</b> <c>TopK</c> bounds how many chunks come
/// back and <c>MinScore</c> bounds how relevant they are; neither bounds how long they are.
/// <c>TopK = 5</c> over a corpus chunked at 4,000 characters is a completely different prompt from
/// <c>TopK = 5</c> over one chunked at 500, so changing a <i>chunking</i> decision made at
/// ingestion silently changed how close every query ran to the model's context limit — with no
/// error until the model rejected the request (issue #85).
/// </para>
/// <para>
/// <b>Chunks are dropped whole, never truncated.</b> A chunk cut mid-sentence is evidence the
/// answer engine can still cite while no longer supporting what it says, which is worse than one
/// that is simply absent. <c>ConversationMemoryPipeline</c> reached the same conclusion for
/// conversation history and drops whole messages; this is the same rule for the other half of the
/// prompt.
/// </para>
/// <para>
/// <b>It runs before reordering, and that ordering is the point.</b> In the behaviour chain this
/// sits inside <see cref="LostInTheMiddleBehavior"/>, so results arrive ranked — best first, after
/// reranking, MMR and redundancy filtering have settled — and the budget drops from the tail.
/// Budgeting after reordering would drop whichever chunk ended up last, and lost-in-the-middle
/// deliberately puts the <i>weakest</i> chunk in the middle and strong ones at both ends: the
/// dropped chunk would be a mid-ranked one, chosen by position rather than by rank.
/// </para>
/// <para>
/// <b>Dropping is logged.</b> A budget that quietly discards evidence is a new way to produce "I
/// cannot find any relevant information" — the symptom issue #56 was opened with — so the count,
/// the token totals and the budget are reported rather than left to be inferred from
/// <c>RagResponse.Sources</c> by someone already suspicious.
/// </para>
/// </summary>
[Singleton]
public sealed class ContextBudgetBehavior : IRetrievalBehavior
{
    /// <summary>
    /// The same encoding <c>ConversationMemoryPipeline</c> counts with, deliberately: two token
    /// budgets over two halves of one prompt disagreeing about what a token is would make the
    /// combined total meaningless. It is an approximation for any model that does not use
    /// cl100k_base — stated here rather than implied, since the budget is the caller's number and
    /// they need to know what it is counted in.
    /// </summary>
    private static readonly Tokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(next);

        var results = await next(ctx, ct).ConfigureAwait(false);
        if (ctx.Options.MaxContextTokens is not { } budget || results.Count == 0)
        {
            return results;
        }

        return ApplyBudget(ctx, results, budget);
    }

    private static IReadOnlyList<SearchResult> ApplyBudget(
        RetrievalContext ctx, IReadOnlyList<SearchResult> results, int budget)
    {
        var kept = new List<SearchResult>(results.Count);
        var keptTokens = 0;
        var totalTokens = 0;

        for (var i = 0; i < results.Count; i++)
        {
            var tokens = s_tokenizer.CountTokens(results[i].Chunk.Text);
            totalTokens += tokens;

            // Every later chunk is ranked at or below this one, but a long chunk followed by a
            // short one must not end the scan: skipping the overlong chunk and keeping the
            // shorter one that follows fills the budget better and never drops a higher-ranked
            // chunk in favour of a lower-ranked one.
            if (keptTokens + tokens > budget)
            {
                continue;
            }

            kept.Add(results[i]);
            keptTokens += tokens;
        }

        if (kept.Count == results.Count)
        {
            return results;
        }

        RagPipelineLog.ContextBudgetDroppedChunks(
            ctx.Logger, results.Count - kept.Count, results.Count, totalTokens, budget, keptTokens);

        return kept;
    }
}
