using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>One object in a batch store. Ids are deterministic so re-storing replaces.</summary>
public sealed class WeaviateObject
{
    [JsonPropertyName("class")]
    public required string Class { get; init; }

    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("vector")]
    public required float[] Vector { get; init; }

    [JsonPropertyName("tenant")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tenant { get; init; }

    /// <summary>
    /// Object properties. Values are <see cref="object"/> because the map mixes fixed
    /// text/int properties with arbitrary metadata keys (auto-schema adds those).
    /// </summary>
    [JsonPropertyName("properties")]
    public required IDictionary<string, object?> Properties { get; init; }
}
