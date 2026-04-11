using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

public sealed class NotionPage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("last_edited_time")]
    public string LastEditedTime { get; init; } = string.Empty;

    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, NotionProperty> Properties { get; init; } = new Dictionary<string, NotionProperty>(StringComparer.Ordinal);
}
