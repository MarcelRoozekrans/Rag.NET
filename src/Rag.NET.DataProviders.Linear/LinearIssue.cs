using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>An issue node as selected by the connector's issues query.</summary>
public sealed class LinearIssue
{
    /// <summary>Human-readable identifier, e.g. <c>ENG-123</c>.</summary>
    [JsonPropertyName("identifier")]
    public string Identifier { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Issue description in Markdown; <c>null</c> when empty.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public LinearWorkflowState? State { get; init; }

    [JsonPropertyName("project")]
    public LinearProject? Project { get; init; }

    [JsonPropertyName("assignee")]
    public LinearUser? Assignee { get; init; }

    [JsonPropertyName("team")]
    public LinearTeam? Team { get; init; }

    [JsonPropertyName("comments")]
    public LinearCommentConnection? Comments { get; init; }
}
