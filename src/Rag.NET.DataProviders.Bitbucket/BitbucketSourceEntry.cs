using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Bitbucket;

public sealed record BitbucketSourceEntry(
    [property: JsonPropertyName("path")]   string Path,
    [property: JsonPropertyName("type")]   string Type,
    [property: JsonPropertyName("size")]   long? Size,
    [property: JsonPropertyName("commit")] BitbucketSourceCommit? Commit);
