using Microsoft.Extensions.AI;
using ZeroAlloc.Validation;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for GraphRAG global search.</summary>
[Validate]
public sealed class GraphRagGlobalSearchOptions
{
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
