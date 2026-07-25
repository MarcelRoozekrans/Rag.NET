using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateVectorIndexConfig
{
    [JsonPropertyName("distance")]
    public string Distance { get; init; } = "cosine";
}
