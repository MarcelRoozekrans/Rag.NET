using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability for an <see cref="IVectorStore"/> that can return chunks by identity rather
/// than by similarity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a search-oriented store needs this at all.</b> GraphRAG's local search puts the source
/// chunks that produced its selected entities in front of the model, chosen by graph provenance —
/// <c>GraphEntity.SourceChunkIds</c> — and never by score. There is no query vector that reliably
/// returns exactly those chunks, and a metadata filter cannot express "any of these twenty
/// <c>(document, index)</c> pairs" on most backends. So the lookup is by key or it does not happen.
/// </para>
/// <para>
/// <b>Probed on the registered instance, like every other capability here.</b> A store that does
/// not implement this leaves local search's Sources section empty — half its token budget unspent —
/// which is why <see cref="SupportsChunkLookup"/> exists rather than an <c>is IChunkLookup</c>
/// test alone: a decorator has to implement the interface to be able to forward it, and would
/// otherwise claim a capability its inner store does not have. <c>ResilientVectorStore</c> does
/// exactly that, delegating this property, the same way it delegates
/// <see cref="IScoreScaleAware.ScoreScale"/>.
/// </para>
/// </remarks>
public interface IChunkLookup
{
    /// <summary>Whether this instance can actually serve lookups.</summary>
    /// <remarks>
    /// <see langword="true"/> for a store implementing the capability itself — the default. A
    /// decorator implements the interface unconditionally so that it can forward, and overrides
    /// this to report what its inner store can do.
    /// </remarks>
    bool SupportsChunkLookup => true;

    /// <summary>
    /// Returns the chunks for the given keys. Keys with no stored chunk are absent from the result
    /// rather than an error, and the order of the result is not the order of the keys.
    /// </summary>
    /// <remarks>
    /// A missing key is ordinary: a document deleted since extraction leaves the graph naming
    /// chunks that no longer exist, and that is not a reason to fail a query. Callers that need the
    /// keys' order — local search does, since its source ordering is graph-derived — should
    /// re-associate by <see cref="ChunkKey"/> themselves.
    /// </remarks>
    /// <param name="keys">Chunk identities to fetch.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The chunks that exist, in unspecified order.</returns>
    Task<IReadOnlyList<TextChunk>> GetChunksAsync(
        IReadOnlyList<ChunkKey> keys,
        CancellationToken cancellationToken = default);
}
