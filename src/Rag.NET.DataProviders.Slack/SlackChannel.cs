using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

internal sealed class SlackChannel
{
    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    internal SlackChannel(string id, string name)
    {
        Id   = id;
        Name = name;
    }
}
