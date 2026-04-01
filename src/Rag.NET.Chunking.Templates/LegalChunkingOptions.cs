namespace Rag.NET.Chunking.Templates;

public sealed class LegalChunkingOptions
{
    public int MaxDepth { get; set; } = 3;

    /// <summary>
    /// One regex pattern per heading level (level 1, 2, 3, …).
    /// Each pattern is treated as the sole pattern for that level when passed
    /// to <c>HierarchicalMergerOptions.HeadingPatterns</c>.
    /// </summary>
    public string[] HeadingPatterns { get; set; } =
    [
        @"^\d+\.\s",
        @"^\d+\.\d+\s",
        @"^\d+\.\d+\.\d+\s",
    ];
}
