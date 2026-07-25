using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>Request body for delete-by-filter (<c>DELETE /v1/batch/objects</c>).</summary>
public sealed class WeaviateBatchDeleteRequest
{
    [JsonPropertyName("match")]
    public required WeaviateBatchDeleteMatch Match { get; init; }

    [JsonPropertyName("output")]
    public string Output { get; init; } = "minimal";
}
