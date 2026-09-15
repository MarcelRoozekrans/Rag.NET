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
    /// <remarks>
    /// <para>
    /// <b>The leading <c>\s*</c> is load-bearing, and was missing until #636.</b> These patterns
    /// were anchored flush-left, and real legal documents indent their clauses — contracts,
    /// licences and statutes routinely do. Measured against the Apache License 2.0 verbatim, the
    /// old <c>^\d+\.\s</c> matched <b>nothing</b> while <c>^\s*\d+\.\s</c> matches all nine
    /// clauses. On a real licence the template therefore found no boundary at all and returned the
    /// whole document as a single chunk: it did not fail, it silently did nothing.
    /// </para>
    /// <para>
    /// Fifty-four unit tests did not catch it, because they hand-build their
    /// <c>DocumentSection</c> inputs and a fixture author writes <c>1. Definitions</c> flush-left —
    /// the pattern's own assumption, written into the input meant to test it.
    /// <c>RealContractExerciseTests</c> runs a real licence instead.
    /// </para>
    /// </remarks>
    public string[] HeadingPatterns { get; set; } =
    [
        @"^\s*\d+\.\s",
        @"^\s*\d+\.\d+\s",
        @"^\s*\d+\.\d+\.\d+\s",
    ];
}
