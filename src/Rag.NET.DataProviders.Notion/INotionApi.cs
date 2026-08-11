using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Notion;

[ZeroAllocRestClient]
internal interface INotionApi
{
    [Post("/v1/search")]
    Task<Result<NotionSearchResult, ZeroAlloc.Rest.HttpError>> SearchAsync(
        [Body] NotionSearchRequest body,
        CancellationToken cancellationToken = default);

    [Post("/v1/databases/{databaseId}/query")]
    Task<Result<NotionSearchResult, ZeroAlloc.Rest.HttpError>> QueryDatabaseAsync(
        string databaseId,
        [Body] NotionDatabaseQueryRequest body,
        CancellationToken cancellationToken = default);

    [Get("/v1/blocks/{blockId}/children")]
    Task<Result<NotionBlockList, ZeroAlloc.Rest.HttpError>> GetBlockChildrenAsync(
        string blockId,
        [Query(Name = "page_size")] int page_size = 100,
        [Query(Name = "start_cursor")] string? start_cursor = null,
        CancellationToken cancellationToken = default);
}
