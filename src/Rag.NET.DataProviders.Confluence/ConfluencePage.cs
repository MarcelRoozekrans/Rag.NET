using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

public sealed record ConfluencePage(
    [property: JsonPropertyName("id")]      string Id,
    [property: JsonPropertyName("title")]   string Title,
    [property: JsonPropertyName("body")]    ConfluenceBody Body,
    [property: JsonPropertyName("version")] ConfluenceVersion Version);
