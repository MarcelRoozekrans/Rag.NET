using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Asana;

internal sealed class AsanaNextPage
{
    [JsonPropertyName("offset")]
    public string Offset { get; init; } = string.Empty;
}
