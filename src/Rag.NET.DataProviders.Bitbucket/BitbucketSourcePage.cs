using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

internal sealed record BitbucketSourcePage(
    [property: JsonPropertyName("values")] List<BitbucketSourceEntry> Values,
    [property: JsonPropertyName("next")]   string? Next);
