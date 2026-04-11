using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraSearchResult(
    [property: JsonPropertyName("issues")] IReadOnlyList<JiraIssue> Issues,
    [property: JsonPropertyName("total")]  int Total);
