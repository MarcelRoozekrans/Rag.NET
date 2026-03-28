using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

internal sealed record ConfluenceStorage(
    [property: JsonPropertyName("value")] string Value);
