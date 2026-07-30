using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="DocumentRanking"/>'s order of operations: map chunk to parent document, max-pool
/// to one score per document, dedupe, <b>then</b> cut to top <c>k</c>.
/// <para>
/// The fixture <see cref="TopDocuments_MaxPoolingBeforeTheCutKeepsDocumentsAChunkHeavyRivalWouldSqueezeOut"/>
/// builds is the point of this file. A fixture where every document contributes one chunk passes
/// under both orderings and is therefore watching nothing — which is the exact failure this task
/// exists to prevent. Here one document contributes four chunks that occupy the top ranks, so
/// cutting first throws away documents that pooling first would keep, and the two answers differ in
/// length as well as in content.
/// </para>
/// <para>
/// The damage is uneven, which is what makes it dangerous. SciFact abstracts are mostly
/// single-chunk, so SciFact's number looks plausible either way; FiQA and TREC-COVID have long
/// documents where the discrepancy is real. A table that is right in the cheap places and wrong in
/// the expensive ones is worse than no table.
/// </para>
/// </summary>
public sealed class DocumentRankingTests
{
    private const int Places = 5;

    /// <summary>
    /// Four chunks of <c>doc-long</c>, one each of the rest. Ranked by raw chunk score the top three
    /// are <c>doc-long</c> 0.95, <c>doc-long</c> 0.92, <c>doc-b</c> 0.90 — so cutting to three
    /// chunks first and pooling afterwards yields two documents and loses <c>doc-c</c> entirely.
    /// Pooling first yields <c>doc-long</c> 0.95, <c>doc-b</c> 0.90, <c>doc-c</c> 0.70,
    /// <c>doc-d</c> 0.65, and the cut then keeps three.
    /// </summary>
    private static IReadOnlyList<ChunkHit> ChunkHeavyRival() =>
    [
        new ChunkHit("long#1", "doc-long", 0.95),
        new ChunkHit("long#2", "doc-long", 0.92),
        new ChunkHit("long#3", "doc-long", 0.88),
        new ChunkHit("long#4", "doc-long", 0.61),
        new ChunkHit("b#1", "doc-b", 0.90),
        new ChunkHit("c#1", "doc-c", 0.70),
        new ChunkHit("d#1", "doc-d", 0.65),
    ];

    [Fact]
    public void TopDocuments_MaxPoolingBeforeTheCutKeepsDocumentsAChunkHeavyRivalWouldSqueezeOut()
    {
        var top = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 3);

        Assert.Equal(["doc-long", "doc-b", "doc-c"], top.Select(static document => document.DocumentId));
    }

    [Fact]
    public void TopDocuments_CuttingChunksFirstWouldReturnFewerDocumentsThanRequested()
    {
        // States the disagreement as a count, so the fixture cannot quietly stop disagreeing. Cutting
        // the seven chunks to three and pooling afterwards leaves two documents; pooling first leaves
        // four, of which three survive the cut. A run that returns two documents for k = 3 out of a
        // corpus holding four distinct documents got the order of operations wrong.
        var top = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 3);

        Assert.Equal(3, top.Count);
    }

    [Fact]
    public void TopDocumentIds_CuttingChunksFirstWouldScoreZeroOnAQueryPoolingFirstScoresPointFive()
    {
        // The same fixture carried through to the number the phase reports, because a list-shaped
        // assertion does not convey how much this moves. doc-c is the relevant document: pooling
        // first puts it at rank 3, worth 1/log2(4) = 0.5. Cutting chunks first drops it, worth 0.
        // Half of nDCG@3 on this query, against a parity band of +/-0.02.
        var relevance = new Dictionary<string, int>(StringComparer.Ordinal) { ["doc-c"] = 1 };

        var ranked = DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 3);

        Assert.Equal(0.5, IrMetrics.NormalizedDiscountedCumulativeGain(ranked, relevance, k: 3), Places);
    }

    [Fact]
    public void TopDocuments_PooledScoreIsTheMaximumNotTheSumOrTheMean()
    {
        // doc-long's four chunks sum to 3.36 and average to 0.84. Either would reorder this ranking:
        // the sum would put any sufficiently chunked document first regardless of how well it
        // matches, and the mean would push doc-long below doc-b (0.90) for containing one strong
        // passage among weaker ones.
        var top = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 4);

        Assert.Equal(0.95, top[0].Score, Places);
        Assert.Equal("doc-long", top[0].DocumentId, StringComparer.Ordinal);
    }

    [Fact]
    public void TopDocuments_PoolsAcrossEveryChunkNotJustTheFirstOneSeen()
    {
        // Catches a "first chunk wins" or "last chunk wins" implementation, which max-pooling looks
        // like whenever the chunks happen to arrive in score order. doc-late's best chunk is last and
        // must still decide its rank.
        IReadOnlyList<ChunkHit> hits =
        [
            new ChunkHit("late#1", "doc-late", 0.10),
            new ChunkHit("early#1", "doc-early", 0.98),
            new ChunkHit("late#2", "doc-late", 0.99),
        ];

        var top = DocumentRanking.TopDocuments(hits, k: 2);

        Assert.Equal(["doc-late", "doc-early"], top.Select(static document => document.DocumentId));
        Assert.Equal(0.99, top[0].Score, Places);
    }

    [Fact]
    public void TopDocuments_DeduplicatesSoEveryDocumentAppearsExactlyOnce()
    {
        // Without the dedupe, nDCG@10 would count the same relevant document's gain several times
        // over while IDCG counted it once — a score that can exceed 1 and reads as a leak.
        var top = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 10);

        Assert.Equal(4, top.Count);
        Assert.Equal(4, top.Select(static document => document.DocumentId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TopDocuments_OrdersByPooledScoreDescending()
    {
        var top = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 10);

        Assert.Equal(["doc-long", "doc-b", "doc-c", "doc-d"], top.Select(static document => document.DocumentId));
    }

    [Fact]
    public void TopDocuments_EqualPooledScoresBreakOnDocumentIdSoTheRankingIsDeterministic()
    {
        // In-memory storage is pinned for determinism, and that is worth nothing if equal scores come
        // back in dictionary order. Ordinal on the document id is arbitrary but repeatable, which is
        // the whole requirement.
        IReadOnlyList<ChunkHit> hits =
        [
            new ChunkHit("c#1", "doc-c", 0.5),
            new ChunkHit("a#1", "doc-a", 0.5),
            new ChunkHit("b#1", "doc-b", 0.5),
        ];

        var first = DocumentRanking.TopDocuments(hits, k: 3);
        var second = DocumentRanking.TopDocuments(hits, k: 3);

        Assert.Equal(["doc-a", "doc-b", "doc-c"], first.Select(static document => document.DocumentId));
        Assert.Equal(
            first.Select(static document => document.DocumentId),
            second.Select(static document => document.DocumentId));
    }

    [Fact]
    public void TopDocuments_FewerDistinctDocumentsThanK_ReturnsThemAll()
    {
        var top = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 100);

        Assert.Equal(4, top.Count);
    }

    [Fact]
    public void TopDocuments_NoChunkHits_IsEmptyRatherThanThrowing()
    {
        // A query that retrieved nothing is a zero, not an exception: IrMetrics.Evaluate scores it
        // and keeps it in the divisor.
        Assert.Empty(DocumentRanking.TopDocuments([], k: 10));
    }

    [Fact]
    public void TopDocumentIds_ReturnsTheSameOrderAsTopDocuments()
    {
        var hits = ChunkHeavyRival();

        Assert.Equal(
            DocumentRanking.TopDocuments(hits, k: 3).Select(static document => document.DocumentId),
            DocumentRanking.TopDocumentIds(hits, k: 3));
    }

    [Fact]
    public void TopDocuments_RejectsANonPositiveK()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: -1));
    }

    [Fact]
    public void TopDocuments_RejectsANullChunkList()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentRanking.TopDocuments(null!, k: 10));
    }
}
