using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>A single GraphQL error entry; only the message is consumed.</summary>
public sealed class WeaviateGraphQlError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
