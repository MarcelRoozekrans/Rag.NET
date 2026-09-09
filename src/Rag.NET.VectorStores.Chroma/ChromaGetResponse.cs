using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>
/// Response to Chroma's <c>/get</c>: <b>flat</b> arrays, unlike
/// <see cref="ChromaQueryResponse"/>'s nested one-row-per-query-embedding shape.
/// </summary>
/// <remarks>
/// <para>
/// The difference is the reason this is a separate type rather than a reuse.
/// <c>/query</c> answers per query embedding, so its arrays are arrays of rows and the store takes
/// the first; <c>/get</c> answers for one set of ids, so its arrays are the records themselves.
/// Modelling <c>/get</c> with the query type would compile and then read the first record's fields
/// as if they were a whole row.
/// </para>
/// <para>
/// Metadata values are <see cref="JsonElement"/> for the same reason as on the query response:
/// Chroma round-trips them typed, so <c>chunk_index</c> arrives as a JSON number rather than a
/// string.
/// </para>
/// </remarks>
public sealed class ChromaGetResponse
{
    /// <summary>The ids that were found. Requested ids with no record are simply absent.</summary>
    [JsonPropertyName("ids")]
    public IReadOnlyList<string>? Ids { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<string?>? Documents { get; init; }

    [JsonPropertyName("metadatas")]
    public IReadOnlyList<Dictionary<string, JsonElement>?>? Metadatas { get; init; }
}
