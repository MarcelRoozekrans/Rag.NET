using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Post-processes chunks produced by any chunking strategy.
/// Applied by ParseBehavior after the chunking step (both per-section and document-level paths).
/// </summary>
public interface IChunkRefinementStrategy
{
    /// <summary>
    /// Transforms a stream of freshly-chunked <see cref="TextChunk"/>s into a refined stream —
    /// for example splitting oversized chunks, merging undersized ones, or filtering some out
    /// entirely. The output count need not match the input count.
    /// </summary>
    IAsyncEnumerable<TextChunk> RefineAsync(
        IAsyncEnumerable<TextChunk> chunks,
        CancellationToken cancellationToken = default);
}
