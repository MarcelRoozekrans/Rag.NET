namespace Rag.NET.Models;

/// <summary>
/// A chunk's identity: the document it belongs to and its position within that document.
/// </summary>
/// <remarks>
/// The same pair <c>InMemoryVectorStore</c> keys its records on, <c>RrfMerger</c> deduplicates on,
/// and every other dedup path in the pipeline uses. A chunk index alone is not an identity — it is
/// a position within one document, and index <c>0</c> exists in every document of a corpus.
/// </remarks>
/// <param name="DocumentId">The owning document.</param>
/// <param name="ChunkIndex">Position within that document. Negative for synthetic chunks.</param>
public readonly record struct ChunkKey(string DocumentId, int ChunkIndex);
