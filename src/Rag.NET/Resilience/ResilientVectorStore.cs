using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IVectorStore"/> decorator that executes every store call through the
/// <c>"rag-net"</c> Polly <see cref="ResiliencePipeline"/> configured by
/// <c>RagBuilder.ConfigureResilience</c>.
/// </summary>
/// <remarks>
/// Cancellation is never retried by the default policy (its retry predicate excludes
/// <see cref="OperationCanceledException"/>) and the caller's token flows into every attempt.
/// Retries assume the decorated operations are idempotent: <c>StoreAsync</c> is an upsert
/// keyed by <c>(DocumentId, ChunkIndex)</c> and delete-of-missing is a no-op across the
/// shipped stores, so a re-sent write does not duplicate.
/// The decorator owns neither the inner store nor the pipeline and is deliberately not
/// <see cref="IDisposable"/> — disposal stays with whatever registered the inner store.
/// <para>
/// Capability probes: use <see cref="Create"/> rather than the constructor. It returns a
/// <see cref="ResilientSparseVectorStore"/> when the inner store is
/// <see cref="ISparseSearchable"/>, so an <c>is ISparseSearchable</c> probe on the resolved
/// <see cref="IVectorStore"/> stays honest after decoration. <see cref="ICollectionManageable"/>
/// and <see cref="IHybridSearchable"/> are registered separately in DI by the store's own
/// <c>Use*</c> extension and therefore resolve to the undecorated store — collection
/// management and native hybrid search are not retried.
/// </para>
/// </remarks>
public class ResilientVectorStore : IVectorStore
{
    /// <summary>Creates a decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store to decorate.</param>
    /// <param name="pipeline">The resilience pipeline every call is executed through.</param>
    public ResilientVectorStore(IVectorStore inner, ResiliencePipeline pipeline)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <summary>The decorated store. Exposed so capability-forwarding subclasses can reach it.</summary>
    protected IVectorStore Inner { get; }

    /// <summary>The resilience pipeline every call is executed through.</summary>
    protected ResiliencePipeline Pipeline { get; }

    /// <summary>
    /// Creates the decorator variant that preserves <paramref name="inner"/>'s capability
    /// surface: <see cref="ResilientSparseVectorStore"/> when the inner store is
    /// <see cref="ISparseSearchable"/>, otherwise a plain <see cref="ResilientVectorStore"/>.
    /// </summary>
    public static ResilientVectorStore Create(IVectorStore inner, ResiliencePipeline pipeline) =>
        inner is ISparseSearchable
            ? new ResilientSparseVectorStore(inner, pipeline)
            : new ResilientVectorStore(inner, pipeline);

    /// <inheritdoc/>
    public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) => await state.Inner.StoreAsync(state.Chunks, ct).ConfigureAwait(false),
            (Inner, Chunks: chunks),
            cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) =>
                await state.Inner.SearchAsync(state.Query, state.Options, ct).ConfigureAwait(false),
            (Inner, Query: queryEmbedding, Options: options),
            cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) =>
                await state.Inner.DeleteByDocumentIdAsync(state.DocumentId, ct).ConfigureAwait(false),
            (Inner, DocumentId: documentId),
            cancellationToken).AsTask();
}
