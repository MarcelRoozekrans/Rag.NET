using Rag.NET.Abstractions;

namespace Rag.NET.GraphRag;

/// <summary>
/// The vector store the graph's own chunks live in — entities, relationships and community
/// reports — kept apart from the store holding your documents.
/// </summary>
/// <remarks>
/// <para>
/// <b>#247, and the reason it is a separate store rather than a filter.</b> Until now GraphRAG
/// embedded its synthetic units into the <i>same</i> store as the article chunks: on MultiHop-RAG,
/// 303,503 of them beside 17,648 article chunks. Dense retrieval treated them as peers of the text,
/// and a six-chunk window filled with entity descriptions instead of article content. Measured, with
/// depth and chunking held constant: <b>−0.043 nDCG@10</b> and <b>−0.21 answer accuracy</b>.
/// </para>
/// <para>
/// Filtering them out of results at query time recovers all of that, and was the first fix written.
/// It was rejected before merging: it needs an over-fetch on every query — 20× <c>TopK</c> on a
/// store where synthetic units outnumber article chunks 17:1 — with a multiplier that is a heuristic
/// and can still under-fill. Separating the stores has no over-fetch, no multiplier, and nothing to
/// undo at query time, because the two kinds never compete in the first place. It is also what
/// Microsoft's reference design does.
/// </para>
/// <para>
/// <b>A wrapper rather than a second <c>IVectorStore</c> registration.</b> One
/// <see cref="IVectorStore"/> is already registered and resolved by everything in the pipeline;
/// registering a second would make "which one?" ambiguous at every injection site. This type names
/// the distinction instead, so the graph's store is asked for by name and nothing else can receive
/// it by accident.
/// </para>
/// </remarks>
/// <param name="store">Where the graph's chunks are written and searched.</param>
public sealed class GraphChunkStore(IVectorStore store)
{
    /// <summary>Gets the underlying store.</summary>
    public IVectorStore Store { get; } = store
        ?? throw new ArgumentNullException(nameof(store));
}
