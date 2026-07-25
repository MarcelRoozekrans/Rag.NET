using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>
/// GraphQL response envelope. Per the GraphQL spec, <c>errors</c> may be present alongside
/// or instead of <c>data</c>; the provider treats any non-empty <c>errors</c> array as a failure.
/// </summary>
public sealed class LinearGraphQlResponse
{
    [JsonPropertyName("data")]
    public LinearIssuesData? Data { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<LinearGraphQlError>? Errors { get; init; }
}
