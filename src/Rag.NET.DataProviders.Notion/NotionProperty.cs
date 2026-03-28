using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed class NotionProperty
{
    [JsonPropertyName("title")]
    public List<NotionRichText>? Title { get; init; }
}
