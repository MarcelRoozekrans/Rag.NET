using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

/// <summary>Subset of Chroma's collection configuration (<c>configuration_json</c>).</summary>
public sealed class ChromaCollectionConfiguration
{
    [JsonPropertyName("hnsw")]
    public ChromaHnswConfiguration? Hnsw { get; init; }
}
