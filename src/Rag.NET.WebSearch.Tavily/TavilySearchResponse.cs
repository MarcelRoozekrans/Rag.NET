using System.Text.Json.Serialization;

namespace Rag.NET.WebSearch.Tavily;

public sealed record TavilySearchResponse
{
    [JsonPropertyName("results")] public IReadOnlyList<TavilyResult> Results { get; init; } = [];
}
