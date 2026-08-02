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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown outran the drain: the in-flight shadow capture was abandoned when the {DrainTimeout} drain deadline expired after StopAsync had already returned; it is counted in AbandonedCount and on the abandoned counter")]
    internal static partial void InFlightCaptureAbandonedAfterShutdownReport(ILogger logger, TimeSpan drainTimeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shadowed secondary pipeline failed; the failure is recorded in the captured pair as a result, and the caller's primary response was never at risk")]
    internal static partial void SecondaryRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the secondary cost ledger failed; this capture's spend is recorded as absent rather than estimated, and the capture itself is unaffected")]
    internal static partial void SecondarySpendReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduling a shadow capture failed; the caller's response was already served and is unaffected, and this sample is lost")]
    internal static partial void ShadowSchedulingFailed(ILogger logger, Exception exception);
}
