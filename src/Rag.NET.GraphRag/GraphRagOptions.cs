using Microsoft.Extensions.AI;
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

    /// <summary>Optional cheaper model for entity extraction. Null = use DI-registered IChatClient.</summary>
    public IChatClient? ExtractionChatClient { get; set; }

    /// <summary>Optional model for community report generation. Null = use DI-registered IChatClient.</summary>
    public IChatClient? SummarizationChatClient { get; set; }
}
