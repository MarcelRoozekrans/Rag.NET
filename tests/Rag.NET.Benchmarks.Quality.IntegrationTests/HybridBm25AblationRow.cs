using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The +BM25 hybrid row: the dense ranking and an <see cref="InMemoryBm25Index"/> ranking over the
/// same units, fused by Reciprocal Rank Fusion. Composed manually because
/// <c>IHybridSearchable</c> is implemented only by the Azure AI Search and Weaviate stores — the
/// in-memory store the harness measures against has no hybrid path of its own.
/// <para>
/// <b>Incomparable to any published BM25 figure, and labelled so in <see cref="Name"/>.</b>
/// Anserini — the source of BEIR's published BM25 numbers — applies Porter stemming and an English
/// stopword list at <c>k1=0.9, b=0.4</c>; <see cref="InMemoryBm25Index"/> lowercases and splits at
/// <c>k1=1.5, b=0.75</c>. Those are not two settings of one retriever, so this row is an internal
/// comparison against the dense row above it and nothing else. Do not "improve" the tokenizer for
/// comparability: 3.7 §2 rejected a benchmark-only analyzer and that decision stands.
/// </para>
/// <para>
/// <b>The lexical index must cover exactly the units the harness indexed</b>, or the two legs rank
/// different corpora and the fusion compares apples to a subset of oranges. The seam cannot
/// enforce that — the row never sees indexing — so <see cref="Over"/> builds the index from a unit
/// list and the calling test passes the <i>same list variable</i> to it and to
/// <c>BeirHarness.MeasureAsync</c>, making the guarantee visible at the call site.
/// </para>
/// <para>
/// <b>The row records whether BM25 contributed, and <see cref="AssertBm25Contributed"/> turns
/// silence into a failure.</b> If the lexical index returned nothing — wrong initialisation, an
/// empty index, a tokenizer that drops everything — RRF over the one remaining list preserves its
/// order exactly, and this row would display the dense row's number under a hybrid label: a
/// passing test showing a meaningless cell. The counters are per-instance and accumulate, so a
/// row instance belongs to one measurement.
/// </para>
/// </summary>
public sealed class HybridBm25AblationRow : AblationRow, IDisposable
{
    /// <summary>
    /// RRF's rank constant: each list contributes <c>0.5 / (60 + rank)</c> at 1-based ranks.
    /// </summary>
    /// <remarks>
    /// 60 is the value the RRF paper (Cormack, Clarke &amp; Büttcher, SIGIR 2009) fixed for all
    /// its experiments, and the same default <c>RrfMerger</c> uses on the library's ensemble
    /// path — quoted rather than referenced because that type is internal to Rag.NET.
    /// </remarks>
    private const int RankConstant = 60;

    /// <summary>
    /// Each leg's weight, matching <c>EnsembleOptions.DenseWeight</c> and <c>Bm25Weight</c>, both
    /// 0.5 by default.
    /// </summary>
    /// <remarks>
    /// <b>This was 1.0, and the difference was invisible.</b> Weighting both legs equally is a
    /// uniform factor on an RRF sum, so it moves every fused score and no rank — nDCG cannot see it
    /// and the pinned figures did not move when this changed. It is not invisible everywhere:
    /// anything reading the fused score directly, <c>MinScore</c> most obviously, saw numbers twice
    /// the size the library would have produced. Carrying the library's own default costs nothing
    /// and lets <see cref="HybridFusionParityTests"/> assert score equality outright rather than
    /// equality-up-to-a-factor, which is a claim a reader has to check rather than read.
    /// </remarks>
    private const double DefaultLegWeight = 0.5;

    private readonly InMemoryBm25Index _lexicalIndex;

    private HybridBm25AblationRow(InMemoryBm25Index lexicalIndex)
    {
        _lexicalIndex = lexicalIndex;
    }

    /// <summary>Gets how many queries this row has retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <summary>Gets how many of those queries BM25 returned at least one hit for.</summary>
    public int Bm25ProductiveQueryCount { get; private set; }

    /// <summary>
    /// Gets how many of those queries ended with a fused ranking that differs from the dense
    /// ranking — the only direct evidence that the fusion changed anything at all.
    /// </summary>
    public int DivergedQueryCount { get; private set; }

    /// <inheritdoc/>
    public override string Name =>
        "+bm25 hybrid via RRF (incomparable to published BM25: no stemming or stopwords, k1=1.5 b=0.75)";

    /// <summary>
    /// Builds the row with its lexical index built over <paramref name="units"/> — pass the
    /// <b>same list</b> handed to <c>BeirHarness.MeasureAsync</c>, so both legs rank one corpus.
    /// </summary>
    /// <param name="units">The text units, exactly as the harness will index them.</param>
    /// <returns>The row; the caller disposes it.</returns>
    public static HybridBm25AblationRow Over(IReadOnlyList<TextChunk> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        var index = new InMemoryBm25Index();
        try
        {
            for (var i = 0; i < units.Count; i++)
            {
                index.Add(i, units[i]);
            }

            return new HybridBm25AblationRow(index);
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

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
        var dense = ToChunkHits(await store.SearchAsync(vectors[0], options, cancellationToken));

        // The same depth as the dense leg. RRF returns the union, so the fused list can run to
        // 2×TopK — deeper than the harness asked for, which its contract allows ("at least").
        var lexical = ToChunkHits(_lexicalIndex.Search(query.Text, options.TopK));
        var fused = FuseByReciprocalRank(dense, lexical);

        QueryCount++;
        if (lexical.Count > 0)
        {
            Bm25ProductiveQueryCount++;
        }

        if (!SameRanking(dense, fused))
        {
            DivergedQueryCount++;
        }

        return fused;
    }

    /// <summary>
    /// Asserts BM25 actually reached this row's rankings, before its number is read as a
    /// measurement of hybrid retrieval.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// Two checks, in diagnostic order: whether the lexical index produced anything, then whether
    /// what it produced moved anything. The first failing explains the second, so it goes first —
    /// the same shape as <c>BeirRealChunkingTests.AssertTheProtocolActuallyChunkedAndAggregated</c>.
    /// </remarks>
    public void AssertBm25Contributed(string datasetName)
    {
        Assert.True(QueryCount > 0, FormattableString.Invariant($"""
            THE ROW RETRIEVED NOTHING. {datasetName}: {nameof(AssertBm25Contributed)} was called on a
            row that has answered no queries, so there is no evidence to judge. Call it after the
            measurement, on the same row instance the measurement used.
            """));

        Assert.True(
            Bm25ProductiveQueryCount * 2 >= QueryCount,
            FormattableString.Invariant($"""
                BM25 RETURNED NOTHING FOR MOST QUERIES. {datasetName}: InMemoryBm25Index produced a
                non-empty result for {Bm25ProductiveQueryCount} of {QueryCount} queries. When the
                lexical list is empty, RRF over the one remaining list preserves its order exactly, so
                this row silently displays the dense row's ranking under a hybrid label — a number
                that means nothing. Check that the index was built over the same units the harness
                indexed ({nameof(HybridBm25AblationRow)}.{nameof(Over)} with the very list handed to
                MeasureAsync) and that the tokenizer is not dropping every term of these queries.
                """));

        Assert.True(DivergedQueryCount > 0, FormattableString.Invariant($"""
            THE FUSION CHANGED NOTHING. {datasetName}: BM25 produced a non-empty result for
            {Bm25ProductiveQueryCount} of {QueryCount} queries, yet no query's fused ranking differs
            from its dense ranking — so this row's number IS the dense row's number and the hybrid
            label on it is a lie. Check that the lexical hits actually enter the fusion instead of
            being discarded, and that both lists are fused by reciprocal rank rather than the dense
            list being returned as-is.
            """));
    }

    /// <inheritdoc/>
    public void Dispose() => _lexicalIndex.Dispose();

    /// <summary>
    /// Fuses the two rankings: each hit contributes <c>1 / (RankConstant + rank)</c> per list it
    /// appears in, 1-based, and the union is returned by descending fused score.
    /// </summary>
    /// <remarks>
    /// Ties are real, not theoretical — rank <c>n</c> in one list only scores exactly rank
    /// <c>n</c> in the other — so the order is made deterministic: dense rank first (the anchor
    /// row's evidence outranks the incomparable retriever's), then chunk id, ordinal.
    /// </remarks>
    /// <remarks>
    /// <b>Internal rather than private so parity can reach it.</b> It is a pure function of two
    /// rankings, and <see cref="HybridFusionParityTests"/> holds it against the library's own
    /// <c>EnsembleBehavior</c> fusion. Driving this row instead would drag an ONNX embedder into a
    /// test about arithmetic.
    /// </remarks>
    internal static IReadOnlyList<ChunkHit> FuseByReciprocalRank(
        IReadOnlyList<ChunkHit> dense, IReadOnlyList<ChunkHit> lexical)
    {
        var byChunk = new Dictionary<string, Candidate>(
            dense.Count + lexical.Count, StringComparer.Ordinal);

        for (var rank = 0; rank < dense.Count; rank++)
        {
            byChunk[dense[rank].ChunkId] = new Candidate(dense[rank].DocumentId, rank + 1)
            {
                Score = DefaultLegWeight / (RankConstant + rank + 1),
            };
        }

        for (var rank = 0; rank < lexical.Count; rank++)
        {
            var contribution = DefaultLegWeight / (RankConstant + rank + 1);
            if (byChunk.TryGetValue(lexical[rank].ChunkId, out var seen))
            {
                seen.Score += contribution;
                continue;
            }

            byChunk[lexical[rank].ChunkId] = new Candidate(lexical[rank].DocumentId, int.MaxValue)
            {
                Score = contribution,
            };
        }

        var fused = new List<(string ChunkId, Candidate Candidate)>(byChunk.Count);
        foreach (var pair in byChunk)
        {
            fused.Add((pair.Key, pair.Value));
        }

        fused.Sort(static (a, b) =>
        {
            var byScore = b.Candidate.Score.CompareTo(a.Candidate.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byDenseRank = a.Candidate.DenseRank.CompareTo(b.Candidate.DenseRank);
            return byDenseRank != 0 ? byDenseRank : string.CompareOrdinal(a.ChunkId, b.ChunkId);
        });

        var hits = new ChunkHit[fused.Count];
        for (var i = 0; i < fused.Count; i++)
        {
            hits[i] = new ChunkHit(fused[i].ChunkId, fused[i].Candidate.DocumentId, fused[i].Candidate.Score);
        }

        return hits;
    }

    /// <summary>Projects BM25 hits onto <see cref="ChunkHit"/>, the same id scheme as the base's.</summary>
    internal static IReadOnlyList<ChunkHit> ToChunkHits(
        IReadOnlyList<(TextChunk chunk, double score)> results)
    {
        var hits = new ChunkHit[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var (chunk, score) = results[i];
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                score);
        }

        return hits;
    }

    /// <summary>Reports whether the fused list carries exactly the dense list's chunks in order.</summary>
    private static bool SameRanking(IReadOnlyList<ChunkHit> dense, IReadOnlyList<ChunkHit> fused)
    {
        if (dense.Count != fused.Count)
        {
            return false;
        }

        for (var i = 0; i < dense.Count; i++)
        {
            if (!string.Equals(dense[i].ChunkId, fused[i].ChunkId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One chunk's accumulating fused score, plus the tie-breaking dense rank.</summary>
    private sealed class Candidate
    {
        public Candidate(string documentId, int denseRank)
        {
            DocumentId = documentId;
            DenseRank = denseRank;
        }

        public string DocumentId { get; }

        /// <summary>1-based rank in the dense list, or <see cref="int.MaxValue"/> when absent.</summary>
        public int DenseRank { get; }

        public double Score { get; set; }
    }
}
