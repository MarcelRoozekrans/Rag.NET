using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>Weaviate class (collection) definition as sent to / returned by <c>/v1/schema</c>.</summary>
public sealed class WeaviateClass
{
    [JsonPropertyName("class")]
    public required string Class { get; init; }

    /// <summary>Always <c>"none"</c> — Rag.NET brings its own vectors.</summary>
    [JsonPropertyName("vectorizer")]
    public string Vectorizer { get; init; } = "none";

    [JsonPropertyName("vectorIndexConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeaviateVectorIndexConfig? VectorIndexConfig { get; init; }

    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WeaviateProperty>? Properties { get; init; }

    [JsonPropertyName("multiTenancyConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeaviateMultiTenancyConfig? MultiTenancyConfig { get; init; }
}
