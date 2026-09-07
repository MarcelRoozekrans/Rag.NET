namespace Rag.NET.Abstractions;

/// <summary>
/// A store holding data derived from documents, which must forget a document when that document
/// is deleted or replaced.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a store outside <c>Rag.NET</c> had no way to be told.</b>
/// <c>PipelineIngestor.DeleteAsync</c> clears the vector store, the BM25 index, parent chunks, the
/// data manager and the embedding-version store — every one of them an interface in this assembly,
/// injected optionally. <c>IRaptorLeafStore</c> lives in <c>Rag.NET.Raptor.Store</c>, which core
/// cannot reference (the dependency runs the other way), so deletion never reached it and a deleted
/// document's text was read back on the next corpus build and stored as a searchable summary under
/// no document id — issue #338.
/// </para>
/// <para>
/// <b>It is deliberately one method and no marker properties.</b> Anything richer would oblige
/// implementers to describe themselves for a caller that only ever needs to say "forget this
/// document". Implementations are resolved as a collection, so several may coexist and a package
/// registering one needs no coordination with core.
/// </para>
/// <para>
/// <b>Removal must be idempotent.</b> It is called on the delete path and on the overwrite path,
/// and a document that was never stored is not an error — the same contract
/// <c>IParentChunkStore.Remove</c> and <c>IEmbeddingVersionStore.RemoveAsync</c> already keep.
/// </para>
/// </remarks>
public interface IDocumentScopedStore
{
    /// <summary>Removes everything this store holds that was derived from a document.</summary>
    /// <param name="documentId">The document being deleted or replaced.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Idempotent: a document this store never held removes nothing and does not throw.
    /// </remarks>
    Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}
