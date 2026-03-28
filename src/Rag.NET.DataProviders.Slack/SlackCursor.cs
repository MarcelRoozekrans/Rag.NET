using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

internal sealed class SlackCursor
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}
