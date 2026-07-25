using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>A comment on a Linear issue.</summary>
public sealed class LinearComment
{
    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("user")]
    public LinearUser? User { get; init; }
}
