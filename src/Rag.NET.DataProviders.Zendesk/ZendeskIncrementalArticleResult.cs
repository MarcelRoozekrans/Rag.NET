using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Response from the Zendesk incremental articles endpoint (placeholder for future use).</summary>
internal sealed class ZendeskIncrementalArticleResult
{
    [JsonPropertyName("articles")]
    public List<ZendeskArticle> Articles { get; set; } = [];

    [JsonPropertyName("end_time")]
    public long EndTime { get; set; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; set; }
}
