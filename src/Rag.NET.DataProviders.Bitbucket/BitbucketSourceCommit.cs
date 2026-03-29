using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

internal sealed record BitbucketSourceCommit(
    [property: JsonPropertyName("hash")] string Hash);
