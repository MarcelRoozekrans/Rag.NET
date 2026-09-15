using System.Text.Json.Serialization;

namespace Rag.NET.WebSearch.Tavily;

public sealed record TavilySearchRequest
{
    // The API key is NOT a body field. It carried [JsonPropertyName("api_key")] here until #625,
    // which is the shape Tavily deprecated: the documented method is an Authorization: Bearer
    // header, their own Python SDK sends the key only in that header, and dev-tier keys reject the
    // body form outright. AddTavilyWebSearch sets the header; nothing here carries a credential.
    [JsonPropertyName("query")]       public required string Query      { get; init; }
    [JsonPropertyName("max_results")] public int MaxResults             { get; init; } = 5;
}
