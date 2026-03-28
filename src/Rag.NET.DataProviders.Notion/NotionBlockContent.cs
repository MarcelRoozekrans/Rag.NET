using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed class NotionBlockContent
{
    [JsonPropertyName("rich_text")]
    public List<NotionRichText>? RichText { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }
}
