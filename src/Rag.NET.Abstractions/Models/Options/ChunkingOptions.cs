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
    /// Throws when the two properties are individually valid but contradict each other.
    /// <para>
    /// The generated <c>ChunkingOptionsValidator</c> checks each property alone, so it cannot see
    /// that <see cref="Overlap"/> must be smaller than <see cref="MaxChunkSize"/>. Callers run
    /// both: the attribute validator first, then this.
    /// </para>
    /// <para>
    /// This exists because the three strategies disagreed about an oversized overlap and none of
    /// them said so: <c>TokenAwareChunkingStrategy</c> threw, <c>FixedSizeChunkingStrategy</c>
    /// silently degraded to no overlap at all, and <c>RecursiveChunkingStrategy</c> capped it.
    /// One configuration, three behaviours, no error. Rejecting it once, up front, is the only
    /// answer that means the same thing everywhere.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Overlap"/> is not smaller than <see cref="MaxChunkSize"/>.
    /// </exception>
    public void Validate()
    {
        if (Overlap >= MaxChunkSize)
        {
            throw new InvalidOperationException(
                $"Overlap ({Overlap}) must be smaller than MaxChunkSize ({MaxChunkSize}). An " +
                "overlap at least as large as the chunk re-reads everything the previous chunk " +
                "covered, so chunking cannot advance through the document.");
        }
    }
}
