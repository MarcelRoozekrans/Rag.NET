using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The Semantic Kernel entrant's protocol arithmetic — retrieval depth and the writer-side cut —
/// exercised offline, no model, no corpus, in the fast tier on every push.
/// <para>
/// <b>Why the cut is the entrant's own and not <see cref="DocumentRanking"/>'s.</b> The control
/// row pools chunk hits, so its ranking is Rag.NET's aggregation and <see cref="DocumentRanking"/>
/// re-sorts with its own ordinal-id tie-break. The Semantic Kernel row indexes one record per
/// document — nothing to pool — and <b>the library's returned order is the entrant's ranking</b>:
/// re-sorting it through <see cref="DocumentRanking"/> would overwrite Semantic Kernel's own
/// tie-breaking with ours, which is precisely the kind of harness fingerprint the comparison
/// exists to keep out of a comparator's row. So the entrant only drops the protocol-excluded
/// document and cuts to the metric's cutoff, order untouched — and these tests pin that it
/// touches nothing else.
/// </para>
/// </summary>
public sealed class SemanticKernelEntrantTests
{
    [Fact]
    public void RetrievalDepth_IsTheCutoff_WhenTheDatasetKeepsSelfRetrievedDocuments()
    {
        // Retrieval depth is the Task 2 protocol parameter (the metric's cutoff, 10), not a
        // Semantic Kernel default — SK's SearchAsync has no default top at all.
        Assert.Equal(BeirHarness.Cutoff, SemanticKernelEntrant.RetrievalDepth(
            excludesSelfRetrievedDocument: false));
    }

    [Fact]
    public void RetrievalDepth_OverShootsByOne_WhenTheDatasetExcludesTheSelfDocument()
    {
        // One extra, exactly as BeirHarness over-shoots for the control: the worst case is the
        // query's own document in the top 10, and the ranking must still hold 10 after it is
        // dropped.
        Assert.Equal(BeirHarness.Cutoff + 1, SemanticKernelEntrant.RetrievalDepth(
            excludesSelfRetrievedDocument: true));
    }

    [Fact]
    public void TopDocuments_PreservesTheSearchOrder_IncludingTiesItWouldLoseToAnIdSort()
    {
        // "b" before "a" at an exact tie: DocumentRanking would re-order this pair by ordinal id,
        // so this fixture fails if the entrant ever starts routing through our aggregation.
        var hits = Hits(("d9", 0.9), ("b", 0.5), ("a", 0.5), ("z", 0.1));

        var top = SemanticKernelEntrant.TopDocuments(hits, cutoff: 3, excludedDocumentId: null);

        Assert.Equal(["d9", "b", "a"], Ids(top));
        Assert.Equal(0.5, top[1].Score);
    }

    [Fact]
    public void TopDocuments_DropsTheExcludedDocument_AndStillFillsTheCutoff()
    {
        // The over-shot eleventh hit exists exactly so the ranking still holds `cutoff` documents
        // after the query's own document is dropped.
        var hits = Hits(("q1", 1.0), ("d1", 0.9), ("d2", 0.8), ("d3", 0.7));

        var top = SemanticKernelEntrant.TopDocuments(hits, cutoff: 3, excludedDocumentId: "q1");

        Assert.Equal(["d1", "d2", "d3"], Ids(top));
    }

    [Fact]
    public void TopDocuments_ExcludesNothing_WhenNoDocumentIsExcluded()
    {
        var hits = Hits(("d1", 0.9), ("d2", 0.8));

        var top = SemanticKernelEntrant.TopDocuments(hits, cutoff: 3, excludedDocumentId: null);

        Assert.Equal(["d1", "d2"], Ids(top));
    }

    [Fact]
    public void TopDocuments_ReturnsEverything_WhenFewerHitsThanTheCutoffSurvive()
    {
        // A short ranking stays short rather than being padded: TrecRunFile carries it as-is and
        // IrMetrics scores the absent ranks as retrieving nothing.
        var hits = Hits(("q1", 1.0), ("d1", 0.9));

        var top = SemanticKernelEntrant.TopDocuments(hits, cutoff: 10, excludedDocumentId: "q1");

        Assert.Equal(["d1"], Ids(top));
    }

    private static IReadOnlyList<ScoredDocument> Hits(params (string Id, double Score)[] hits)
    {
        var documents = new ScoredDocument[hits.Length];
        for (var i = 0; i < hits.Length; i++)
        {
            documents[i] = new ScoredDocument(hits[i].Id, hits[i].Score);
        }

        return documents;
    }

    private static string[] Ids(IReadOnlyList<ScoredDocument> documents)
    {
        var ids = new string[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            ids[i] = documents[i].DocumentId;
        }

        return ids;
    }
}
