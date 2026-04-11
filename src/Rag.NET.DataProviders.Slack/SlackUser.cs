using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackUser
{
    [JsonPropertyName("real_name")]
    public string RealName { get; init; } = string.Empty;
}
