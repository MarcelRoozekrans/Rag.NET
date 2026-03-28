using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

internal sealed class SlackChannelList
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("channels")]
    public List<SlackChannel> Channels { get; init; } = [];

    [JsonPropertyName("response_metadata")]
    public SlackCursor? ResponseMetadata { get; init; }
}
