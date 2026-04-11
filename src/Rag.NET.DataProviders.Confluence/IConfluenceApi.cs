using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Confluence;

[ZeroAllocRestClient]
internal interface IConfluenceApi
{
    [Get("/wiki/rest/api/content")]
    Task<Result<ConfluencePageList, ZeroAlloc.Rest.HttpError>> GetPagesAsync(
        [Query(Name = "spaceKey")] string? spaceKey,
        [Query] int limit,
        [Query] string? cursor,
        [Query(Name = "expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);

    [Get("/wiki/rest/api/content/search")]
    Task<Result<ConfluencePageList, ZeroAlloc.Rest.HttpError>> SearchPagesAsync(
        [Query] string cql,
        [Query] int limit,
        [Query] string? cursor,
        [Query(Name = "expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);
}
