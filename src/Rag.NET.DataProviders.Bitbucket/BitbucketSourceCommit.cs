using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

public sealed record BitbucketSourceCommit(
    [property: JsonPropertyName("hash")] string Hash);
