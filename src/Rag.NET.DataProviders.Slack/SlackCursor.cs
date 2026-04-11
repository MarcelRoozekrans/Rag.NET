using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackCursor
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}
