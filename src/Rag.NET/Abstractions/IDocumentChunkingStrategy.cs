using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Document-level chunking strategy that receives the full section stream for a document.
/// Use when chunking decisions require cross-section context (e.g. heading-tree merging).
/// <see cref="IChunkingStrategy"/> operates per-section; this interface operates per-document.
/// </summary>
public interface IDocumentChunkingStrategy
{
    IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
