using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

public sealed record ConfluencePageList(
    [property: JsonPropertyName("results")] IReadOnlyList<ConfluencePage> Results,
    [property: JsonPropertyName("_links")]  ConfluenceLinks Links);
