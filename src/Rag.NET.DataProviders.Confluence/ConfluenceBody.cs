using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

public sealed record ConfluenceBody(
    [property: JsonPropertyName("storage")] ConfluenceStorage Storage);
