using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

public sealed class ChromaCreateCollectionRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Collection metadata. <c>{"hnsw:space": "cosine"}</c> selects the cosine distance
    /// space (verified against Chroma 1.0.0: the legacy metadata key is honored — the
    /// created collection's HNSW configuration reports <c>space: cosine</c>).
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>When true, an existing collection with the same name is returned instead of a 409.</summary>
    [JsonPropertyName("get_or_create")]
    public bool GetOrCreate { get; init; }
}
