namespace Rag.NET.Chunking;

/// <summary>
/// Keeps a split offset off the middle of a Unicode character.
/// </summary>
/// <remarks>
/// <para>
/// Every chunker here measures its budget in UTF-16 code units, which is the right unit for a size
/// limit and the wrong one for a boundary. A character outside the Basic Multilingual Plane — an
/// emoji, the CJK extensions, most historic scripts — occupies two code units, and an offset that
/// lands between them cuts the character in half.
/// </para>
/// <para>
/// Each half is a lone surrogate, and a string holding one is not merely odd but invalid:
/// <see cref="string.Normalize()"/> throws <see cref="ArgumentException"/> on it. Normalization is
/// the first thing a transformer tokenizer does to its input, so a chunk split through a character
/// cannot be embedded at all — the whole ingestion fails rather than degrading. That is why this is
/// a correctness concern and not a tidiness one.
/// </para>
/// </remarks>
internal static class RuneBoundary
{
    /// <summary>Gets the nearest legal split offset at or before <paramref name="index"/>.</summary>
    /// <param name="text">The text being split.</param>
    /// <param name="index">The wanted offset, in UTF-16 code units.</param>
    /// <returns>
    /// <paramref name="index"/>, or one code unit less when splitting there would cut a surrogate
    /// pair in half.
    /// </returns>
    /// <remarks>
    /// Backing up by one is always sufficient: no encoded character exceeds two code units, so an
    /// offset can sit inside at most one pair and stepping off it cannot land inside another. The
    /// offset moves backwards rather than forwards so that a chunk never grows past its budget —
    /// callers that need forward progress guaranteed must handle the case where the character
    /// itself is wider than the budget, which no adjustment can solve.
    /// </remarks>
    internal static int AtOrBefore(string text, int index) =>
        index > 0
        && index < text.Length
        && char.IsHighSurrogate(text[index - 1])
        && char.IsLowSurrogate(text[index])
            ? index - 1
            : index;
}
