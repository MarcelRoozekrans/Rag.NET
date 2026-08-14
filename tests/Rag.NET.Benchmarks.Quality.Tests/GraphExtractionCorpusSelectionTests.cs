using System.Globalization;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// What each corpus mode selects, against synthetic datasets small enough to check by hand.
/// <para>
/// <b>The slice's count guard is the assertion that matters here.</b> The slice exists so the
/// GraphRAG guard replays a fixed sixty articles forever; a walk that landed on any other number
/// and was extracted anyway would fill the cache under keys nothing ever asks for — real money
/// spent, and a guard that then fails refuse-on-miss on its first chunk as though the cache were
/// empty. Widening the tool to the full corpus must not weaken that, so the guard is asserted here
/// on a dataset built to fail it, where no dataset download is involved.
/// </para>
/// <para>
/// The real corpus's numbers — sixty pinned ids, 609 articles — are asserted in
/// <c>Rag.NET.Benchmarks.Quality.IntegrationTests</c>, which has the dataset. These are the
/// properties that hold for any dataset at all.
/// </para>
/// </summary>
public sealed class GraphExtractionCorpusSelectionTests
{
    /// <summary>How many articles the synthetic corpus holds: more than the slice takes.</summary>
    private const int CorpusSize = MultiHopRagSliceWalk.TargetDocumentCount + 10;

    [Fact]
    public void FullMode_TakesEveryArticleInTheCorpus_InCorpusOrder()
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: MultiHopRagSliceWalk.TargetDocumentCount);

        var selection = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Full, int.MaxValue);

        Assert.Equal(GraphExtractionCorpus.Full, selection.Corpus);
        Assert.Equal(CorpusSize, selection.Documents.Count);
        Assert.Equal(CorpusSize, selection.CorpusDocumentCount);
        Assert.Equal(dataset.Documents, selection.Documents);
    }

    [Fact]
    public void SliceMode_TakesOnlyTheWalkedArticles_AndPutsThemBackInCorpusOrder()
    {
        // The walk reaches the articles in whatever order the qrels enumerate them; what comes back
        // is the corpus's own order, because entity descriptions merge by concatenation and the
        // ingestion order therefore decides what the merged graph says.
        var dataset = Dataset(CorpusSize, judgedDocumentCount: MultiHopRagSliceWalk.TargetDocumentCount);

        var selection = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Slice, int.MaxValue);

        Assert.Equal(GraphExtractionCorpus.Slice, selection.Corpus);
        Assert.Equal(MultiHopRagSliceWalk.TargetDocumentCount, selection.Documents.Count);
        Assert.Equal(CorpusSize, selection.CorpusDocumentCount);
        Assert.Equal(
            dataset.Documents.Take(MultiHopRagSliceWalk.TargetDocumentCount),
            selection.Documents);
    }

    [Theory]
    [InlineData(GraphExtractionCorpus.Slice)]
    [InlineData(GraphExtractionCorpus.Full)]
    public void MaxDocuments_BoundsEitherCorpus_FromTheFront(GraphExtractionCorpus corpus)
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: MultiHopRagSliceWalk.TargetDocumentCount);

        var selection = GraphExtractionCorpusSelection.Select(dataset, corpus, maxDocuments: 4);

        Assert.Equal(4, selection.Documents.Count);
        Assert.Equal(dataset.Documents.Take(4), selection.Documents);

        // The bound truncates what was selected; it never changes what the mode names, so a plan
        // can still say "4 of 60" rather than claiming the corpus is four articles long.
        Assert.Equal(
            corpus == GraphExtractionCorpus.Slice ? MultiHopRagSliceWalk.TargetDocumentCount : CorpusSize,
            selection.AvailableDocumentCount);
    }

    [Fact]
    public void SliceMode_RefusesAWalkThatDidNotLandOnSixty_RatherThanExtractingIt()
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: 12);

        var failure = Assert.Throws<InvalidDataException>(
            () => GraphExtractionCorpusSelection.Select(
                dataset, GraphExtractionCorpus.Slice, int.MaxValue));

        Assert.Contains("12 articles", failure.Message, StringComparison.Ordinal);
        Assert.Contains("cost real money", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullMode_HasNoSuchGuard_BecauseItTakesWhateverTheCorpusHolds()
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: 12);

        var selection = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Full, int.MaxValue);

        Assert.Equal(CorpusSize, selection.Documents.Count);
    }

    [Fact]
    public void TheDescription_SaysWhichCorpusAndHowMuchOfItWasLeftOut()
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: MultiHopRagSliceWalk.TargetDocumentCount);

        var slice = GraphExtractionCorpusSelection
            .Select(dataset, GraphExtractionCorpus.Slice, maxDocuments: 4)
            .Describe();
        var full = GraphExtractionCorpusSelection
            .Select(dataset, GraphExtractionCorpus.Full, int.MaxValue)
            .Describe();

        Assert.Contains("slice", slice, StringComparison.Ordinal);
        Assert.Contains("4 of the 60", slice, StringComparison.Ordinal);
        Assert.Contains("full", full, StringComparison.Ordinal);
        Assert.Contains(
            FormattableString.Invariant($"{CorpusSize} of the {CorpusSize}"), full, StringComparison.Ordinal);
    }

    [Fact]
    public void AModeNothingRecognises_Throws_RatherThanFallingThroughToACorpus()
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: MultiHopRagSliceWalk.TargetDocumentCount);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphExtractionCorpusSelection.Select(dataset, (GraphExtractionCorpus)7, int.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveBound_Throws(int maxDocuments)
    {
        var dataset = Dataset(CorpusSize, judgedDocumentCount: MultiHopRagSliceWalk.TargetDocumentCount);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphExtractionCorpusSelection.Select(
                dataset, GraphExtractionCorpus.Full, maxDocuments));
    }

    /// <summary>
    /// A corpus of <paramref name="documentCount"/> articles, with one judged query citing the
    /// first <paramref name="judgedDocumentCount"/> of them — in reverse corpus order, so that
    /// "the walk reached them" and "the corpus lists them" are visibly different orders.
    /// </summary>
    private static BeirDataset Dataset(int documentCount, int judgedDocumentCount)
    {
        var documents = new List<BeirDocument>(documentCount);
        for (var i = 0; i < documentCount; i++)
        {
            var id = i.ToString("D3", CultureInfo.InvariantCulture);
            documents.Add(new BeirDocument("doc-" + id, "title " + id, "text " + id, "title " + id + " text " + id));
        }

        var judgements = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = judgedDocumentCount - 1; i >= 0; i--)
        {
            judgements[documents[i].Id] = 1;
        }

        return new BeirDataset(
            MultiHopRagSource.DatasetName,
            "test",
            documents,
            [],
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
            {
                ["mhr-0000"] = judgements,
            });
    }
}
