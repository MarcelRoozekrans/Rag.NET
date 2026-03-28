using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed class NotionRichText
{
    [JsonPropertyName("plain_text")]
    public string PlainText { get; init; } = string.Empty;
}
