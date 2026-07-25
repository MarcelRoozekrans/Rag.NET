using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>A tenant of a multi-tenancy-enabled class.</summary>
public sealed class WeaviateTenant
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}
