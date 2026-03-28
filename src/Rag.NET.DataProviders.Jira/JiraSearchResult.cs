using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraSearchResult(
    [property: JsonPropertyName("issues")] List<JiraIssue> Issues,
    [property: JsonPropertyName("total")]  int Total);
