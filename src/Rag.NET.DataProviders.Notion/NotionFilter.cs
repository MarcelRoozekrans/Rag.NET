using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed record NotionFilter(
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("value")] string Value);
