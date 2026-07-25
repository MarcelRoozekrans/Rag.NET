using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Relay-style connection of an issue's comments.</summary>
public sealed class LinearCommentConnection
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<LinearComment> Nodes { get; init; } = [];
}
