using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateErrorList
{
    [JsonPropertyName("error")]
    public IReadOnlyList<WeaviateErrorMessage>? Error { get; init; }
}
