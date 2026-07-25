using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateErrorMessage
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
