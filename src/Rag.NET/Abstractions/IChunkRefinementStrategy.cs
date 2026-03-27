using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Post-processes chunks produced by any chunking strategy.
/// Applied by ParseBehavior after the chunking step (both per-section and document-level paths).
/// </summary>
public interface IChunkRefinementStrategy
{
    IAsyncEnumerable<TextChunk> RefineAsync(
        IAsyncEnumerable<TextChunk> chunks,
        CancellationToken cancellationToken = default);
}
