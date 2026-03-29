using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>A Zendesk Help Center article.</summary>
internal sealed class ZendeskArticle
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("section_id")]
    public long? SectionId { get; set; }
}
