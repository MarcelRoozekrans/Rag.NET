using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Confluence;

public sealed record ConfluenceVersion(
    [property: JsonPropertyName("number")] int Number);
