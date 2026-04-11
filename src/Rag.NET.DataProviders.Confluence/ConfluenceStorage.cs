using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

public sealed record ConfluenceStorage(
    [property: JsonPropertyName("value")] string Value);
