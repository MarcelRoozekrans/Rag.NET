using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateMultiTenancyConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}
