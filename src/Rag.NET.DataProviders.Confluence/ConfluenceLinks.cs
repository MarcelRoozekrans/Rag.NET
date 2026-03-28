using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

internal sealed record ConfluenceLinks(
    [property: JsonPropertyName("next")] string? Next);
