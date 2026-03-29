using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

internal sealed record BitbucketDiffstatPage(
    [property: JsonPropertyName("values")] List<BitbucketDiffstatEntry> Values,
    [property: JsonPropertyName("next")]   string? Next);
