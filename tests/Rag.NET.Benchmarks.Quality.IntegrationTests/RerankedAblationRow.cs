using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The +reranker row: the dense row's top-k rescored by a cross-encoder and re-sorted by its
/// scores. The candidates come pre-projection from <c>store.SearchAsync</c>, whose
/// <see cref="SearchResult"/>s carry <c>Chunk.Text</c> — exactly the
/// <c>(query, results)</c> shape <see cref="IReranker.RerankAsync"/> takes — so the row scores,
/// reorders, and only then projects to <see cref="ChunkHit"/>.
/// <para>
/// <b>The hits carry the cross-encoder's scores, not the cosine ones, and that is load-bearing.</b>
/// <c>DocumentRanking</c> max-pools chunk hits to documents by hit score; hits reordered by the
/// cross-encoder but still carrying their cosine scores would be sorted straight back into the
/// dense ranking downstream, and the row would measure nothing while looking like it did.
/// </para>
/// <para>
/// <b>Two truncation lengths meet in this row, and neither is a bug.</b> The cross-encoder scores
/// query–passage pairs truncated at <c>OnnxRerankerOptions.MaxLength</c>'s default of 512 tokens
/// (ms-marco-MiniLM-L6-v2's own limit); the dense leg's embedder truncated each unit at
/// <c>OnnxEmbeddingGenerator</c>'s 256 (all-MiniLM-L6-v2's <c>max_seq_length</c>, the published
/// configuration). Different models doing different jobs, stated in <see cref="Name"/> so the
/// table's reader does not have to infer it.
/// </para>
/// <para>
/// <b>The row records whether the reranker reordered anything, and
/// <see cref="AssertRerankerReordered"/> turns silence into a failure.</b> A cross-encoder that
/// returns its input order — misconfigured, wrong output tensor, scores all equal — produces this
/// row as the dense row under a reranker label: a passing test displaying a meaningless cell. The
/// counters are per-instance and accumulate, so a row instance belongs to one measurement.
/// </para>
/// </summary>
public sealed class RerankedAblationRow : AblationRow
{
    private readonly IReranker _reranker;

    public RerankedAblationRow(IReranker reranker)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        _reranker = reranker;
    }

    /// <summary>Gets how many queries this row has retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <summary>
    /// Gets how many of those queries ended with a reranked order that differs from the dense
    /// order — the only direct evidence that the cross-encoder's scores did anything at all.
    /// </summary>
    public int ReorderedQueryCount { get; private set; }

    /// <inheritdoc/>
    public override string Name =>
        "+reranker (cross-encoder/ms-marco-MiniLM-L6-v2 over dense top-k; " +
        "pairs truncated at 512 tokens, dense embeddings at 256)";

    /// <inheritdoc/>
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
        var dense = await store.SearchAsync(vectors[0], options, cancellationToken);

        // Scored in the dense order and returned in the dense order — OnnxReranker preserves its
        // input order, which is what makes position i below "dense rank i" for the tie-break.
        var scored = await _reranker.RerankAsync(query.Text, dense, cancellationToken);
        var order = RankByScore(scored);

        QueryCount++;
        if (!IsIdentity(order))
        {
            ReorderedQueryCount++;
        }

        return ToChunkHits(scored, order);
    }

    /// <summary>
    /// Asserts the cross-encoder actually moved rankings, before this row's number is read as a
    /// measurement of reranked retrieval.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// Two checks, in diagnostic order, the shape of
    /// <see cref="HybridBm25AblationRow.AssertBm25Contributed"/>: whether anything moved at all,
    /// then whether enough moved to be a scoring model rather than an accident. The first failing
    /// explains the second, so it goes first.
    /// </remarks>
    public void AssertRerankerReordered(string datasetName)
    {
        Assert.True(QueryCount > 0, FormattableString.Invariant($"""
            THE ROW RETRIEVED NOTHING. {datasetName}: {nameof(AssertRerankerReordered)} was called on
            a row that has answered no queries, so there is no evidence to judge. Call it after the
            measurement, on the same row instance the measurement used.
            """));

        Assert.True(ReorderedQueryCount > 0, FormattableString.Invariant($"""
            THE RERANKER CHANGED NOTHING. {datasetName}: the reranked order differs from the dense
            order on {ReorderedQueryCount} of {QueryCount} queries — the cross-encoder returned its
            input order every single time, which is what all-equal scores, a wrong output tensor or
            a model that never loaded look like. This row's number IS the dense row's number and the
            reranker label on it is a lie. Check that RerankAsync's scores actually vary across a
            query's candidates and that the row sorts by them.
            """));

        Assert.True(
            ReorderedQueryCount * 2 >= QueryCount,
            FormattableString.Invariant($"""
                THE RERANKER BARELY MOVED ANYTHING. {datasetName}: the reranked order differs from
                the dense order on only {ReorderedQueryCount} of {QueryCount} queries. Two rankings
                of ten-plus candidates produced by different models agree exactly almost never, so a
                cross-encoder that leaves most queries in their dense order is scoring with
                near-constant values — broken in a way the zero check cannot see. Its number would
                still be, for most queries, the dense row's number under a reranker label.
                """));
    }

    /// <summary>
    /// Ranks positions by descending cross-encoder score; ties keep the dense order.
    /// </summary>
    /// <remarks>
    /// The tie-break is not decoration: <see cref="Array.Sort{T}(T[], Comparison{T})"/> is
    /// unstable, and an unstable sort over all-equal scores could scramble the list — which
    /// <see cref="ReorderedQueryCount"/> would then count as the reranker having reordered, hiding
    /// exactly the degradation <see cref="AssertRerankerReordered"/> exists to catch.
    /// </remarks>
    private static int[] RankByScore(IReadOnlyList<RerankResult> scored)
    {
        var order = new int[scored.Count];
        var scores = new double[scored.Count];
        for (var i = 0; i < scored.Count; i++)
        {
            order[i] = i;
            scores[i] = scored[i].RelevanceScore;
        }

        Array.Sort(order, (a, b) =>
        {
            var byScore = scores[b].CompareTo(scores[a]);
            return byScore != 0 ? byScore : a.CompareTo(b);
        });

        return order;
    }

    /// <summary>Reports whether the permutation leaves every position where it was.</summary>
    private static bool IsIdentity(IReadOnlyList<int> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Projects the reordered results onto <see cref="ChunkHit"/> — the base's id scheme, but the
    /// cross-encoder's score, for the pooling reason in the class remarks.
    /// </summary>
    private static IReadOnlyList<ChunkHit> ToChunkHits(
        IReadOnlyList<RerankResult> scored, IReadOnlyList<int> order)
    {
        var hits = new ChunkHit[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            var result = scored[order[i]];
            var chunk = result.SearchResult.Chunk;
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                result.RelevanceScore);
        }

        return hits;
    }
}
