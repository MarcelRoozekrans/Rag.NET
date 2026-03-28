using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

internal sealed class SlackUser
{
    [JsonPropertyName("real_name")]
    public string RealName { get; init; } = string.Empty;
}
