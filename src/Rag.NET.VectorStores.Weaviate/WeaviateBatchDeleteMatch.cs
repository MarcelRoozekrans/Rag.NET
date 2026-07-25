using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateBatchDeleteMatch
{
    [JsonPropertyName("class")]
    public required string Class { get; init; }

    [JsonPropertyName("where")]
    public required WeaviateWhereFilter Where { get; init; }
}
