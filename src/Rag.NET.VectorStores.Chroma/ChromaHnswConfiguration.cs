using System.Text.Json.Serialization;

namespace Rag.NET.Chroma;

public sealed class ChromaHnswConfiguration
{
    /// <summary>Distance space: <c>"cosine"</c>, <c>"l2"</c> (the Chroma default), or <c>"ip"</c>.</summary>
    [JsonPropertyName("space")]
    public string? Space { get; init; }
}
