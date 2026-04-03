using System.Text.RegularExpressions;

namespace Rag.NET.Parsers.Vision;

/// <summary>
/// Lightweight regex guard against prompt injection in vision LLM output.
/// Replaces matched spans with [REDACTED]. Not publicly exposed — see the
/// Prompt Injection Fortification backlog item for the full IChunkSanitiser abstraction.
/// </summary>
internal static partial class PromptInjectionSanitiser
{
    internal static string Sanitise(string text, string fileName)
    {
        _ = fileName; // reserved for future logging
        return InjectionPattern().Replace(text, "[REDACTED]");
    }

    // Covers:
    //   - Role-switch phrases: "ignore previous instructions", "you are now", "act as",
    //     "disregard", "new instructions", "system prompt"
    //   - Delimiter injection: <|system|>, <|user|>, [INST], ### instruction blocks
    [GeneratedRegex(
        @"(?:ignore\s+previous\s+instructions|you\s+are\s+now|act\s+as|disregard|new\s+instructions|system\s+prompt|<\|system\|>|<\|user\|>|\[INST\]|###\s*[Ii]nstruction)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex InjectionPattern();
}
