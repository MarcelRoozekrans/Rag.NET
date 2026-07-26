using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

public sealed class ChromaQueryRequest
{
    [JsonPropertyName("query_embeddings")]
    public required IReadOnlyList<float[]> QueryEmbeddings { get; init; }

    [JsonPropertyName("n_results")]
    public int NResults { get; init; }

    /// <summary>
    /// Metadata filter: <c>{key: {"$eq": value}}</c> per key, wrapped in
    /// <c>{"$and": [...]}</c> for two or more keys. Values are <see cref="object"/>
    /// because the composed shapes differ per level.
    /// </summary>
    [JsonPropertyName("where")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object?>? Where { get; init; }

    /// <summary>
    /// Requested payload sections. Sent explicitly (rather than relying on Chroma's
    /// default include) so the mapped response never depends on server defaults.
    /// </summary>
    [JsonPropertyName("include")]
    public required IReadOnlyList<string> Include { get; init; }
}
