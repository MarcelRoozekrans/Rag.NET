using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

public sealed class NotionProperty
{
    [JsonPropertyName("title")]
    public IReadOnlyList<NotionRichText>? Title { get; init; }
}
