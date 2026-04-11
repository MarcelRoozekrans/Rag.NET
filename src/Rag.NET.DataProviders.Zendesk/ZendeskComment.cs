using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>A comment on a Zendesk support ticket.</summary>
public sealed class ZendeskComment
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("author_id")]
    public long AuthorId { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;
}
