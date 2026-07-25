using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>A single entry of the GraphQL top-level <c>errors</c> array.</summary>
public sealed class LinearGraphQlError
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
