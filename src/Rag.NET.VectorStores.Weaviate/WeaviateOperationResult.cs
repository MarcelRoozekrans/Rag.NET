using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateOperationResult
{
    /// <summary><c>"SUCCESS"</c> or <c>"FAILED"</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("errors")]
    public WeaviateErrorList? Errors { get; init; }
}
