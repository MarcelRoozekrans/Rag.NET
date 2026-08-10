using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Security;

public sealed partial class PiiChunkSanitiser : IChunkSanitiser
{
    private readonly ILogger<PiiChunkSanitiser> _logger;
    private readonly (Regex Regex, string Placeholder)[] _compiled;

    public PiiChunkSanitiser(PiiDetectionOptions options, ILogger<PiiChunkSanitiser>? logger = null)
    {
        _logger = logger ?? NullLogger<PiiChunkSanitiser>.Instance;
        _compiled = options.Patterns
            .Select(p => (
                new Regex(p.RegexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
                p.Placeholder))
            .ToArray();
    }

    public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        if (text is null) return string.Empty;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.security.sanitize");
        activity?.SetTag("security.sanitizer.type", "pii-chunk");

        try
        {
            var fileName = metadata.TryGetValue(ReservedMetadataKeys.FileName, out var fn) ? fn.ToString() : "<unknown>";
            var result = text;
            var matchCount = 0;
            foreach (var (regex, placeholder) in _compiled)
            {
                result = regex.Replace(result, _ =>
                {
                    matchCount++;
                    LogPiiDetected(_logger, placeholder, fileName);
                    return placeholder;
                });
            }
            activity?.SetTag("security.matches.count", matchCount);
            return result;
        }
        catch (RegexMatchTimeoutException ex)
        {
            LogRegexTimeout(_logger, ex);
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitiseFailed(_logger, ex);
            return text;
        }
    }

    [LoggerMessage(EventId = 1097036419, EventName = "log_pii_detected", Level = LogLevel.Warning,
        Message = "PII pattern '{Placeholder}' detected in chunk from '{FileName}'.")]
    private static partial void LogPiiDetected(ILogger logger, string placeholder, string fileName);

    [LoggerMessage(EventId = 1657271217, EventName = "log_regex_timeout", Level = LogLevel.Warning,
        Message = "PII regex timed out — pattern likely too complex for input. Returning original text.")]
    private static partial void LogRegexTimeout(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 1287818186, EventName = "log_sanitise_failed", Level = LogLevel.Warning,
        Message = "PiiChunkSanitiser failed; returning original text.")]
    private static partial void LogSanitiseFailed(ILogger logger, Exception ex);
}
