using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Splits a single <see cref="DocumentSection"/> into <see cref="TextChunk"/>s. Operates
/// per-section, with no visibility into a document's other sections; see
/// <see cref="IDocumentChunkingStrategy"/> for chunking that needs cross-section context.
/// </summary>
public interface IChunkingStrategy
{
    /// <summary>
    /// Chunks one section. <see cref="TextChunk.ChunkIndex"/> on the yielded chunks starts at 0
    /// and counts up within this call only — it has no knowledge of chunks produced for any other
    /// section of the same document.
    /// </summary>
    IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
