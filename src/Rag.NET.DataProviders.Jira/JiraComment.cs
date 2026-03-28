using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraComment(
    [property: JsonPropertyName("author")]  JiraUser Author,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("body")]    string Body);
