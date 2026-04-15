using System.Text.Json.Serialization;

namespace Rag.NET.WebSearch.Tavily;

public sealed record TavilyResult
{
    [JsonPropertyName("title")]   public string Title   { get; init; } = "";
    [JsonPropertyName("url")]     public string Url     { get; init; } = "";
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("score")]   public double Score   { get; init; }
}
