using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>Response of a batch delete; only the result counters are consumed.</summary>
public sealed class WeaviateBatchDeleteResponse
{
    [JsonPropertyName("results")]
    public WeaviateBatchDeleteResults? Results { get; init; }
}
