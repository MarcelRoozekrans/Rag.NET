using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

internal sealed record BitbucketDiffstatFile(
    [property: JsonPropertyName("path")] string Path);
