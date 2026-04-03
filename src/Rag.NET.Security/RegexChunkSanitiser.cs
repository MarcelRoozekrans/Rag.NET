using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Security;

public sealed partial class RegexChunkSanitiser(
    ILogger<RegexChunkSanitiser>? logger = null) : IChunkSanitiser
{
    private readonly ILogger<RegexChunkSanitiser> _logger =
        logger ?? NullLogger<RegexChunkSanitiser>.Instance;

    public string Sanitise(string text, IDictionary<string, string> metadata)
    {
        if (text is null) return string.Empty;
        try
        {
            var fileName = metadata.TryGetValue("file_name", out var fn) ? fn : "<unknown>";
            return InjectionPatterns.InjectionPattern().Replace(text, m =>
            {
                LogInjectionDetected(_logger, fileName, m.Value);
                return "[REDACTED]";
            });
        }
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
