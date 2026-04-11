using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Zendesk;

[ZeroAllocRestClient]
internal interface IZendeskApi
{
    [Get("/api/v2/incremental/tickets/cursor.json")]
    Task<Result<ZendeskIncrementalTicketResult, ZeroAlloc.Rest.HttpError>> GetIncrementalTicketsAsync(
        [Query(Name = "start_time")] long startTime,
        [Query(Name = "cursor")] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/tickets/{ticketId}/comments")]
    Task<Result<ZendeskCommentPage, ZeroAlloc.Rest.HttpError>> GetTicketCommentsAsync(
        long ticketId,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/help_center/incremental/articles.json")]
    Task<Result<ZendeskIncrementalArticleResult, ZeroAlloc.Rest.HttpError>> GetIncrementalArticlesAsync(
        [Query(Name = "start_time")] long startTime,
        CancellationToken cancellationToken = default);
}
