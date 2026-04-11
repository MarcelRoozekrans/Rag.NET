using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

public sealed record NotionSort(
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("timestamp")] string Timestamp);
