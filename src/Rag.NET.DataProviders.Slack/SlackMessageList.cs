using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackMessageList
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("messages")]
    public IList<SlackMessage> Messages { get; init; } = [];

    [JsonPropertyName("response_metadata")]
    public SlackCursor? ResponseMetadata { get; init; }
}
