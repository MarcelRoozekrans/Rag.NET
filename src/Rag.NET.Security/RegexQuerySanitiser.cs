using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Security;

public sealed partial class RegexQuerySanitiser(
    ILogger<RegexQuerySanitiser>? logger = null) : IQuerySanitiser
{
    private readonly ILogger<RegexQuerySanitiser> _logger =
        logger ?? NullLogger<RegexQuerySanitiser>.Instance;

    public string Sanitise(string query)
    {
        if (query is null) return string.Empty;
        try
        {
            var preview = query.Length > 100 ? query[..100] : query;
            return InjectionPatterns.InjectionPattern().Replace(query, m =>
            {
                LogInjectionDetected(_logger, preview, m.Value);
                return "[REDACTED]";
            });
        }
        // Non-blocking by design: RegexMatchTimeoutException and other infrastructure failures
        // return the original query unmodified. OperationCanceledException propagates as normal.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitiseFailed(_logger, ex);
            return query;
        }
    }

    [LoggerMessage(EventId = 2053114672, EventName = "log_injection_detected", Level = LogLevel.Warning,
        Message = "Prompt injection pattern detected in query '{QueryPreview}': matched '{Pattern}'.")]
    private static partial void LogInjectionDetected(ILogger logger, string queryPreview, string pattern);

    [LoggerMessage(EventId = 1287818186, EventName = "log_sanitise_failed", Level = LogLevel.Warning,
        Message = "RegexQuerySanitiser failed; returning original query.")]
    private static partial void LogSanitiseFailed(ILogger logger, Exception ex);
}
