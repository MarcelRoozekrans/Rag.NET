using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

internal sealed class SlackMessageList
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("messages")]
    public List<SlackMessage> Messages { get; init; } = [];

    [JsonPropertyName("response_metadata")]
    public SlackCursor? ResponseMetadata { get; init; }
}
