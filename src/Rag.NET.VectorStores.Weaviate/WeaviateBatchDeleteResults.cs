using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateBatchDeleteResults
{
    [JsonPropertyName("matches")]
    public long Matches { get; init; }

    [JsonPropertyName("failed")]
    public long Failed { get; init; }

    [JsonPropertyName("successful")]
    public long Successful { get; init; }
}
