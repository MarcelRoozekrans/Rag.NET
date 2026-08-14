using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Graph;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What the extraction generation tool's two corpora actually select out of the real MultiHop-RAG
/// dataset, and what a full-corpus run would cost given what is already cached.
/// <para>
/// <b>The slice is load bearing and this is where that is checked against the corpus itself.</b>
/// <see cref="MultiHopRagSlice"/> pins sixty article ids as a literal,
/// <see cref="GraphRagFunctionsTests"/> asserts a graph built from exactly those, and
/// <see cref="BeirReproduction"/> quotes figures derived from it. Widening the tool to 609 articles
/// is only safe if the sixty are still reachable, still the same sixty, and still in the same
/// order — so all three are asserted here rather than argued.
/// </para>
/// <para>
/// <b>And it answers the money question before anyone spends it.</b> The full corpus contains the
/// slice; the slice's articles chunk identically inside it, because chunking is a function of one
/// document's own text; the extraction cache is keyed on the rendered prompt. Those three
/// sentences add up to "a full run re-uses every extraction the slice paid for", and the last case
/// below counts it rather than reasoning about it.
/// </para>
/// <para>
/// Needs only <c>RAGNET_BEIR_CACHE</c> — no model, no budget, and <b>no LLM calls at all</b>: the
/// probe reads the extraction cache and never generates.
/// </para>
/// </summary>
public sealed class GraphExtractionCorpusTests
{
    private readonly ITestOutputHelper _output;

    public GraphExtractionCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SliceMode_SelectsExactlyThePinnedSixtyArticles_InTheOrderTheGuardReadsThem()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the extraction corpora.");

        var dataset = await LoadAsync(cacheDirectory);
        var selection = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Slice, int.MaxValue);

        // The guard's own projection, compared as a SEQUENCE rather than a set: the two must agree
        // on order as well as membership, because entity descriptions merge by concatenation and
        // the ingestion order therefore decides what the cached graph says.
        Assert.Equal(
            Ids(MultiHopRagSlice.Documents(dataset.Documents)),
            Ids(selection.Documents),
            StringComparer.Ordinal);
        Assert.Equal(MultiHopRagSlice.DocumentIds.Count, selection.Documents.Count);
        Assert.Equal(MultiHopRagSliceWalk.TargetDocumentCount, selection.AvailableDocumentCount);
        Assert.Equal(MultiHopRagSlice.QueryIds.Count, selection.JudgedQueryCount);
    }

    [Fact]
    public async Task FullMode_SelectsTheWholeConvertedCorpus_WithTheSliceInsideIt()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the extraction corpora.");

        var dataset = await LoadAsync(cacheDirectory);
        var selection = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Full, int.MaxValue);

        Assert.Equal(MultiHopRagSource.PublishedCounts.DocumentCount, selection.Documents.Count);
        Assert.Equal(dataset.Documents, selection.Documents);

        var selected = new HashSet<string>(Ids(selection.Documents), StringComparer.Ordinal);
        Assert.True(
            selected.IsSupersetOf(MultiHopRagSlice.DocumentIdSet),
            "The full corpus does not contain every pinned slice article, so a full run would not " +
            "be a superset of the run the guard replays.");
    }

    [Theory]
    [InlineData(GraphExtractionCorpus.Slice)]
    [InlineData(GraphExtractionCorpus.Full)]
    public async Task MaxDocuments_BoundsEitherCorpus_SoASmokeRunIsPossibleOnBoth(
        GraphExtractionCorpus corpus)
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the extraction corpora.");

        var dataset = await LoadAsync(cacheDirectory);
        var bounded = GraphExtractionCorpusSelection.Select(dataset, corpus, maxDocuments: 3);
        var whole = GraphExtractionCorpusSelection.Select(dataset, corpus, int.MaxValue);

        Assert.Equal(3, bounded.Documents.Count);
        Assert.Equal(whole.Documents.Take(3), bounded.Documents);
    }

    [Fact]
    public async Task TheSliceArticles_ChunkIdenticallyInsideTheFullCorpus()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the extraction corpora.");

        var dataset = await LoadAsync(cacheDirectory);
        var slice = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Slice, int.MaxValue);
        var full = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Full, int.MaxValue);

        var fromFull = full.Documents
            .Where(document => MultiHopRagSlice.DocumentIdSet.Contains(document.Id))
            .ToList();

        // Value equality over the records first: same id, title, text and — the one that decides
        // the chunks — the same RetrievalText. Neither mode touches the loader's separator.
        Assert.Equal(slice.Documents, fromFull);

        // Then the chunks themselves, for every slice article, because the cache is keyed on a
        // prompt that carries the chunk text verbatim. If these ever differed, a full run would
        // re-pay for all 60 articles and the guard's cache would be half of two experiments.
        var chunks = 0;
        for (var i = 0; i < slice.Documents.Count; i++)
        {
            var fromSlice = await ChunkAsync(slice.Documents[i]);
            var alsoFromFull = await ChunkAsync(fromFull[i]);

            Assert.Equal(Texts(fromSlice), Texts(alsoFromFull), StringComparer.Ordinal);
            chunks += fromSlice.Count;
        }

        _output.WriteLine(FormattableString.Invariant(
            $"{slice.Documents.Count} slice articles chunk to {chunks} chunks in both modes."));
    }

    [Fact]
    public async Task EveryRequestTheSliceWouldMake_IsAlreadyInTheExtractionCache()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the extraction corpora.");

        // Refuse-on-miss, so nothing here can write to the cache even by accident; the probe reads
        // through TryGet, which neither generates nor moves the cache's own tallies.
        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.RefuseOnMiss);

        Assert.SkipUnless(
            Directory.Exists(cache.EntryDirectory),
            $"No extraction cache in {cache.EntryDirectory}; run the generation tool to fill it.");

        var dataset = await LoadAsync(cacheDirectory);
        var slice = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Slice, int.MaxValue);
        var probe = await ProbeAsync(cache, slice.Documents);

        _output.WriteLine(FormattableString.Invariant(
            $"{slice.Documents.Count} articles: {probe.Cached} requests cached, {probe.Uncached} would be generated."));

        Assert.Equal(0, probe.Uncached);
        Assert.True(
            probe.Cached > 0,
            "The probe counted no cached requests at all, which means it walked no chunks — the " +
            "count below it would then be a plan for nothing.");
    }

    /// <summary>The documents' ids, in order — written as a loop because these run inside one.</summary>
    private static List<string> Ids(IReadOnlyList<BeirDocument> documents)
    {
        var ids = new List<string>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            ids.Add(documents[i].Id);
        }

        return ids;
    }

    /// <summary>The chunks' texts, in order.</summary>
    private static List<string> Texts(IReadOnlyList<TextChunk> chunks)
    {
        var texts = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            texts.Add(chunks[i].Text);
        }

        return texts;
    }

    /// <summary>Loads the converted dataset both corpora are cut from.</summary>
    private static Task<BeirDataset> LoadAsync(string cacheDirectory) =>
        BeirHarness.LoadAsync(
            BeirDatasetDescriptor.ByName(MultiHopRagSlice.DatasetName),
            cacheDirectory,
            BeirLoader.DefaultTitleTextSeparator,
            TestContext.Current.CancellationToken);

    /// <summary>Chunks one article exactly as both the tool and the guard do.</summary>
    private static Task<IReadOnlyList<TextChunk>> ChunkAsync(BeirDocument document) =>
        GraphRagSliceIngestion.ChunkAsync(document, TestContext.Current.CancellationToken);

    /// <summary>
    /// Replays the extraction for a set of articles against the cache alone, counting which
    /// requests are already stored — the same accounting the tool prints before it spends.
    /// </summary>
    private static async Task<GraphExtractionPlanProbe> ProbeAsync(
        GraphExtractionCache cache, IReadOnlyList<BeirDocument> documents)
    {
        var options = GraphRagSliceIngestion.CreateOptions();
        var probe = new GraphExtractionPlanProbe(cache);
        using var embedder = new StubEmbeddingGenerator();

        for (var i = 0; i < documents.Count; i++)
        {
            await using var graphStore = new SqliteGraphStore(":memory:");
            _ = await GraphRagSliceIngestion.ExtractAsync(
                documents[i],
                await ChunkAsync(documents[i]),
                probe,
                embedder,
                graphStore,
                options,
                TestContext.Current.CancellationToken);
        }

        return probe;
    }
}
