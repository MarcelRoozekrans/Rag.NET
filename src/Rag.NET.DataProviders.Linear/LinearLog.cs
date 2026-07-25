using Microsoft.Extensions.Logging;

namespace Rag.NET.DataProviders.Linear;

internal static partial class LinearLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Linear pagination stopped on a malformed page: hasNextPage was true but endCursor was '{EndCursor}'; traversal is incomplete and the watermark is withheld — keep the previous DeltaToken")]
    internal static partial void PaginationStoppedMalformedPage(
        ILogger logger, string? endCursor);
}
