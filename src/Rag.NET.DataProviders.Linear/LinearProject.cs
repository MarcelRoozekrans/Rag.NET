using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>The project an issue belongs to.</summary>
public sealed class LinearProject
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
