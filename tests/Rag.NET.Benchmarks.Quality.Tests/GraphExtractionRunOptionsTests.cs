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
        Assert.Equal(GraphRagGenerationStage.Extraction, options.Stage);
        Assert.Equal(GraphExtractionRunOptions.Default, options);
    }

    [Theory]
    [InlineData("extraction", GraphRagGenerationStage.Extraction)]
    [InlineData("reports", GraphRagGenerationStage.Reports)]
    [InlineData("Reports", GraphRagGenerationStage.Reports)]
    [InlineData("EXTRACTION", GraphRagGenerationStage.Extraction)]
    public void TheStageFlag_NamesTheStage_InAnyCasing(string name, GraphRagGenerationStage expected)
    {
        var options = GraphExtractionRunOptions.Parse(["--stage", name]);

        Assert.NotNull(options);
        Assert.Equal(expected, options.Stage);
        Assert.Equal(GraphExtractionCorpus.Slice, options.Corpus);
    }

    [Fact]
    public void TheStageFlag_IsIndependentOfTheCorpusAndTheBound()
    {
        // The invocation that generates community reports over the whole corpus. All three flags
        // are orthogonal: the stage decides which prompts are sent, the corpus decides which
        // articles the graph is built from, and the bound is still a smoke-run bound over either.
        var options = GraphExtractionRunOptions.Parse(
            ["--stage", "reports", "--corpus", "full", "--max-documents", "20"]);

        Assert.Equal(
            new GraphExtractionRunOptions(
                GraphExtractionCorpus.Full, MaxDocuments: 20, GraphRagGenerationStage.Reports),
            options);
    }

    [Theory]
    // An unknown stage name, including the near miss and the numeric one Enum.TryParse would take.
    [InlineData("--stage", "report")]
    [InlineData("--stage", "communities")]
    [InlineData("--stage", "1")]
    [InlineData("--stage", "")]
    public void AnUnrecognisedStage_IsRefusedRatherThanDefaulted(string name, string value)
    {
        // Defaulting would run extraction — which is free once the cache is full, and would
        // therefore look like a successful report run that generated nothing.
        Assert.Null(GraphExtractionRunOptions.Parse([name, value]));
    }

    [Fact]
    public void PlanOnly_IsOffUnlessAskedFor_AndTakesNoValue()
    {
        // The only valueless flag, so the parser's cursor has to advance by one here and by two
        // everywhere else. A flag that swallowed the next argument would silently drop a --corpus.
        Assert.False(GraphExtractionRunOptions.Default.PlanOnly);
        Assert.False(GraphExtractionRunOptions.Parse([])!.PlanOnly);

        var alone = GraphExtractionRunOptions.Parse(["--plan-only"]);
        Assert.NotNull(alone);
        Assert.True(alone.PlanOnly);
        Assert.Equal(GraphExtractionCorpus.Slice, alone.Corpus);
        Assert.Equal(GraphRagGenerationStage.Extraction, alone.Stage);
    }

    [Fact]
    public void PlanOnly_ComposesWithTheOtherFlags_InAnyPosition()
    {
        // The invocation that answers "how many communities does the full corpus have" without
        // committing to generating a report for any of them — and it must mean the same thing
        // first, last and in the middle, since the valueless flag is what moves the cursor oddly.
        var expected = new GraphExtractionRunOptions(
            GraphExtractionCorpus.Full,
            int.MaxValue,
            GraphRagGenerationStage.Reports,
            PlanOnly: true);

        Assert.Equal(
            expected,
            GraphExtractionRunOptions.Parse(
                ["--plan-only", "--stage", "reports", "--corpus", "full"]));
        Assert.Equal(
            expected,
            GraphExtractionRunOptions.Parse(
                ["--stage", "reports", "--plan-only", "--corpus", "full"]));
        Assert.Equal(
            expected,
            GraphExtractionRunOptions.Parse(
                ["--stage", "reports", "--corpus", "full", "--plan-only"]));
    }

    [Fact]
    public void ReportConcurrency_IsAbsentUnlessAskedFor_SoTheLibraryDefaultApplies()
    {
        // #226: absent means "whatever GraphRagOptions.CommunityReportConcurrency defaults to",
        // not a copy of that default here — a copy would drift from the library the day the
        // library changed its mind, and the tool would quietly measure a bound nobody ships.
        Assert.Null(GraphExtractionRunOptions.Default.ReportConcurrency);
        Assert.Null(GraphExtractionRunOptions.Parse([])!.ReportConcurrency);
        Assert.Null(GraphExtractionRunOptions.Parse(["--stage", "reports"])!.ReportConcurrency);
    }

    [Fact]
    public void ReportConcurrency_OverridesTheBound_AndComposesWithTheOtherFlags()
    {
        // The invocation that measures the bound against the provider: the same graph at 1 and
        // at N, differing in nothing but how many calls are in flight.
        var options = GraphExtractionRunOptions.Parse(
            ["--stage", "reports", "--max-documents", "8", "--report-concurrency", "1"]);

        Assert.Equal(
            new GraphExtractionRunOptions(
                GraphExtractionCorpus.Slice,
                MaxDocuments: 8,
                GraphRagGenerationStage.Reports,
                ReportConcurrency: 1),
            options);
    }

    [Theory]
    // Not a positive count: zero and negative would be refused by the library too, but at
    // registration inside the tool — after the graph has been rebuilt — rather than before.
    [InlineData("--report-concurrency", "0")]
    [InlineData("--report-concurrency", "-1")]
    [InlineData("--report-concurrency", "1.5")]
    [InlineData("--report-concurrency", "four")]
    [InlineData("--report-concurrency", "")]
    public void AnUnusableReportConcurrency_IsRefusedRatherThanDefaulted(string name, string value)
    {
        Assert.Null(GraphExtractionRunOptions.Parse([name, value]));
    }

    [Fact]
    public void ARepeatedReportConcurrencyFlag_IsRefused()
    {
        Assert.Null(GraphExtractionRunOptions.Parse(
            ["--report-concurrency", "1", "--report-concurrency", "4"]));
        Assert.Null(GraphExtractionRunOptions.Parse(["--report-concurrency"]));
    }

    [Fact]
    public void ARepeatedPlanOnlyFlag_IsRefused_LikeEveryOtherRepeat()
    {
        Assert.Null(GraphExtractionRunOptions.Parse(["--plan-only", "--plan-only"]));
    }

    [Fact]
    public void ARepeatedStageFlag_IsRefused()
    {
        Assert.Null(
            GraphExtractionRunOptions.Parse(["--stage", "reports", "--stage", "extraction"]));
        Assert.Null(GraphExtractionRunOptions.Parse(["--stage"]));
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
    public void TheUsageLine_NamesBothStagesBothCorporaAndTheBound()
    {
        // Printed on every rejection above, so it is the only guidance a mistyped invocation gets.
        Assert.Contains(GraphExtractionRunOptions.CorpusOption, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.SliceName, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.FullName, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.MaxDocumentsOption, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.StageOption, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.ExtractionStageName, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
        Assert.Contains(GraphExtractionRunOptions.ReportsStageName, GraphExtractionRunOptions.Usage, StringComparison.Ordinal);
    }
}
