using Microsoft.Extensions.AI;
using Rag.NET.Graph.Algorithms;
using ZeroAlloc.Validation;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for GraphRAG ingestion behaviors.</summary>
[Validate]
public sealed class GraphRagOptions
{
    /// <summary>Toggle GraphRAG on/off. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of follow-up "did I miss anything?" LLM passes per chunk. Default: 1.</summary>
    public int GleaningPasses { get; set; } = 1;

    /// <summary>
    /// Constrain entity extraction to these types. Null = open extraction. Default: null.
    /// <para>
    /// Enforced in two layers: the allowed list is substituted into
    /// <see cref="EntityExtractionPrompt"/>'s <c>{entity_types}</c> placeholder, and any entity
    /// the LLM still returns with a type outside the list is dropped (compared
    /// case-insensitively) before it reaches the graph store or the embedded chunks. The filter
    /// also applies to gleaning passes and to user-supplied prompts without the placeholder.
    /// An empty array behaves like null (open extraction) rather than silently dropping
    /// every entity.
    /// </para>
    /// </summary>
    public string[]? EntityTypes { get; set; }

    /// <summary>
    /// Constrain relationship extraction to these types. Null = open. Default: null.
    /// <para>
    /// The extraction schema expresses a relationship's kind through its <c>description</c>
    /// (a concise verb phrase), so the constraint applies to that field: the allowed list is
    /// substituted into <see cref="EntityExtractionPrompt"/>'s <c>{relationship_types}</c>
    /// placeholder, and any relationship whose description falls outside the list is dropped
    /// (compared case-insensitively) before storage — including gleaning output and
    /// user-supplied prompts without the placeholder. An empty array behaves like null.
    /// </para>
    /// </summary>
    public string[]? RelationshipTypes { get; set; }

    /// <summary>
    /// Trigger LLM summarization when accumulated entity description exceeds this length. Default: 500.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>UseGraphRag</c> runs through the generated <c>GraphRagOptionsValidator</c> at
    /// registration. <c>GraphEntityExtractionBehavior</c> truncates descriptions with
    /// <c>description[..MaxEntityDescriptionLength]</c>, so a negative threshold throws
    /// mid-ingestion on the first extracted entity, and zero silently truncates every entity
    /// description to the empty string.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int MaxEntityDescriptionLength { get; set; } = 500;

    /// <summary>
    /// The largest relationship weight an extraction may contribute to the graph. Default: 10.
    /// <para>
    /// <b>Without it, one sentence in one article decided the whole clustering.</b>
    /// <see cref="EntityExtractionPrompt"/> asks the model for <c>"weight": 1.0</c> and nothing
    /// checked the answer. Over the full 609-article MultiHop-RAG corpus the model returned
    /// acquisition prices: <c>Microsoft -&gt; Mojang</c> at 2.5e9 and <c>Microsoft -&gt; Rare</c> at
    /// 3.75e8. Two edges out of 147,021 held 99.99% of the graph's total weight — <c>m</c> =
    /// 2,875,204,647 against a per-relationship mean of 1.0009 in the sixty-article slice — and
    /// since modularity's gain term is <c>w − γ·k_i·k_c/2m</c>, the penalty for an ordinary pair
    /// fell to about 1e-9, every merge paid, and Leiden returned <b>57,484 of 62,392 entities,
    /// 92.13%, in one community</b> at modularity 0.0001. Eight weights were also non-positive or
    /// not a number, the smallest −1.00, so negatives passed too.
    /// </para>
    /// <para>
    /// <b>Bounded rather than discarded, and the measurement is why.</b> Rebuilding that graph from
    /// the same cached extractions with this ceiling at 10 alters <b>20 of 147,021</b> weights — 12
    /// clamped, 8 replaced — and moves the largest community to 5,629 entities, <b>9.02%</b>, at
    /// modularity 0.7496. Forcing every weight to 1.0 instead changes 121 of them and lands at
    /// 8.95% and 0.7492. The ordering below the ceiling is therefore worth about 0.03 points on
    /// this corpus — little, but not nothing, and discarding 147,001 usable values to correct 20
    /// bad ones is the wrong trade. A ceiling of 20,000 was also measured and is <i>worse</i>
    /// (15.05%), which says the bound has to sit near the schema's own scale rather than merely be
    /// finite. <see cref="LeidenOptions.Resolution"/> is not implicated and was not changed: a
    /// hundred-fold sweep of it moved the largest community by 0.06 points.
    /// </para>
    /// <para>
    /// <b>The sixty-article slice never saw any of this, which is why nothing caught it.</b>
    /// <c>GraphRagFunctionsTests</c> already caps the largest community's share at 25%, but over the
    /// slice the heaviest weight the model returned is 6.0 and this ceiling alters <b>no edge at
    /// all</b>. The defect needed the corpus that contained the sentence about the Mojang price.
    /// </para>
    /// <para>
    /// Must be a finite number greater than 0 — enforced by the validation attribute, which
    /// <c>UseGraphRag</c> runs at registration. Infinity would type-check and reinstate the defect;
    /// NaN would make every comparison against it false, which is the same thing written less
    /// obviously. Weights above it are clamped, not dropped, and weights that are not finite or not
    /// greater than zero are replaced with the schema's own 1.0 rather than costing the graph an
    /// edge whose endpoints and description were perfectly good. <b>Every alteration is counted</b>
    /// on the <c>ragnet.graphrag.extract</c> activity —
    /// <c>graphrag.relationship.weight.replaced</c>, <c>.clamped</c> and <c>.largest</c> — and
    /// warned about once per document, because the whole of what made this defect expensive was
    /// that it was silent.
    /// </para>
    /// </summary>
    [Must(nameof(WeightCeilingIsUsable), Message =
        "MaxRelationshipWeight must be a finite number greater than 0. Infinity restores the " +
        "unbounded behaviour this setting exists to prevent, and NaN silently disables the bound " +
        "because every comparison against it is false.")]
    public double MaxRelationshipWeight { get; set; } = 10.0;

    /// <summary>Reports whether the weight ceiling is one that can actually bound anything.</summary>
    /// <param name="value">The <see cref="MaxRelationshipWeight"/> value under validation.</param>
    /// <returns>Whether it is finite and greater than zero.</returns>
    internal bool WeightCeilingIsUsable(double value) => double.IsFinite(value) && value > 0.0;

    /// <summary>
    /// LLM prompt template for entity/relationship extraction. {text} is replaced with chunk
    /// text. {entity_types} and {relationship_types} are replaced with type guidance derived
    /// from <see cref="EntityTypes"/> and <see cref="RelationshipTypes"/> — the open-extraction
    /// guidance when they are null, the allowed list when they are set. A custom template
    /// without those placeholders still gets the constraint: out-of-list extractions are
    /// filtered after the LLM responds.
    /// </summary>
    public string EntityExtractionPrompt { get; set; } = """
        Extract all entities and relationships from the following text.
        Return a JSON object with two arrays:
        - "entities": [{"name": "...", "type": "...", "description": "..."}]
        - "relationships": [{"source": "...", "target": "...", "description": "...", "weight": 1.0}]

        {entity_types}
        {relationship_types}
        Extract ALL entities and relationships, even minor ones.

        Text:
        {text}
        """;

    /// <summary>Follow-up prompt for gleaning passes. {text} and {previous} are replaced.</summary>
    public string GleaningPrompt { get; set; } = """
        You previously extracted entities and relationships from this text.
        Your previous extraction: {previous}

        Are there any entities or relationships you missed? Look carefully for:
        - Implicit relationships
        - Minor entities mentioned in passing
        - Temporal or causal relationships

        Return ONLY the additional entities and relationships in the same JSON format.
        Return {"entities": [], "relationships": []} if nothing was missed.

        Text:
        {text}
        """;

    /// <summary>
    /// Caps a community report prompt, in characters. Default: 50,000 (roughly 12,000 tokens).
    /// <para>
    /// <b>Without it the prompt's size was a property of the corpus rather than of the code.</b>
    /// <c>CommunityDetectionBehavior</c> pasted every member entity's whole merged description into
    /// one message with no bound of any kind. Over a sixty-article slice, while Leiden was
    /// over-merging, that produced a single prompt of 1,806,352 characters — some 450,000 tokens
    /// against gpt-4o-mini's 128,000-token context, which no model could accept. Fixing the
    /// clustering brought the same prompt to 195,446 characters, which fits, but nothing in the
    /// code made it fit and a larger corpus would regrow it.
    /// </para>
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which <c>UseGraphRag</c> runs
    /// at registration. Over-budget communities are <b>truncated, not rejected</b>: members are
    /// emitted in PageRank order so the least central are dropped first, three quarters of the
    /// budget is reserved for entities and the remainder for the relationships between them, and
    /// the prompt states what was left out so the model is not shown a partial community as though
    /// it were whole. Truncation is also tagged on the <c>ragnet.graphrag.communities</c> activity.
    /// Failing an ingestion that used to work, on data rather than on configuration, would be a
    /// regression however principled it looked — so the bound degrades the report instead.
    /// </para>
    /// <para>
    /// One prompt can still exceed this: a single entity whose description alone is longer than the
    /// budget is emitted anyway, because a report prompt describing none of its community's members
    /// is indistinguishable from one for a community that holds nothing.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int MaxCommunityReportPromptLength { get; set; } = 50_000;

    /// <summary>
    /// How many community-report LLM calls may be in flight at once. Default: 4.
    /// <para>
    /// <b>Until this existed the report loop awaited one community at a time, and nothing said
    /// so.</b> Over the 609-article MultiHop-RAG corpus that is 3,587 sequential round trips —
    /// hours in a loop that is embarrassingly parallel, since each report depends only on its own
    /// community and every community is known before the first call — and the cost was found by a
    /// benchmark that had to pay for it rather than by a user, who would have seen only a long
    /// ingestion with no progress signal. Entity extraction next door had been run at twelve
    /// articles in flight the whole time.
    /// </para>
    /// <para>
    /// <b>Parallel and still deterministic.</b> Every prompt is built first, in the community
    /// order Leiden returned and in PageRank order inside each — the ordering the report cache is
    /// keyed on — and each response is written back to the community whose prompt produced it,
    /// so completion order decides nothing. Two runs at different concurrencies produce the same
    /// reports on the same communities in the same order; <c>CommunityDetectionBehaviorTests</c>
    /// asserts it.
    /// </para>
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which <c>UseGraphRag</c>
    /// runs at registration. The provider's rate limit is the real ceiling, not this number:
    /// parallelising into a 429 storm trades one wait for another, so measure against the
    /// provider before raising it. 4 is deliberately modest for that reason and matches the
    /// concurrency the map-reduce answer engine and the evaluation harness already use for calls
    /// of the same shape. Measured 2026-08-15 against OpenRouter's <c>openai/gpt-4o-mini</c>
    /// with the prompt bounded at 50,000 characters: 4.62 s per report at 1 in flight, 1.13 s at
    /// 4, 0.63 s at 8, zero retries at every level — one provider, one model, one day.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int CommunityReportConcurrency { get; set; } = 4;

    /// <summary>Prompt template for community report generation. {entities} and {relationships} are replaced.</summary>
    public string CommunityReportPrompt { get; set; } = """
        You are analyzing a community of related entities in a knowledge graph.
        Write a comprehensive summary report of this community that covers:
        - The main entities and their roles
        - Key relationships and how entities interact
        - Overall themes and significance

        Entities:
        {entities}

        Relationships:
        {relationships}

        Write a clear, informative report in 2-4 paragraphs.
        """;

    /// <summary>
    /// Settings for the Leiden clustering that community detection runs. Default: Leiden's own.
    /// <para>
    /// <b>These were unreachable until recently, which is why they are documented at length
    /// here.</b> <see cref="LeidenOptions"/> shipped public and complete, and
    /// <c>CommunityDetectionBehavior</c> called <c>Leiden.Detect(snapshot)</c> with the argument
    /// omitted — so no caller could change a single one of them and the defaults were the only
    /// settings that had ever run. That is the same shape as the three dead settings audit #108
    /// found, and it is asserted against in both directions: that the value is stored, and that
    /// changing it changes the clustering.
    /// </para>
    /// <para>
    /// <see cref="LeidenOptions.Randomness"/> is deliberately absent from the validation below, and
    /// that is not an omission: it validates in its own setter, so an unusable value cannot reach
    /// this property to be checked for. It is documented on the option itself.
    /// </para>
    /// <para>
    /// <see cref="LeidenOptions.Resolution"/> must be finite and greater than zero — enforced by
    /// the validation attribute below, which <c>UseGraphRag</c> runs at registration. It scales
    /// modularity's penalty term, so zero removes the penalty entirely and every connected graph
    /// collapses into a single community; negative inverts it and merging is rewarded without
    /// bound. <see cref="LeidenOptions.MaxIterations"/> must be greater than zero, since the local
    /// moving phase loops that many times and zero means no node ever moves — every entity its own
    /// community. <see cref="LeidenOptions.MaxLevels"/>, when set, must be greater than zero for
    /// the same reason one level up; null means "until no further improvement" and is the default.
    /// </para>
    /// </summary>
    [Must(nameof(LeidenIsUsable), Message =
        "Leiden.Resolution must be a finite number greater than 0, Leiden.MaxIterations must be " +
        "greater than 0, and Leiden.MaxLevels, when set, must be greater than 0.")]
    public LeidenOptions Leiden { get; set; } = new();

    /// <summary>
    /// How much the graph must grow before community detection runs again during ingestion.
    /// Default: <c>0.10</c> — a 10% increase in entity count. Set to <c>0</c> to detect on every
    /// document, which is the pre-#300 behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a threshold exists at all.</b> <see cref="CommunityDetectionBehavior"/> is a
    /// per-document ingestion behaviour, and detection is a whole-graph operation: it loads the
    /// entire graph, runs Leiden and PageRank over it, and writes every score back. Ingesting N
    /// documents therefore did that N times against a graph growing throughout — on a 17,648-document
    /// corpus, 17,648 whole-graph recomputes.
    /// </para>
    /// <para>
    /// <b>Nothing was gained by the repetition.</b> Detection is a pure function of the graph and
    /// each run overwrites the last, so the state after the final document is exactly the state one
    /// run at the end would produce. Every earlier run was discarded, not merged — pinned by
    /// <c>CommunityDetectionCostTests.FiveRunsLeaveTheSameCommunitiesAsOne</c>.
    /// </para>
    /// <para>
    /// <b>What the default buys.</b> Requiring 10% growth means detections happen at geometrically
    /// spaced sizes, so their number is logarithmic in the corpus rather than linear, and the total
    /// work is a geometric series bounded at roughly eleven times the final graph rather than
    /// proportional to documents times graph.
    /// </para>
    /// <para>
    /// <b>The trade, stated plainly:</b> communities can be up to this fraction stale at the end of
    /// an ingest, because the last document may not have triggered a detection. When you need them
    /// current — after a bulk load, before measuring — call
    /// <see cref="GraphProjectionRebuilder.RebuildAsync"/>, which is the operation this threshold
    /// assumes exists.
    /// </para>
    /// </remarks>
    [InclusiveBetween(0.0, 100.0)]
    [Must(nameof(GrowthThresholdIsFinite), Message =
        "CommunityDetectionGrowthThreshold must be a finite number (not NaN or infinity).")]
    public double CommunityDetectionGrowthThreshold { get; set; } = 0.10;

    /// <summary>Reports whether the growth threshold is a finite number.</summary>
    /// <param name="value">The <see cref="CommunityDetectionGrowthThreshold"/> under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool GrowthThresholdIsFinite(double value) => double.IsFinite(value);

    /// <summary>Reports whether the Leiden settings are ones the algorithm can actually run.</summary>
    /// <param name="value">The <see cref="Leiden"/> value under validation.</param>
    /// <returns>Whether every setting is inside its documented range.</returns>
    internal bool LeidenIsUsable(LeidenOptions value) =>
        value is not null
        && double.IsFinite(value.Resolution)
        && value.Resolution > 0.0
        && value.MaxIterations > 0
        && value.MaxLevels is not <= 0;

    /// <summary>Optional cheaper model for entity extraction. Null = use DI-registered IChatClient.</summary>
    public IChatClient? ExtractionChatClient { get; set; }

    /// <summary>Optional model for community report generation. Null = use DI-registered IChatClient.</summary>
    public IChatClient? SummarizationChatClient { get; set; }
}
