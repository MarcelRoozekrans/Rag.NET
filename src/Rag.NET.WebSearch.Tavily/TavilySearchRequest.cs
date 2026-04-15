using System.Text.Json.Serialization;

namespace Rag.NET.WebSearch.Tavily;

public sealed record TavilySearchRequest
{
    [JsonPropertyName("api_key")]     public required string ApiKey     { get; init; }
    [JsonPropertyName("query")]       public required string Query      { get; init; }
    [JsonPropertyName("max_results")] public int MaxResults             { get; init; } = 5;
}
