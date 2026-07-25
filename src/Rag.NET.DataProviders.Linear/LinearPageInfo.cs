using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Relay-style page info for cursor pagination.</summary>
public sealed class LinearPageInfo
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; init; }
}
