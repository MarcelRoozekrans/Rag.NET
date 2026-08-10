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
    [InclusiveBetween(0.0, 1.0)]
    [Must(nameof(PageRankWeightIsFinite), Message = "PageRankWeight must be a finite number (not NaN or infinity).")]
    public double PageRankWeight { get; set; } = 0.3;

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

    /// <summary>Optional model for global map-reduce. Null = use DI-registered IChatClient.</summary>
    public IChatClient? GlobalChatClient { get; set; }
}
