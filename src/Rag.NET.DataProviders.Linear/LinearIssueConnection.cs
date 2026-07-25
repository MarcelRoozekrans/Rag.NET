using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Relay-style connection returned by the issues query.</summary>
public sealed class LinearIssueConnection
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<LinearIssue> Nodes { get; init; } = [];

    [JsonPropertyName("pageInfo")]
    public LinearPageInfo PageInfo { get; init; } = new();
}
