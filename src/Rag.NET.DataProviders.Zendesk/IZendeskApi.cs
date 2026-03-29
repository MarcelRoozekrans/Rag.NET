using Refit;

namespace Rag.NET.DataProviders.Zendesk;

[Headers("Accept: application/json")]
internal interface IZendeskApi
{
    [Get("/api/v2/incremental/tickets/cursor.json")]
    Task<ZendeskIncrementalTicketResult> GetIncrementalTicketsAsync(
        [Query("start_time")] long startTime,
        [Query("cursor")] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/tickets/{ticketId}/comments")]
    Task<ZendeskCommentPage> GetTicketCommentsAsync(
        long ticketId,
        CancellationToken cancellationToken = default);

    [Get("/api/v2/help_center/incremental/articles.json")]
    Task<ZendeskIncrementalArticleResult> GetIncrementalArticlesAsync(
        [Query("start_time")] long startTime,
        CancellationToken cancellationToken = default);
}
