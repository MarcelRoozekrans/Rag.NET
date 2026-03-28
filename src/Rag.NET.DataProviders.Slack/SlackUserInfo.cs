using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

internal sealed class SlackUserInfo
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("user")]
    public SlackUser? User { get; init; }
}
