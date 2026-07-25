using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>Request body for <c>POST /v1/batch/objects</c>.</summary>
public sealed class WeaviateBatchObjectsRequest
{
    [JsonPropertyName("objects")]
    public required IReadOnlyList<WeaviateObject> Objects { get; init; }
}
