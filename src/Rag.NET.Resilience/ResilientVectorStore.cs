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
/// <see cref="IVectorStore"/> stays honest after decoration. <see cref="IScoreScaleAware"/>
/// needs no variant: the decorator always implements it and delegates (see
/// <see cref="ScoreScale"/>). <see cref="ICollectionManageable"/> and
/// <see cref="IHybridSearchable"/> are registered separately in DI by the store's own
/// <c>Use*</c> extension and therefore resolve to the undecorated store — collection
/// management and native hybrid search are not retried.
/// </para>
/// </remarks>
public class ResilientVectorStore : IVectorStore, IScoreScaleAware, IChunkLookup, IVectorStoreDecorator
{
    /// <summary>Creates a decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store to decorate.</param>
    /// <param name="pipeline">The resilience pipeline every call is executed through.</param>
    /// <remarks>
    /// Deliberately not public: constructing this type directly over an
    /// <see cref="ISparseSearchable"/> store would silently drop the sparse capability —
    /// the degradation <see cref="Create"/> exists to prevent. <see cref="Create"/> is
    /// therefore the only public way to obtain this type, and it picks the variant matching
    /// the inner store's capabilities.
    /// </remarks>
    private protected ResilientVectorStore(IVectorStore inner, ResiliencePipeline pipeline)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <summary>The decorated store. Exposed so capability-forwarding subclasses can reach it.</summary>
    private protected IVectorStore Inner { get; }

    /// <summary>The resilience pipeline every call is executed through.</summary>
    private protected ResiliencePipeline Pipeline { get; }

    /// <summary>
    /// The runtime <see cref="Type"/> of the decorated store, for diagnostics that need to name
    /// the store rather than the decorator (<see cref="IVectorStoreDecorator"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately the type and not the store itself: <see cref="Inner"/> stays
    /// <c>private protected</c> so no caller can reach around the pipeline, while a log line
    /// such as persistent memory's opaque-scale warning — whose entire job is naming the store
    /// responsible for the score scale — can still say <c>FederatedVectorStore</c> instead of
    /// <c>ResilientVectorStore</c>. Since the package decomposition that probe goes through
    /// <see cref="IVectorStoreDecorator"/>, so <c>Rag.NET.Memory</c> needs no reference to this
    /// package. Decoration is idempotent (<c>ConfigureResilience</c> never stacks a second
    /// layer), so one level of unwrapping is all there is.
    /// </remarks>
    public Type InnerStoreType => Inner.GetType();

    /// <summary>
    /// The inner store's declared scale, or <see cref="ScoreScale.Similarity"/> when it
    /// declares none — the documented meaning of the interface's absence.
    /// </summary>
    /// <remarks>
    /// Implemented unconditionally rather than split into a variant (the way
    /// <see cref="ISparseSearchable"/> is): the capability has a defined value for absence,
    /// so delegating is exactly semantics-preserving in both directions. Without it a
    /// decorated <c>FederatedVectorStore</c> would read as <see cref="ScoreScale.Similarity"/>
    /// and consumers such as persistent memory would threshold RRF scores that peak near
    /// <c>0.033</c> against a similarity-calibrated <c>MinScore</c>, recalling nothing.
    /// </remarks>
    public ScoreScale ScoreScale =>
        Inner is IScoreScaleAware aware ? aware.ScoreScale : ScoreScale.Similarity;

    /// <summary>
    /// Whether the decorated store can serve chunk lookups.
    /// </summary>
    /// <remarks>
    /// Implemented unconditionally, and delegated, for the same reason
    /// <see cref="ScoreScale"/> is rather than being split into a variant: a decorator has to
    /// implement the interface in order to forward it, and adding a variant per capability would
    /// need one class per combination — a sparse-and-lookup store, a lookup-only store, and so on.
    /// The probe keeps the claim honest instead. A caller that tested <c>is IChunkLookup</c> alone
    /// against this type would get <see langword="true"/> for every inner store, and GraphRAG's
    /// Sources section would come back empty with nothing saying why.
    /// </remarks>
    public bool SupportsChunkLookup => Inner is IChunkLookup { SupportsChunkLookup: true };

    /// <inheritdoc/>
    /// <remarks>
    /// Not retried, and not pipelined: this is a keyed read whose failure mode is a store that
    /// cannot do it at all, which no retry fixes. An unsupported inner store returns nothing, which
    /// <see cref="SupportsChunkLookup"/> is there to let callers detect <i>before</i> reading an
    /// empty result as an empty graph.
    /// </remarks>
    public Task<IReadOnlyList<TextChunk>> GetChunksAsync(
        IReadOnlyList<ChunkKey> keys, CancellationToken cancellationToken = default) =>
        Inner is IChunkLookup { SupportsChunkLookup: true } lookup
            ? lookup.GetChunksAsync(keys, cancellationToken)
            : Task.FromResult<IReadOnlyList<TextChunk>>([]);

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
