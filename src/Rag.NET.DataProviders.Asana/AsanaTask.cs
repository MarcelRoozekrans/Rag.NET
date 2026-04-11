using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaTask
{
    [JsonPropertyName("gid")]
    public string Gid { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("due_on")]
    public string? DueOn { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("assignee")]
    public AsanaAssignee? Assignee { get; init; }

    [JsonPropertyName("modified_at")]
    public string? ModifiedAt { get; init; }
}
