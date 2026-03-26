namespace Rag.NET.Models.Options;

public sealed class HierarchicalMergerOptions
{
    /// <summary>
    /// Maximum heading depth treated as chunk boundaries.
    /// Headings deeper than this are folded into their nearest in-scope ancestor as body text.
    /// </summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>
    /// Per-level regex patterns used when <see cref="DocumentSection.HeadingLevel"/> is null.
    /// <c>HeadingPatterns[0]</c> = level-1 patterns, <c>HeadingPatterns[1]</c> = level-2 patterns, etc.
    /// <see langword="null"/> means rely on the parser's heading level metadata only.
    /// </summary>
    public string[][]? HeadingPatterns { get; init; }
}
