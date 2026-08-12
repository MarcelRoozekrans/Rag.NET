using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What has to be true of <see cref="MultiHopRagSlice"/> before
/// <see cref="GraphRagFunctionsTests"/> can blame GraphRAG for anything.
/// <para>
/// <b>Self-containment is the assertion that matters.</b> The guard asserts that local search over
/// the graph retrieves one of a query's known-relevant documents. If half of a query's evidence sat
/// outside the sixty articles the graph was built from, that assertion would fail on a pipeline
/// that was working perfectly — the evidence simply would not be there to retrieve — and the
/// failure would read as "GraphRAG is broken". This test makes that impossible: a query is in the
/// slice only if <b>all</b> of its judged documents are.
/// </para>
/// <para>
/// <b>The re-derivation is the other half.</b> The pinned lists are literals precisely so the slice
/// cannot drift with upstream, but a literal nobody re-checks is a literal that can be wrong from
/// the day it is typed. Walking the qrels here and comparing against the pin costs milliseconds and
/// turns "these sixty were derived correctly once" into a statement something re-asserts.
/// </para>
/// <para>
/// Needs only <c>RAGNET_BEIR_CACHE</c> — no model, no budget. It reads text and finishes in
/// seconds, so it runs on any machine that has the dataset rather than joining the opt-in tier.
/// </para>
/// </summary>
public sealed class MultiHopRagSliceTests
{
    private readonly ITestOutputHelper _output;

    public MultiHopRagSliceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryPinnedQuerysEvidence_LiesInsideThePinnedDocumentSet()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the MultiHop-RAG slice.");

        var dataset = await LoadAsync(cacheDirectory);
        var outside = EvidenceOutsideTheSlice(dataset);

        _output.WriteLine(FormattableString.Invariant(
            $"{MultiHopRagSlice.QueryIds.Count} queries, {MultiHopRagSlice.DocumentIds.Count} articles, {outside.Count} evidence rows outside the slice"));

        Assert.True(
            outside.Count == 0,
            FormattableString.Invariant($"""
                THE SLICE IS NOT SELF-CONTAINED. {outside.Count} judged (query, document) pairs name a
                document outside the pinned sixty:
                {string.Join(Environment.NewLine, outside)}
                A query whose evidence is half outside the slice cannot be retrieved correctly from a
                graph built over the slice, so GraphRagFunctionsTests' ground-truth assertion would go
                red for a reason that is not GraphRAG's. Either the walk that produced the pin changed,
                or the pinned document list was edited without re-running it.
                """));
    }

    [Fact]
    public async Task WalkingTheQrelsInIdOrder_ReproducesThePinnedSlice()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the MultiHop-RAG slice.");

        var dataset = await LoadAsync(cacheDirectory);

        // The walk lives in the harness project rather than here, because the extraction
        // generation tool is a console application that cannot see this project's literals and
        // must fill the cache for exactly the articles the guard will ask for. Two copies of the
        // walk would eventually disagree, and the symptom would be a refuse-on-miss failure on the
        // guard's first chunk that reads like an empty cache.
        var (queries, documents) = MultiHopRagSliceWalk.Derive(dataset);

        // The query sequence is compared in order — it comes off an explicitly sorted list, so its
        // order is the walk's and is reproducible. The documents are compared as a SET: within one
        // query, qrels order reaches here through a Dictionary's enumeration, which is an
        // implementation detail rather than a promise. Asserting a sequence there would pin a
        // detail of the BCL instead of a property of the slice.
        Assert.Equal(MultiHopRagSlice.QueryIds, queries);
        Assert.Equal(MultiHopRagSlice.DocumentIds.Count, documents.Count);
        Assert.True(
            new HashSet<string>(documents, StringComparer.Ordinal).SetEquals(MultiHopRagSlice.DocumentIdSet),
            "Re-deriving the walk produced a different set of articles from the pinned one. The " +
            "pin is what stops the slice moving with upstream, so a disagreement means either the " +
            "dataset changed under the pinned revision or the literal list was edited by hand.");
    }

    [Fact]
    public async Task EveryPinnedQueryAndDocument_ExistsInTheConvertedDataset()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the MultiHop-RAG slice.");

        var dataset = await LoadAsync(cacheDirectory);

        // Both projections throw, naming what is missing, rather than returning a short list — so
        // the assertion here is that they do not, and the counts are the readable form of that.
        Assert.Equal(MultiHopRagSlice.DocumentIds.Count, MultiHopRagSlice.Documents(dataset.Documents).Count);
        Assert.Equal(MultiHopRagSlice.QueryIds.Count, MultiHopRagSlice.Queries(dataset.Queries).Count);

        foreach (var queryId in MultiHopRagSlice.QueryIds)
        {
            Assert.True(
                dataset.Qrels.ContainsKey(queryId),
                $"Query '{queryId}' is pinned into the slice but qrels/test.tsv judges it not at " +
                "all, so the guard would have no ground truth to assert against for it.");
        }
    }

    /// <summary>Loads the converted dataset the slice is cut from.</summary>
    private static Task<BeirDataset> LoadAsync(string cacheDirectory) =>
        BeirHarness.LoadAsync(
            BeirDatasetDescriptor.ByName(MultiHopRagSlice.DatasetName),
            cacheDirectory,
            BeirLoader.DefaultTitleTextSeparator,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Lists every judged pair of a pinned query that names an unpinned document — empty when the
    /// slice is self-contained.
    /// </summary>
    private static IReadOnlyList<string> EvidenceOutsideTheSlice(BeirDataset dataset)
    {
        var outside = new List<string>();
        foreach (var queryId in MultiHopRagSlice.QueryIds)
        {
            if (!dataset.Qrels.TryGetValue(queryId, out var judgements))
            {
                continue;
            }

            foreach (var (documentId, grade) in judgements)
            {
                if (grade > 0 && !MultiHopRagSlice.DocumentIdSet.Contains(documentId))
                {
                    outside.Add(FormattableString.Invariant($"  {queryId} -> {documentId} (grade {grade})"));
                }
            }
        }

        return outside;
    }

}
