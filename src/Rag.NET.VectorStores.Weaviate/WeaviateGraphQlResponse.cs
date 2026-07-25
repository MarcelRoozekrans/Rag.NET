using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>
/// GraphQL response envelope. Per the GraphQL spec, <c>errors</c> may be present alongside
/// or instead of <c>data</c>; the store treats any non-empty <c>errors</c> array as a
/// failure and throws naming the messages.
/// </summary>
public sealed class WeaviateGraphQlResponse
{
    /// <summary>
    /// The response payload is class-keyed — <c>data.Get.{ClassName}</c> — so the envelope
    /// cannot be statically typed: the store extracts the result array for the configured
    /// class by name from this element.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<WeaviateGraphQlError>? Errors { get; init; }
}
