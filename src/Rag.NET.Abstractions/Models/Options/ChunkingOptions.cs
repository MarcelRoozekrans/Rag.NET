using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>Tuning for a plain <c>IChunkingStrategy</c> pass over a document's sections.</summary>
[Validate]
public sealed class ChunkingOptions
{
    /// <summary>
    /// Target maximum chunk size in characters, not tokens. Default 512. Must be greater than 0;
    /// enforced by the <c>[GreaterThan]</c> validation attribute.
    /// </summary>
    [GreaterThan(0)] public int MaxChunkSize { get; set; } = 512;

    /// <summary>
    /// Characters of overlap between consecutive chunks, in the same character units as
    /// <see cref="MaxChunkSize"/>. Default 50.
    /// <para>
    /// <b>Zero is valid</b> and means consecutive chunks do not overlap — every chunking strategy
    /// supports it. This was <c>[GreaterThan(0)]</c> until 2026-08-09, which rejected that
    /// configuration for no reason; the doc comment asserted the same wrong rule, so the code and
    /// the documentation agreed with each other and were both wrong (issue #90).
    /// </para>
    /// <para>
    /// Must also be smaller than <see cref="MaxChunkSize"/> — an overlap at least as large as the
    /// chunk means each chunk re-reads everything the last one covered, so the window cannot
    /// advance. That is a relationship between two properties rather than a bound on one, so it
    /// is enforced by <see cref="Validate"/> rather than by an attribute.
    /// </para>
    /// </summary>
    [GreaterThanOrEqualTo(0)] public int Overlap { get; set; } = 50;

    /// <summary>
    /// Reports the two properties being individually valid but contradicting each other.
    /// <para>
    /// A <c>[CustomValidation]</c> method rather than a separate public <c>Validate()</c> the
    /// caller has to remember: it runs inside the generated <c>ChunkingOptionsValidator</c>, so
    /// <b>every</b> existing caller enforces it without changing. That distinction is the whole
    /// point — the first version of this fix was a separate method, and
    /// <c>PipelineIngestor</c> called the generated validator without it, so the ingestion path
    /// silently did not enforce this rule at all. A check you only get by remembering to call it
    /// is the defect shape this repository keeps finding.
    /// </para>
    /// <para>
    /// It exists because the three strategies disagreed about an oversized overlap and none of
    /// them said so: <c>TokenAwareChunkingStrategy</c> threw, <c>FixedSizeChunkingStrategy</c>
    /// silently degraded to no overlap at all, and <c>RecursiveChunkingStrategy</c> capped it.
    /// One configuration, three behaviours, no error. Rejecting it once, up front, is the only
    /// answer that means the same thing everywhere.
    /// </para>
    /// </summary>
    /// <returns>One failure when <see cref="Overlap"/> is not smaller than <see cref="MaxChunkSize"/>.</returns>
    // Fully qualified: Rag.NET.Models.ValidationFailure is a different type in scope here, and
    // the generator's ZV0013 rejects the method outright if the return type resolves to it.
    [CustomValidation]
    public IEnumerable<ZeroAlloc.Validation.ValidationFailure> ValidateOverlapFitsChunk()
    {
        if (Overlap >= MaxChunkSize)
        {
            yield return new ZeroAlloc.Validation.ValidationFailure
            {
                PropertyName = nameof(Overlap),
                ErrorMessage =
                    $"Overlap ({Overlap}) must be smaller than MaxChunkSize ({MaxChunkSize}). " +
                    "An overlap at least as large as the chunk re-reads everything the previous " +
                    "chunk covered, so chunking cannot advance through the document.",
            };
        }
    }
}
