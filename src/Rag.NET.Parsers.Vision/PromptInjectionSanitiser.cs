using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Rag.NET.Parsers.Vision;

/// <summary>
/// Lightweight regex guard against prompt injection in vision LLM output.
/// Replaces matched spans with [REDACTED] and emits a warning log. Not publicly
/// exposed — see the Prompt Injection Fortification backlog item for the full
/// IChunkSanitiser abstraction.
/// </summary>
internal static partial class PromptInjectionSanitiser
{
    internal static string Sanitise(string text, ILogger? logger = null, string? fileName = null)
    {
        if (text is null) return string.Empty;
        return InjectionPattern().Replace(text, m =>
        {
            if (logger is not null)
                LogInjectionDetected(logger, fileName ?? "<unknown>", m.Value);
            return "[REDACTED]";
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Prompt injection pattern detected in vision LLM output for '{FileName}': matched '{Pattern}'.")]
    private static partial void LogInjectionDetected(ILogger logger, string fileName, string pattern);

    // Covers:
    //   - Role-switch phrases: "ignore previous instructions", "you are now", "act as",
    //     "disregard", "new instructions", "system prompt"
    //   - Delimiter injection: <|system|>, <|user|>, [INST], ### instruction blocks
    [GeneratedRegex(
        @"(?:ignore\s+previous\s+instructions|you\s+are\s+now|act\s+as|disregard|new\s+instructions|system\s+prompt|<\|system\|>|<\|user\|>|\[INST\]|###\s*instruction)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex InjectionPattern();
}
