using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>The team an issue belongs to, referenced by its key (e.g. <c>ENG</c>).</summary>
public sealed class LinearTeam
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;
}
