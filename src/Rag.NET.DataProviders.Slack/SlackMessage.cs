using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackMessage
{
    [JsonPropertyName("ts")]
    public string Ts { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("thread_ts")]
    public string? ThreadTs { get; init; }

    [JsonPropertyName("reply_count")]
    public int? ReplyCount { get; init; }
}
