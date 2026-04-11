using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaNextPage
{
    [JsonPropertyName("offset")]
    public string Offset { get; init; } = string.Empty;
}
