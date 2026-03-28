using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

internal sealed record ConfluencePageList(
    [property: JsonPropertyName("results")] List<ConfluencePage> Results,
    [property: JsonPropertyName("_links")]  ConfluenceLinks Links);
