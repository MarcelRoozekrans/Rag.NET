using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

public sealed record BitbucketDiffstatPage(
    [property: JsonPropertyName("values")] IList<BitbucketDiffstatEntry> Values,
    [property: JsonPropertyName("next")]   string? Next);
