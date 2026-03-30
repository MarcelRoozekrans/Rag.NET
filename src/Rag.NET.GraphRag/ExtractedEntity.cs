using System.Text.Json.Serialization;

namespace Rag.NET.GraphRag;

internal sealed record ExtractedEntity
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";
}
