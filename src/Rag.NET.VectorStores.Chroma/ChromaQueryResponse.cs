using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>
/// Query response: nested arrays with one inner list per query embedding (the store always
/// sends exactly one). Metadata values are <see cref="JsonElement"/> because Chroma
/// round-trips them typed (strings for chunk metadata, a JSON number for
/// <c>chunk_index</c>).
/// </summary>
public sealed class ChromaQueryResponse
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<IReadOnlyList<string>>? Ids { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<IReadOnlyList<string?>?>? Documents { get; init; }

    [JsonPropertyName("metadatas")]
    public IReadOnlyList<IReadOnlyList<Dictionary<string, JsonElement>?>?>? Metadatas { get; init; }

    /// <summary>Cosine distances (0 identical … 2 opposite) in the collection's configured space.</summary>
    [JsonPropertyName("distances")]
    public IReadOnlyList<IReadOnlyList<double>?>? Distances { get; init; }
}
