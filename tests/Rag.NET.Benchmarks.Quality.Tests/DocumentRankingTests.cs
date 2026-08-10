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
/// <b>Cut-then-pool fails 3 of these 18 tests</b> (3 of 13 before Phase 3.12 added the five
/// self-exclusion cases at the end, which are orthogonal to the ordering), and the three are
/// <see cref="TopDocuments_MaxPoolingBeforeTheCutKeepsDocumentsAChunkHeavyRivalWouldSqueezeOut"/>,
/// <see cref="TopDocuments_CuttingChunksFirstWouldReturnFewerDocumentsThanRequested"/> and
/// <see cref="TopDocumentIds_CuttingChunksFirstWouldScoreZeroOnAQueryPoolingFirstScoresPointFive"/>.
/// The count is stated because it was reported as four when this file was written, and the fourth —
/// <see cref="TopDocuments_EqualPooledScoresBreakOnDocumentIdSoTheRankingIsDeterministic"/> —
/// <b>passes</b> under cut-then-pool alone. It only reddens if the deterministic tie-break is
/// removed as well, which is a second, independent mutation. Three is what the ordering by itself
/// is worth here, and all three fail the same way: a document goes MISSING, none of them reorder.
/// </para>
/// <para>
/// The damage is uneven, which is what makes it dangerous. SciFact abstracts are mostly
/// single-chunk — the parity run indexes literally one chunk per document, which makes pooling a
/// no-op there and the two orderings byte-identical over all 1,109 queries — so SciFact's number
/// says nothing about this either way. FiQA and TREC-COVID have long documents where the
/// discrepancy is real. A table that is right in the cheap places and wrong in the expensive ones
/// is worse than no table.
/// </para>
/// <para>
/// <b>No longer the only guard, as of Phase 3.12.</b> <c>BeirRealChunkingTests</c> runs FiQA and
/// ArguAna through the library's own chunker — 121,236 units over FiQA's 57,638 documents since
/// Phase 3.16's packing chunker, 429,850 before it — and
/// asserts that the result differs from the same corpus indexed one chunk per document. This file is
/// still the only <i>offline</i> guard, and still the only one that pins the ordering rather than
/// merely observing that it changed something.
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

        Assert.Equal(["doc-long", "doc-b", "doc-c"], top.Select(static document => document.DocumentId), StringComparer.Ordinal);
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

        Assert.Equal(["doc-late", "doc-early"], top.Select(static document => document.DocumentId), StringComparer.Ordinal);
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

        Assert.Equal(["doc-long", "doc-b", "doc-c", "doc-d"], top.Select(static document => document.DocumentId), StringComparer.Ordinal);
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

        Assert.Equal(["doc-a", "doc-b", "doc-c"], first.Select(static document => document.DocumentId), StringComparer.Ordinal);
        Assert.Equal(
            first.Select(static document => document.DocumentId),
            second.Select(static document => document.DocumentId), StringComparer.Ordinal);
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
            DocumentRanking.TopDocumentIds(hits, k: 3), StringComparer.Ordinal);
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

    [Fact]
    public void TopDocuments_WithoutAnExclusion_KeepsEveryDocument()
    {
        // The default has to stay exactly what it was: SciFact passes null here and its 0.64593 is
        // this phase's regression gate.
        Assert.Equal(
            DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 4),
            DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 4, excludedDocumentId: null));
    }

    [Fact]
    public void TopDocuments_ExcludingADocumentPromotesTheOneBehindIt_RatherThanLeavingAHole()
    {
        // BEIR's `if corpus_id != query_id`. Dropping doc-long must not cost the ranking a slot:
        // three documents were asked for and three must come back, or nDCG@k is computed over k on
        // some queries and k-1 on others and the mean is over two different metrics.
        var ranked = DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 3, excludedDocumentId: "doc-long");

        Assert.Equal(["doc-b", "doc-c", "doc-d"], ranked);
    }

    [Fact]
    public void TopDocuments_ExclusionRemovesEveryChunkOfTheDocument_NotOnlyItsBestOne()
    {
        // doc-long contributes four chunks. Excluding on the pooled document rather than on the
        // individual hit is what makes this true, and a real corpus is where it would matter: a
        // query's own document chunked into six pieces would otherwise occupy five of the ten slots.
        var ranked = DocumentRanking.TopDocuments(ChunkHeavyRival(), k: 10, excludedDocumentId: "doc-long");

        Assert.DoesNotContain(ranked, static document => string.Equals(
            document.DocumentId, "doc-long", StringComparison.Ordinal));
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void TopDocuments_ExcludingAnAbsentDocument_ChangesNothing()
    {
        // FiQA's query ids and corpus ids are both bare integers and 621 of them collide by
        // coincidence, so most queries exclude an id that is nowhere in their results. That has to
        // be a no-op rather than an off-by-one.
        Assert.Equal(
            DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 4),
            DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 4, excludedDocumentId: "doc-absent"));
    }

    [Fact]
    public void TopDocuments_ExclusionIsOrdinal_NotCaseInsensitive()
    {
        // Ordinal everywhere else in this file — qrels ids are matched ordinally and
        // BeirDatasetDescriptor.ByName rejects "SciFact" for the same reason. A comparison that
        // folded case here would silently drop a different document on a corpus with mixed-case ids.
        var ranked = DocumentRanking.TopDocumentIds(ChunkHeavyRival(), k: 4, excludedDocumentId: "DOC-LONG");

        Assert.Contains("doc-long", ranked, StringComparer.Ordinal);
    }
}
