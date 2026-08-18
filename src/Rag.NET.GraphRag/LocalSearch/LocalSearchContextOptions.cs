using ZeroAlloc.Validation;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// The knobs Microsoft's local search exposes on context construction, with its defaults.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is taken from <c>LocalSearchDefaults</c> in
/// <c>packages/graphrag/graphrag/config/defaults.py</c>, <b>not</b> from the defaults on
/// <c>LocalSearchMixedContext.build_context</c>'s signature. The two disagree — the signature says
/// <c>community_prop = 0.25</c> and <c>max_context_tokens = 8000</c> — and the config wins in any
/// real run because the factory passes it. The signature defaults are unreachable, and copying
/// them is the easy mistake.
/// </para>
/// <para>
/// See <c>docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md</c> for the reading these
/// come from, including the <c>gh api</c> commands to re-fetch the source.
/// </para>
/// </remarks>
[Validate]
public sealed class LocalSearchContextOptions
{
    /// <summary>Total token budget for the assembled context. Default: 12,000.</summary>
    /// <remarks>
    /// Counted in <c>cl100k_base</c> tokens, the same encoding <c>ContextBudgetBehavior</c> and
    /// <c>ConversationMemoryPipeline</c> count in — an approximation for any model that does not
    /// use it, and stated rather than implied because the budget is the caller's number.
    /// </remarks>
    [GreaterThan(0)]
    public int MaxContextTokens { get; set; } = 12_000;

    /// <summary>Fraction of the budget reserved for community reports. Default: 0.15.</summary>
    [InclusiveBetween(0.0, 1.0)]
    public double CommunityProportion { get; set; } = 0.15;

    /// <summary>Fraction of the budget reserved for source chunks. Default: 0.5.</summary>
    /// <remarks>
    /// What is left after this and <see cref="CommunityProportion"/> goes to the local section —
    /// entities, relationships and covariates — so the two proportions summing above 1 would give
    /// that section a negative budget. Rejected rather than clamped, matching upstream's
    /// <c>ValueError</c>: a silently empty entity table is the failure this whole exercise exists
    /// to stop happening quietly.
    /// </remarks>
    [InclusiveBetween(0.0, 1.0)]
    [Must(
        nameof(ProportionsLeaveRoomForTheLocalSection),
        Message = "CommunityProportion + TextUnitProportion must not exceed 1.0 — what is left over is the entity/relationship/covariate section's budget.")]
    public double TextUnitProportion { get; set; } = 0.5;

    /// <summary>Entities to seed the context from. Default: 10.</summary>
    /// <remarks>
    /// Upstream oversamples this by 2 when searching entity embeddings and — deliberately or not —
    /// never truncates back, so a default run selects up to 20 entities. That oversample belongs to
    /// the entity-selection step, not here; this is the <c>k</c> it multiplies.
    /// </remarks>
    [GreaterThan(0)]
    public int TopKEntities { get; set; } = 10;

    /// <summary>
    /// Multiplier on the entity search before selection. Default: 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Upstream oversamples and does not truncate back.</b> <c>map_query_to_entities</c> asks the
    /// entity index for <c>k × oversample_scaler</c> hits so that excluded entities can be filtered
    /// out without shrinking the selection — and then returns everything that survived. With no
    /// exclusions configured, which is the default, that means the default run selects <b>20</b>
    /// entities for a <see cref="TopKEntities"/> of 10.
    /// </para>
    /// <para>
    /// Reproduced rather than corrected, because the selection size is not cosmetic: it is the
    /// entity table's length, the multiplier on the out-of-network relationship cap, and the number
    /// of blocks the source-chunk ordering is divided into. "Fixing" it silently would make this a
    /// different retrieval system that resembled the specification. Set it to 1 to get exactly
    /// <see cref="TopKEntities"/> entities, which is what most readers expect the parameter to
    /// mean.
    /// </para>
    /// </remarks>
    [GreaterThan(0)]
    public int EntityOversampleScaler { get; set; } = 2;

    /// <summary>
    /// The length and format asked of the answer. Default: <c>multiple paragraphs</c>.
    /// </summary>
    /// <remarks>
    /// Interpolated into the prompt verbatim, as upstream's <c>response_type</c> is — so
    /// "a single sentence", "a bulleted list", or "a report of at least 1000 words" all work, and
    /// so does anything else the model will read as an instruction.
    /// </remarks>
    public string ResponseType { get; set; } = "multiple paragraphs";

    /// <summary>
    /// Multiplier on the out-of-network relationship cap. Default: 10.
    /// </summary>
    /// <remarks>
    /// <b>Not a cap of 10.</b> The cap upstream applies is
    /// <c>top_k_relationships × selected_entity_count</c>, and it applies only to out-of-network
    /// relationships — those with exactly one endpoint among the selected entities. In-network
    /// relationships, with both endpoints selected, are never truncated at all. At the defaults
    /// that is 200 out-of-network relationships plus however many in-network ones exist.
    /// </remarks>
    [GreaterThan(0)]
    public int TopKRelationships { get; set; } = 10;

    /// <summary>
    /// Whether to render each entity's degree as a column in the entity table. Default:
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Off upstream, and the only place a centrality number appears in local search at all. It is a
    /// <i>display</i> column: nothing ranks or scores by it. That is worth stating here because
    /// this library previously blended PageRank into similarity scores, which local search does
    /// not do and has never done.
    /// </remarks>
    public bool IncludeEntityRank { get; set; }

    /// <summary>Whether to render relationship weight as a column. Default: <see langword="false"/>.</summary>
    public bool IncludeRelationshipWeight { get; set; }

    /// <summary>Column separator for every rendered table. Default: <c>|</c>.</summary>
    /// <remarks>
    /// The prompt reading these tables is written against this character, so changing it means
    /// changing the prompt too.
    /// </remarks>
    public string ColumnDelimiter { get; set; } = "|";

    /// <summary>Reports whether the two proportions leave the local section a non-negative budget.</summary>
    /// <param name="value">The <see cref="TextUnitProportion"/> under validation.</param>
    /// <returns>Whether it plus <see cref="CommunityProportion"/> is at most 1.</returns>
    internal bool ProportionsLeaveRoomForTheLocalSection(double value) =>
        CommunityProportion + value <= 1.0;
}
