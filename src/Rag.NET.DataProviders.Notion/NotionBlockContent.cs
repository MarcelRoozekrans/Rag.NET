using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

public sealed class NotionBlockContent
{
    [JsonPropertyName("rich_text")]
    public IReadOnlyList<NotionRichText>? RichText { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }
}
