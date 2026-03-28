using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraIssue(
    [property: JsonPropertyName("id")]     string Id,
    [property: JsonPropertyName("key")]    string Key,
    [property: JsonPropertyName("fields")] JiraFields Fields);
