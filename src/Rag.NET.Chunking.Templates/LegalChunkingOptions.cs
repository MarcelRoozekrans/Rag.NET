namespace Rag.NET.Chunking.Templates;

public sealed class LegalChunkingOptions
{
    public int MaxDepth { get; set; } = 3;

    /// <summary>
    /// One regex pattern per heading level (level 1, 2, 3, …).
    /// Each pattern is treated as the sole pattern for that level when passed
    /// to <c>HierarchicalMergerOptions.HeadingPatterns</c>.
    /// <para>
    /// The default patterns require at least one whitespace character after the
    /// numbering (e.g. <c>1. </c>, <c>1.1 </c>) — this is intentional to avoid
    /// false positives on bare decimal numbers in running text.
    /// Customise via the <c>configure</c> callback on <c>UseLegalChunking</c> if
    /// your documents omit the trailing space.
    /// </para>
    /// </summary>
    public string[] HeadingPatterns { get; set; } =
    [
        @"^\d+\.\s",
        @"^\d+\.\d+\s",
        @"^\d+\.\d+\.\d+\s",
    ];
}
