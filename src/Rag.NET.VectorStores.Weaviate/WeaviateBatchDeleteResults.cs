using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateBatchDeleteResults
{
    /// <summary>Weaviate's per-call deletion cap (default 10,000): when <see cref="Matches"/>
    /// reaches this value, more matching objects may remain and the caller must loop.</summary>
    [JsonPropertyName("limit")]
    public long Limit { get; init; }

    [JsonPropertyName("matches")]
    public long Matches { get; init; }

    [JsonPropertyName("failed")]
    public long Failed { get; init; }

    [JsonPropertyName("successful")]
    public long Successful { get; init; }
}
