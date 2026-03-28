using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

internal sealed class AsanaAssignee
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
