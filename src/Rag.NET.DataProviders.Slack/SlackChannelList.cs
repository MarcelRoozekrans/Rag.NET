using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackChannelList
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("channels")]
    public IList<SlackChannel> Channels { get; init; } = [];

    [JsonPropertyName("response_metadata")]
    public SlackCursor? ResponseMetadata { get; init; }
}
