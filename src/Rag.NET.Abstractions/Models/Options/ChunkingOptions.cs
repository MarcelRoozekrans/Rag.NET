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
    /// <see cref="MaxChunkSize"/>. Default 50. Must be greater than 0; enforced by
    /// <see cref="ZeroAlloc.Validation.GreaterThanAttribute"/> at validation time.
    /// </summary>
    [GreaterThan(0)] public int Overlap { get; set; } = 50;
}
