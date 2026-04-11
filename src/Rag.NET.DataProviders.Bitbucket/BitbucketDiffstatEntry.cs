using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

public sealed record BitbucketDiffstatEntry(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("new")]    BitbucketDiffstatFile? New);
