using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

internal sealed record ConfluenceVersion(
    [property: JsonPropertyName("number")] int Number);
