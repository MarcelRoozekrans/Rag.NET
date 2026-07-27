using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Security;

public sealed partial class RegexChunkSanitiser(
    ILogger<RegexChunkSanitiser>? logger = null) : IChunkSanitiser
{
    private readonly ILogger<RegexChunkSanitiser> _logger =
        logger ?? NullLogger<RegexChunkSanitiser>.Instance;

    public string Sanitise(string text, IReadOnlyDictionary<string, string> metadata)
    {
        if (text is null) return string.Empty;
        try
        {
            var fileName = metadata.TryGetValue(ReservedMetadataKeys.FileName, out var fn) ? fn : "<unknown>";
            return InjectionPatterns.InjectionPattern().Replace(text, m =>
            {
                LogInjectionDetected(_logger, fileName, m.Value);
                return "[REDACTED]";
            });
        }
        // Non-blocking by design: RegexMatchTimeoutException and other infrastructure failures
        // return the original text unmodified. OperationCanceledException propagates as normal.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitiseFailed(_logger, ex);
            return text;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Prompt injection pattern detected in chunk from '{FileName}': matched '{Pattern}'.")]
    private static partial void LogInjectionDetected(ILogger logger, string fileName, string pattern);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "RegexChunkSanitiser failed; returning original text.")]
    private static partial void LogSanitiseFailed(ILogger logger, Exception ex);
}
