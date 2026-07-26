using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>Parallel-array upsert payload; index i across all lists describes one record.</summary>
public sealed class ChromaUpsertRequest
{
    [JsonPropertyName("ids")]
    public required IReadOnlyList<string> Ids { get; init; }

    [JsonPropertyName("embeddings")]
    public required IReadOnlyList<float[]> Embeddings { get; init; }

    [JsonPropertyName("documents")]
    public required IReadOnlyList<string> Documents { get; init; }

    /// <summary>
    /// Values are <see cref="object"/> because each record's metadata mixes string values
    /// (chunk metadata, <c>document_id</c>) with the int <c>chunk_index</c>.
    /// </summary>
    [JsonPropertyName("metadatas")]
    public required IReadOnlyList<IDictionary<string, object?>> Metadatas { get; init; }
}
