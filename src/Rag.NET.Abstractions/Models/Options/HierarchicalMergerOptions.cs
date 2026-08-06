using Rag.NET.Models;

namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for the hierarchical-merger <c>IDocumentChunkingStrategy</c>, which merges a document's
/// sections along its heading tree into chunks bounded by heading depth rather than character
/// count alone.
/// </summary>
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
