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
