using Microsoft.Extensions.AI;
using ZeroAlloc.Validation;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for GraphRAG retrieval behaviors.</summary>
[Validate]
public sealed class GraphRagRetrievalOptions
{
    /// <summary>
    /// Hop depth for local entity traversal. Default: 1.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>UseGraphRag</c> runs through the generated <c>GraphRagRetrievalOptionsValidator</c>
    /// at registration. Traversal loops once per hop, so zero (or a negative depth) collects
    /// no neighbors and the PageRank blend in <c>GraphLocalSearchBehavior</c> silently never
    /// applies.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int LocalSearchDepth { get; set; } = 1;

    /// <summary>
    /// Top-K entities to start local traversal from. Default: 10.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>UseGraphRag</c> runs through the generated <c>GraphRagRetrievalOptionsValidator</c>
    /// at registration. <c>GraphLocalSearchBehavior</c> seeds traversal with
    /// <c>Take(LocalTopEntities)</c>, so a zero (or negative) count starts from no entities —
    /// local graph search silently disabled.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int LocalTopEntities { get; set; } = 10;

    /// <summary>
    /// Blend weight for PageRank vs. vector similarity in scoring. Default: 0.3.
    /// <para>
    /// Range: 0.0–1.0 — enforced by the validation attribute, which <c>UseGraphRag</c> runs
    /// through the generated <c>GraphRagRetrievalOptionsValidator</c> at registration.
    /// <c>GraphLocalSearchBehavior</c> scores entities as
    /// <c>(1 − w) × similarity + w × pageRank</c>, so a weight outside that range gives one
    /// term a negative coefficient — ranking silently corrupted rather than blended. The
    /// explicit finiteness rule exists because every comparison against NaN is false, so NaN
    /// is never "outside" the range — it slips through the bounds check and poisons every
    /// blended score.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default 0 since #239, and it was 0.3.</b> The two terms are not on the same scale.
    /// <c>PageRank.Compute</c> normalises to sum 1 over every entity — 62,392 on the MultiHop-RAG
    /// corpus, so the mean is 1.6e-5 and even hubs reach only ~1e-2 — while cosine similarity sits at
    /// 0.3–0.6. At <c>w = 0.3</c> a blended entity chunk therefore loses roughly 30% of its score
    /// against the unblended chunks it competes with, and the behaviour <i>demoted precisely the
    /// entity chunks it had traversed to</i>.
    /// </para>
    /// <para>
    /// <b>Measured, not reasoned.</b> At <c>w = 0</c> local search reproduced the candidate-set
    /// control's nDCG@10, Recall@10 and MRR@10 to five decimals, with a top-10 ranking identical to
    /// the control on <b>2,255 of 2,255 queries</b> — so the entire −0.02761 nDCG@10 gap between local
    /// search and that control was this blend, and nothing else in the behaviour moved anything.
    /// </para>
    /// <para>
    /// <b>The option is kept, not removed.</b> A weight has a defensible use once the scales are
    /// reconciled, which #239 point 2 owns; what is not defensible is a default that costs quality by
    /// construction. Setting a non-zero weight is now an opt-in, and
    /// <see cref="GraphLocalSearchBehavior"/> skips the graph walk entirely at 0 rather than
    /// harvesting PageRank scores nothing will read.
    /// </para>
    /// </remarks>
    [InclusiveBetween(0.0, 1.0)]
    [Must(nameof(PageRankWeightIsFinite), Message = "PageRankWeight must be a finite number (not NaN or infinity).")]
    public double PageRankWeight { get; set; }

    /// <summary>Reports whether <see cref="PageRankWeight"/> is a finite number.</summary>
    /// <param name="value">The <see cref="PageRankWeight"/> value under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool PageRankWeightIsFinite(double value) => double.IsFinite(value);

    /// <summary>
    /// Reports per batch in global map phase. Null = auto. Default: null.
    /// <para>
    /// When set, must be greater than 0 — enforced by the validation attribute
    /// (<see langword="null"/> passes). <c>GraphGlobalSearchBehavior.BatchReports</c> advances
    /// its loop by this value, so zero loops forever — retrieval hangs with no error and no
    /// progress — and a negative value throws when slicing the first batch.
    /// </para>
    /// </summary>
    [GreaterThan(0, When = nameof(GlobalBatchSizeIsSet))]
    public int? GlobalBatchSize { get; set; }

    /// <summary>Reports whether <see cref="GlobalBatchSize"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="GlobalBatchSize"/> has a value.</returns>
    internal bool GlobalBatchSizeIsSet() => GlobalBatchSize is not null;

    /// <summary>
    /// How many community reports global search fetches for itself when the candidate set it was
    /// handed contains none. Default: 50.
    /// <para>
    /// <b>Without this second fetch the behavior was unreachable through the pipeline's own
    /// retrieval.</b> <c>GraphGlobalSearchBehavior</c> partitions <c>graph_type =
    /// community_report</c> chunks out of whatever the retrieval underneath returned and does
    /// nothing at all when there are none — and there were none. A corpus produces a few hundred
    /// long, general, multi-entity reports against tens of thousands of short, specific entity and
    /// article chunks, and nothing anywhere reserved the reports a slot; over a sixty-article slice
    /// not one report appeared in a dense top-500, so map-reduce never ran and global search
    /// returned its input untouched. Widening the candidate set is not a fix — it makes every
    /// retrieval pay for a shortfall that is structural.
    /// </para>
    /// <para>
    /// So the behavior now re-enters the pipeline with a metadata filter of its own, which is what
    /// a caller had to do by hand before. Must be greater than 0 when set — enforced by the
    /// validation attribute, since a non-positive value would ask the vector store for nothing and
    /// silently restore the old do-nothing behaviour. The second retrieval only happens when the
    /// first found no reports, so a pipeline already surfacing them pays nothing.
    /// </para>
    /// </summary>
    [GreaterThan(0, When = nameof(GlobalReportCandidatesIsSet))]
    public int? GlobalReportCandidates { get; set; }

    /// <summary>Reports whether <see cref="GlobalReportCandidates"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="GlobalReportCandidates"/> has a value.</returns>
    internal bool GlobalReportCandidatesIsSet() => GlobalReportCandidates is not null;

    /// <summary>Optional model for global map-reduce. Null = use DI-registered IChatClient.</summary>
    public IChatClient? GlobalChatClient { get; set; }
}
