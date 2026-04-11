using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

public sealed record BitbucketSourcePage(
    [property: JsonPropertyName("values")] IList<BitbucketSourceEntry> Values,
    [property: JsonPropertyName("next")]   string? Next);
