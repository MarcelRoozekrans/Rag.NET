using Microsoft.Extensions.Logging;

namespace Rag.NET.DataProviders.Exchange;

internal static partial class ExchangeMailLog
{
    [LoggerMessage(
        EventName = "enumeration_truncated_with_progress",
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Exchange enumeration for mailbox {Mailbox} stopped at MaxResults={MaxResults} in the last folder; watermark advanced to {Watermark} — the remaining backlog drains at MaxResults per run")]
    internal static partial void EnumerationTruncatedWithProgress(
        ILogger logger, string mailbox, int maxResults, string watermark);

    [LoggerMessage(
        EventName = "enumeration_truncated_withheld",
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Exchange enumeration for mailbox {Mailbox} stopped at MaxResults={MaxResults} before all folders were visited; watermark withheld — keep the previous DeltaToken")]
    internal static partial void EnumerationTruncatedWithheld(
        ILogger logger, string mailbox, int maxResults);
}
