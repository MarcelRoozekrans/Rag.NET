using System.Text.Json.Serialization;

namespace Rag.NET.GraphRag;

internal sealed record ExtractedRelationship
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("target")]
    public string Target { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("weight")]
    public double Weight { get; init; } = 1.0;
}
