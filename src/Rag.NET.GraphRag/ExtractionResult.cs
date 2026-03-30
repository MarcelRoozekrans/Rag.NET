using System.Text.Json.Serialization;

namespace Rag.NET.GraphRag;

/// <summary>Deserialized result of entity/relationship extraction from LLM.</summary>
internal sealed record ExtractionResult
{
    [JsonPropertyName("entities")]
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = [];

    [JsonPropertyName("relationships")]
    public IReadOnlyList<ExtractedRelationship> Relationships { get; init; } = [];
}
