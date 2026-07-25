using Microsoft.Extensions.Logging;

namespace Rag.NET.DataProviders.Exchange;

internal static partial class ExchangeMailLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exchange enumeration for mailbox {Mailbox} stopped at MaxResults={MaxResults} before all folders completed; watermark withheld — keep the previous DeltaToken")]
    internal static partial void EnumerationTruncated(ILogger logger, string mailbox, int maxResults);
}
