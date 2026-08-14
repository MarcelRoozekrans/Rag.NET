using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// What the extraction generation tool's command line is allowed to mean.
/// <para>
/// <b>Every case below is a case about money.</b> The tool's two corpora differ by 549 articles,
/// roughly 37,000 LLM requests and several hours, so a parser that guessed would guess in units of
/// dollars: <c>--corpus ful</c> quietly read as the slice would replay for free and look like a
/// successful full run, and <c>--corpus 1</c> read as "the second enum member" would spend the
/// budget on a corpus nobody named. Both are refused, and refusal is asserted rather than assumed.
/// </para>
/// <para>
/// It is also the reason the parser lives in this project rather than in the tool. The tool is a
/// console application whose <c>Program</c> no fast-tier test can see; a parser that could only be
/// exercised by running the executable would be exercised by running it against the real corpus.
/// </para>
/// </summary>
public sealed class GraphExtractionRunOptionsTests
{
    [Fact]
    public void NoArguments_MeanTheWholeSlice_WhichIsWhatTheToolAlwaysDid()
    {
        var options = GraphExtractionRunOptions.Parse([]);

        Assert.NotNull(options);
        Assert.Equal(GraphExtractionCorpus.Slice, options.Corpus);
        Assert.Equal(int.MaxValue, options.MaxDocuments);
        Assert.Equal(GraphExtractionRunOptions.Default, options);
    }

    [Theory]
    [InlineData("slice", GraphExtractionCorpus.Slice)]
    [InlineData("full", GraphExtractionCorpus.Full)]
    [InlineData("Full", GraphExtractionCorpus.Full)]
    [InlineData("SLICE", GraphExtractionCorpus.Slice)]
    public void TheCorpusFlag_NamesTheCorpus_InAnyCasing(string name, GraphExtractionCorpus expected)
    {
        var options = GraphExtractionRunOptions.Parse(["--corpus", name]);

        Assert.NotNull(options);
        Assert.Equal(expected, options.Corpus);
        Assert.Equal(int.MaxValue, options.MaxDocuments);
    }

    [Fact]
    public void MaxDocuments_StillWorksOnItsOwn_AndStillMeansTheSlice()
    {
        var options = GraphExtractionRunOptions.Parse(["--max-documents", "3"]);

        Assert.NotNull(options);
        Assert.Equal(GraphExtractionCorpus.Slice, options.Corpus);
        Assert.Equal(3, options.MaxDocuments);
    }

    [Fact]
    public void MaxDocuments_BoundsTheFullCorpusToo_WhichIsWhatMakesASmokeRunPossible()
    {
        // The pair that #205 asks for: enough of the full corpus to watch the plumbing work,
        // without committing to 609 articles.
        var options = GraphExtractionRunOptions.Parse(["--corpus", "full", "--max-documents", "20"]);

        Assert.NotNull(options);
        Assert.Equal(GraphExtractionCorpus.Full, options.Corpus);
        Assert.Equal(20, options.MaxDocuments);
    }

    [Fact]
    public void TheFlags_MayArriveInEitherOrder()
    {
        var options = GraphExtractionRunOptions.Parse(["--max-documents", "20", "--corpus", "full"]);

        Assert.Equal(
            new GraphExtractionRunOptions(GraphExtractionCorpus.Full, MaxDocuments: 20), options);
    }

    /// <summary>
    /// Everything that must be refused outright. A <see langword="null"/> here is the tool printing
    /// usage and exiting 2 — the only outcome that cannot spend anything.
    /// </summary>
    [Theory]
    // An unknown corpus name, including the near miss and the numeric one Enum.TryParse would take.
    [InlineData("--corpus", "everything")]
    [InlineData("--corpus", "ful")]
    [InlineData("--corpus", "1")]
    [InlineData("--corpus", "")]
    // An unknown flag, including the near miss of a known one.
    [InlineData("--corpora", "full")]
    [InlineData("--max-docs", "3")]
    [InlineData("full", "slice")]
    // A bound that is not a positive count.
    [InlineData("--max-documents", "0")]
    [InlineData("--max-documents", "-1")]
    [InlineData("--max-documents", "1.5")]
    [InlineData("--max-documents", "1,000")]
    [InlineData("--max-documents", "all")]
    public void AnUnrecognisedCommandLine_IsRefusedRatherThanDefaulted(string name, string value)
    {
        Assert.Null(GraphExtractionRunOptions.Parse([name, value]));
    }

    [Fact]
    public void AFlagWithoutAValue_IsRefused()
    {
        Assert.Null(GraphExtractionRunOptions.Parse(["--corpus"]));
        Assert.Null(GraphExtractionRunOptions.Parse(["--corpus", "full", "--max-documents"]));
    }

    [Fact]
    public void ARepeatedFlag_IsRefusedRatherThanResolvedByPosition()
    {
        // Last-one-wins and first-one-wins disagree about which corpus this run covers, and the
        // disagreement is worth six hours. Neither is chosen.
        Assert.Null(GraphExtractionRunOptions.Parse(["--corpus", "full", "--corpus", "slice"]));
        Assert.Null(
            GraphExtractionRunOptions.Parse(["--max-documents", "2", "--max-documents", "3"]));
    }

    [Fact]
    public void TheUsageLine_NamesBothCorporaAndTheBound()
    {
        // Printed on every rejection above, so it is the only guidance a mistyped invocation gets.
        Assert.Contains(GraphExtractionRunOptions.CorpusOption, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.SliceName, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.FullName, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.MaxDocumentsOption, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
    }
}
