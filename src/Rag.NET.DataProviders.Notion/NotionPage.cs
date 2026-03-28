using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed class NotionPage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("last_edited_time")]
    public string LastEditedTime { get; init; } = string.Empty;

    [JsonPropertyName("properties")]
    public Dictionary<string, NotionProperty> Properties { get; init; } = [];
}
