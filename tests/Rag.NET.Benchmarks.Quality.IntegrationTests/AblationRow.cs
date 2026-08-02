using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// One row of the ablation table: a retrieval strategy over an <b>already-populated</b> store.
/// <para>
/// The seam sits at retrieval and nowhere else, because that is the only place the four rows differ.
/// Indexing stays in <see cref="BeirHarness"/>, outside the row, so every row of a dataset queries
/// the same store over the same units and twelve cells cost three corpus embeddings — a row that
/// indexed would multiply the corpus embedding per cell and turn an hour of measurement into a day.
/// </para>
/// <para>
/// A row receives the per-run machinery as arguments — the query, the embedder, the vector cache,
/// the store and how deep to search — and returns a ranked list of <see cref="ChunkHit"/>. What it
/// does <b>not</b> receive as arguments is anything row-specific: a hybrid row's lexical index
/// (<see cref="HybridBm25AblationRow"/>), a HyDE row's hypothetical cache or a reranked row's
/// cross-encoder belong to the row instance that needs them, handed to its constructor, so adding
/// a row never widens this signature for the rows that came before it.
/// </para>
/// <para>
/// The row stops at chunk hits deliberately. Max-pooling chunks back to documents, the
/// self-retrieval exclusion and <see cref="BeirRunResult.PooledQueryCount"/> are properties of the
/// measurement, not of any one strategy, and live in <see cref="BeirHarness"/> where every row gets
/// them identically.
/// </para>
/// </summary>
public abstract class AblationRow
{
    /// <summary>
    /// The anchor row: embed the query, cosine top-k against the store. Exactly what
    /// <see cref="BeirHarness.MeasureAsync"/> always did, which is why this row's parity numbers are
    /// the ones validated against published figures — and why they must not move.
    /// </summary>
    public static AblationRow Dense { get; } = new DenseRow();

    /// <summary>Gets the label this row carries in the published table.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Retrieves for one query and returns the ranked chunk hits, deepest first by score.
    /// </summary>
    /// <param name="query">The query, id included — some datasets exclude its own document later.</param>
    /// <param name="generator">The embedder, shared with indexing.</param>
    /// <param name="embeddings">The vector cache; every embed call goes through it.</param>
    /// <param name="store">The populated store. The row never writes to it.</param>
    /// <param name="options">
    /// How deep to search. Computed by the harness from the cutoff and the units' shape, so pooling
    /// still sees enough distinct documents; a row must retrieve at least this deep.
    /// </param>
    /// <param name="cancellationToken">Cancels the retrieval.</param>
    /// <returns>The ranked hits, each carrying its parent document id.</returns>
    public abstract Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        BeirQuery query,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        InMemoryVectorStore store,
        SearchOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Projects search results onto <see cref="ChunkHit"/>, carrying the <b>parent document</b> id.
    /// </summary>
    /// <remarks>
    /// A chunk id reaching <see cref="IrMetrics"/> does not throw — it simply never matches a qrels
    /// entry and scores every query zero, so the mapping is made explicitly here rather than assumed.
    /// </remarks>
    private protected static IReadOnlyList<ChunkHit> ToChunkHits(IReadOnlyList<SearchResult> results)
    {
        var hits = new ChunkHit[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                results[i].Score);
        }

        return hits;
    }

    /// <summary>
    /// Dense retrieval, unchanged from when it was hard-coded: one query embedding through the
    /// cache, one cosine search, hits projected with their parent document ids.
    /// </summary>
    private sealed class DenseRow : AblationRow
    {
        public override string Name => "dense";

        public override async Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
            BeirQuery query,
            OnnxEmbeddingGenerator generator,
            EmbeddingCache embeddings,
            InMemoryVectorStore store,
            SearchOptions options,
            CancellationToken cancellationToken)
        {
            var vectors = await BeirHarness.EmbedAsync(
                generator, embeddings, [query.Text], cancellationToken);
            var results = await store.SearchAsync(vectors[0], options, cancellationToken);

            return ToChunkHits(results);
        }
    }
}
