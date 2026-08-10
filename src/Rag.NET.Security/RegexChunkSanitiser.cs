using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Security;

public sealed partial class RegexChunkSanitiser(
    ILogger<RegexChunkSanitiser>? logger = null) : IChunkSanitiser
{
    private readonly ILogger<RegexChunkSanitiser> _logger =
        logger ?? NullLogger<RegexChunkSanitiser>.Instance;

    public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        if (text is null) return string.Empty;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.security.sanitize");
        activity?.SetTag("security.sanitizer.type", "regex-chunk");

        try
        {
            var fileName = metadata.TryGetValue(ReservedMetadataKeys.FileName, out var fn) ? fn.ToString() : "<unknown>";
            var matchCount = 0;
            var result = InjectionPatterns.InjectionPattern().Replace(text, m =>
            {
                matchCount++;
                LogInjectionDetected(_logger, fileName, m.Value);
                return "[REDACTED]";
            });
            activity?.SetTag("security.matches.count", matchCount);
            return result;
        }
        // Non-blocking by design: RegexMatchTimeoutException and other infrastructure failures
        // return the original text unmodified. OperationCanceledException propagates as normal.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitiseFailed(_logger, ex);
            return text;
        }
    }

    [LoggerMessage(EventId = 2053114672, EventName = "log_injection_detected", Level = LogLevel.Warning,
        Message = "Prompt injection pattern detected in chunk from '{FileName}': matched '{Pattern}'.")]
    private static partial void LogInjectionDetected(ILogger logger, string fileName, string pattern);

    [LoggerMessage(EventId = 1287818186, EventName = "log_sanitise_failed", Level = LogLevel.Warning,
        Message = "RegexChunkSanitiser failed; returning original text.")]
    private static partial void LogSanitiseFailed(ILogger logger, Exception ex);
}
