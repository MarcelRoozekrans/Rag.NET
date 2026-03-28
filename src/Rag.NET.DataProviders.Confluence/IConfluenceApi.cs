using Refit;

namespace Rag.NET.DataProviders.Confluence;

[Headers("Accept: application/json")]
internal interface IConfluenceApi
{
    [Get("/wiki/rest/api/content")]
    Task<ConfluencePageList> GetPagesAsync(
        [Query] string? spaceKey,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);

    [Get("/wiki/rest/api/content/search")]
    Task<ConfluencePageList> SearchPagesAsync(
        [Query] string cql,
        [Query] int limit,
        [Query] string? cursor,
        [Query("expand")] string expand = "body.storage,version",
        CancellationToken cancellationToken = default);
}
