using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

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

    public string Sanitise(string text, IReadOnlyDictionary<string, string> metadata)
    {
        if (text is null) return string.Empty;
        try
        {
            var fileName = metadata.TryGetValue("file_name", out var fn) ? fn : "<unknown>";
            var result = text;
            foreach (var (regex, placeholder) in _compiled)
            {
                result = regex.Replace(result, _ =>
                {
                    LogPiiDetected(_logger, placeholder, fileName);
                    return placeholder;
                });
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitiseFailed(_logger, ex);
            return text;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "PII pattern '{Placeholder}' detected in chunk from '{FileName}'.")]
    private static partial void LogPiiDetected(ILogger logger, string placeholder, string fileName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "PiiChunkSanitiser failed; returning original text.")]
    private static partial void LogSanitiseFailed(ILogger logger, Exception ex);
}
