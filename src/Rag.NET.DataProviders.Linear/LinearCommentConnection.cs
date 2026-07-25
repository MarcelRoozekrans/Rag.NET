using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Relay-style connection of an issue's comments.</summary>
public sealed class LinearCommentConnection
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<LinearComment> Nodes { get; init; } = [];

    /// <summary>
    /// Only <c>hasNextPage</c> is selected — a true value means the issue has more comments
    /// than the fetched page and the entry is flagged <c>comments_truncated</c>.
    /// </summary>
    [JsonPropertyName("pageInfo")]
    public LinearPageInfo PageInfo { get; init; } = new();
}
