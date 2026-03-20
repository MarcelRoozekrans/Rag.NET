using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Abstractions;

/// <summary>
/// Represents a full RAG (Retrieval-Augmented Generation) pipeline that supports
/// document ingestion, semantic retrieval, and chat generation.
/// </summary>
public interface IRagPipeline
{
    /// <summary>
    /// Parses, chunks, embeds, and stores a document in the configured vector store.
    /// </summary>
    /// <param name="document">The document stream to ingest.</param>
    /// <param name="metadata">Metadata describing the document (ID, file name, content type, tags).</param>
    /// <param name="options">Optional ingestion settings such as overwrite behaviour.</param>
    /// <param name="progress">
    /// Optional progress receiver that is notified after each pipeline stage
    /// (Parsing → Chunking → Embedding → Storing).
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="Result{T,E}"/> containing an <see cref="IngestionResult"/> on success, or a <see cref="RagError"/> on failure.</returns>
    Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds the query and searches the vector store for semantically similar chunks.
    /// </summary>
    /// <param name="query">The natural-language query to search for.</param>
    /// <param name="options">Optional retrieval settings (TopK, MinScore, hybrid search, reordering).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="Result{T,E}"/> containing a list of <see cref="SearchResult"/> on success, or a <see cref="RagError"/> on failure.</returns>
    Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves relevant chunks for the query and generates a grounded answer via the registered <c>IChatClient</c>.
    /// </summary>
    /// <param name="query">The natural-language question to answer.</param>
    /// <param name="options">Optional RAG settings (TopK, system prompt, temperature, conversation history).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="RagResponse"/> containing the answer text and the source chunks used.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no <c>IChatClient</c> is registered.</exception>
    Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves relevant chunks for the query and streams the generated answer token-by-token.
    /// The first yielded update contains the source chunks; subsequent updates contain text deltas.
    /// </summary>
    /// <param name="query">The natural-language question to answer.</param>
    /// <param name="options">Optional RAG settings (TopK, system prompt, temperature, conversation history).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An async sequence of <see cref="RagStreamingUpdate"/> values.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no <c>IChatClient</c> is registered.</exception>
    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all stored chunks associated with the specified document from the vector store.
    /// </summary>
    /// <param name="documentId">The document ID to delete.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
