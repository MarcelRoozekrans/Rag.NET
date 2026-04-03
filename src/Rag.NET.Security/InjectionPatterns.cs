using System.Text.RegularExpressions;

namespace Rag.NET.Security;

/// <summary>
/// Shared compiled regex for prompt injection detection.
/// Used by all regex-based sanitisers and guards in Rag.NET.Security.
/// </summary>
internal static partial class InjectionPatterns
{
    // Covers:
    //   - Role-switch phrases: "ignore previous instructions", "you are now", "act as",
    //     "disregard", "new instructions", "system prompt"
    //   - Delimiter injection: <|system|>, <|user|>, [INST], ### instruction blocks
    [GeneratedRegex(
        @"(?:ignore\s+previous\s+instructions|you\s+are\s+now|act\s+as|disregard|new\s+instructions|system\s+prompt|<\|system\|>|<\|user\|>|\[INST\]|###\s*instruction)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    internal static partial Regex InjectionPattern();
}
