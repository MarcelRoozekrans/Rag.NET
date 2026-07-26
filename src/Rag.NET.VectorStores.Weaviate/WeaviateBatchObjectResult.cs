using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>Per-object outcome in the batch-objects response array.</summary>
public sealed class WeaviateBatchObjectResult
{
    [JsonPropertyName("result")]
    public WeaviateOperationResult? Result { get; init; }
}
