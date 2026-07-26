using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>
/// Collection as returned by the create/get collection endpoints. Only the fields the
/// store needs — Chroma also returns configuration/schema/dimension, all ignored.
/// </summary>
public sealed class ChromaCollectionInfo
{
    /// <summary>Collection UUID — the address for all record operations.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Collection configuration; used to verify the distance space is cosine.</summary>
    [JsonPropertyName("configuration_json")]
    public ChromaCollectionConfiguration? Configuration { get; init; }
}
