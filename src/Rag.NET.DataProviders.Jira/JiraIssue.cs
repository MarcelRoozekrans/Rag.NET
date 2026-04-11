using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraIssue(
    [property: JsonPropertyName("id")]     string Id,
    [property: JsonPropertyName("key")]    string Key,
    [property: JsonPropertyName("fields")] JiraFields Fields);
