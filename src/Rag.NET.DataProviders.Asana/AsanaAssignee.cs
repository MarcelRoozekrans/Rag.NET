using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaAssignee
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
