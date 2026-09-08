using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>
/// Fetch records by id — Chroma's <c>/get</c>, which takes ids rather than an embedding and
/// returns no distances because nothing was ranked.
/// </summary>
/// <remarks>
/// Added for the keyed chunk lookup (#318): GraphRAG's local search chooses its source chunks by
/// graph provenance, so there is no query vector that returns them and <c>/query</c> cannot serve
/// the request at all.
/// </remarks>
public sealed class ChromaGetRequest
{
    /// <summary>The record ids to fetch. Ids with no record are absent from the response.</summary>
    [JsonPropertyName("ids")]
    public required IReadOnlyList<string> Ids { get; init; }

    /// <summary>
    /// Requested payload sections. Sent explicitly — as <see cref="ChromaQueryRequest.Include"/>
    /// does — so the mapped response never depends on a server default. <c>distances</c> is not
    /// among them: a keyed fetch ranks nothing.
    /// </summary>
    [JsonPropertyName("include")]
    public required IReadOnlyList<string> Include { get; init; }
}
