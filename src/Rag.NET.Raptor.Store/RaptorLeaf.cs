namespace Rag.NET.Raptor.Store;

/// <summary>A leaf chunk and its embedding vector, as persisted for corpus-level clustering.</summary>
/// <remarks>
/// Deliberately built from <see cref="string"/> and <see cref="float"/>[] rather than the core
/// <c>TextChunk</c> / <c>EmbeddedChunk</c> models, so this package needs no reference to
/// <c>Rag.NET</c> — the same standalone posture <c>Rag.NET.Graph</c> keeps.
/// </remarks>
/// <param name="DocumentId">The owning document's identifier.</param>
/// <param name="ChunkIndex">The chunk's index within its document. Unique with <paramref name="DocumentId"/>.</param>
/// <param name="Text">The chunk's text, needed to summarise the cluster it lands in.</param>
/// <param name="Embedding">The chunk's embedding vector, which is what clustering runs on.</param>
public sealed record RaptorLeaf(string DocumentId, int ChunkIndex, string Text, float[] Embedding);
