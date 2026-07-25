using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>A Linear user reference (assignee or comment author).</summary>
public sealed class LinearUser
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
