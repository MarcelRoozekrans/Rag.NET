using Microsoft.Extensions.Logging;

namespace Rag.NET.DataProviders.Linear;

internal static partial class LinearLog
{
    [LoggerMessage(
        EventName = "pagination_stopped_malformed_page",
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Linear pagination stopped on a malformed page: hasNextPage was true but endCursor was '{EndCursor}'; traversal is incomplete and the watermark is withheld — keep the previous DeltaToken")]
    internal static partial void PaginationStoppedMalformedPage(
        ILogger logger, string? endCursor);

    [LoggerMessage(
        EventName = "comments_truncated",
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Linear issue {Identifier} has more than {First} comments; the remainder was not fetched — the entry is flagged comments_truncated")]
    internal static partial void CommentsTruncated(
        ILogger logger, string identifier, int first);
}
