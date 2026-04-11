using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraFields(
    [property: JsonPropertyName("summary")]     string Summary,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("status")]      JiraStatus Status,
    [property: JsonPropertyName("priority")]    JiraPriority? Priority,
    [property: JsonPropertyName("assignee")]    JiraUser? Assignee,
    [property: JsonPropertyName("comment")]     JiraCommentList? Comment,
    [property: JsonPropertyName("updated")]     string Updated);
