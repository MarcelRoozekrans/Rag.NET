using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

internal sealed record NotionSearchRequest(
    [property: JsonPropertyName("filter")] NotionFilter Filter,
    [property: JsonPropertyName("page_size")] int PageSize,
    [property: JsonPropertyName("start_cursor")] string? StartCursor,
    [property: JsonPropertyName("sort")] NotionSort? Sort);
