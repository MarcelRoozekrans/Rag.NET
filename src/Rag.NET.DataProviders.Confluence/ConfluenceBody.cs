using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

internal sealed record ConfluenceBody(
    [property: JsonPropertyName("storage")] ConfluenceStorage Storage);
