using Refit;

namespace Rag.NET.DataProviders.Notion;

[Headers("Accept: application/json", "Notion-Version: 2022-06-28")]
internal interface INotionApi
{
    [Post("/v1/search")]
    Task<NotionSearchResult> SearchAsync(
        [Body] NotionSearchRequest request,
        CancellationToken cancellationToken = default);

    [Get("/v1/blocks/{blockId}/children")]
    Task<NotionBlockList> GetBlockChildrenAsync(
        string blockId,
        [Query] int page_size = 100,
        [Query] string? start_cursor = null,
        CancellationToken cancellationToken = default);
}
