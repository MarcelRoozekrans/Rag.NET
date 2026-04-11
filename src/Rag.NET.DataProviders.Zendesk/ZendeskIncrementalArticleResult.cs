using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Response from the Zendesk incremental articles endpoint.</summary>
public sealed class ZendeskIncrementalArticleResult
{
    [JsonPropertyName("articles")]
    public IReadOnlyList<ZendeskArticle> Articles { get; set; } = [];

    [JsonPropertyName("end_time")]
    public long EndTime { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; set; }
}
