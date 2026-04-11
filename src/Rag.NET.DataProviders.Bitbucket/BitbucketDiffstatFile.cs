using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

public sealed record BitbucketDiffstatFile(
    [property: JsonPropertyName("path")] string Path);
