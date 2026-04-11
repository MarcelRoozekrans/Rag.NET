using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

public sealed record NotionFilter(
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("value")] string Value);
