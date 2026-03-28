using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed class NotionSearchResult
{
    [JsonPropertyName("results")]
    public List<NotionPage> Results { get; init; } = [];

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}
