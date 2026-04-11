using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackChannel
{
    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    public SlackChannel(string id, string name)
    {
        Id   = id;
        Name = name;
    }
}
