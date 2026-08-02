using Microsoft.Extensions.Logging;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>Source-generated log messages for the shadow capture pipeline.</summary>
/// <remarks>
/// Neither message includes the captured question or any other payload text: captures hold
/// verbatim user input, and a log line is a store nobody chose deliberately.
/// </remarks>
internal static partial class ShadowLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting a shadow capture failed; the capture is lost, recorded as a persistence failure, and the consumer continues with the next")]
    internal static partial void CapturePersistenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown drain incomplete: {AbandonedCount} shadow capture(s) were still unpersisted when the {DrainTimeout} drain deadline expired; the real capture rate is below the configured sample rate by exactly this amount")]
    internal static partial void CapturesAbandonedAtShutdown(ILogger logger, long abandonedCount, TimeSpan drainTimeout);
}
