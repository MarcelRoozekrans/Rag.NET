using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

public sealed record ConfluenceLinks(
    [property: JsonPropertyName("next")] string? Next);
