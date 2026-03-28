using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed class NotionBlock
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("paragraph")]
    public NotionBlockContent? Paragraph { get; init; }

    [JsonPropertyName("heading_1")]
    public NotionBlockContent? Heading1 { get; init; }

    [JsonPropertyName("heading_2")]
    public NotionBlockContent? Heading2 { get; init; }

    [JsonPropertyName("heading_3")]
    public NotionBlockContent? Heading3 { get; init; }

    [JsonPropertyName("bulleted_list_item")]
    public NotionBlockContent? BulletedListItem { get; init; }

    [JsonPropertyName("numbered_list_item")]
    public NotionBlockContent? NumberedListItem { get; init; }

    [JsonPropertyName("code")]
    public NotionBlockContent? Code { get; init; }

    [JsonPropertyName("quote")]
    public NotionBlockContent? Quote { get; init; }
}
