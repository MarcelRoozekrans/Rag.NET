using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Phase 3.14's acceptance gate: Rag.NET's own row of the library comparison, run through the
/// <b>TREC run-file boundary</b> every entrant crosses — retrieve under the parity protocol, write
/// the rankings to disk with <see cref="TrecRunFile.Write"/>, read them back with
/// <see cref="TrecRunFile.Read"/>, and score the <b>read-back</b> rankings with
/// <see cref="IrMetrics"/>.
/// <para>
/// <b>The metric deliberately never sees the in-memory rankings.</b> A control that handed them
/// straight to <see cref="IrMetrics.Evaluate"/> would prove nothing about the path the comparators
/// use; scoring what came off the disk, on a row whose answer is already published, is the entire
/// reason this test exists. If this row does not reproduce the parity figures, the harness is
/// wrong and no comparator row can be trusted — that check comes before any comparison is read.
/// </para>
/// <para>
/// <b>The self-exclusion happens on the writer's side of the boundary</b>, inside
/// <see cref="DocumentRanking.TopDocuments"/> before the file exists, exactly where
/// <see cref="BeirHarness.MeasureAsync"/> applies it. It has to: the exclusion is part of each
/// dataset's retrieval protocol (BEIR's <c>DenseRetrievalExactSearch</c> drops the hit during
/// search; MTEB exposes it as <c>ignore_identical_ids</c>), so a published run file must already
/// contain the post-exclusion ranking or an outsider's <c>trec_eval</c> pass would score a
/// different ranking than ours — and a reader that excluded would need to know each query's own
/// document, which would stop the scorer being neutral.
/// </para>
/// <para>
/// Gated per dataset through <see cref="BeirRunBudget"/> under
/// <see cref="BeirProtocol.Comparison"/> — FiQA's leg costs its parity leg's 1 h 11 m cold, and
/// even the cheap legs would race the parity classes for a cold cache in the nightly — so every
/// case runs only under <c>RAGNET_BEIR_LONG_RUNS</c>.
/// </para>
/// </summary>
public sealed class BeirComparisonControlTests
{
    /// <summary>The last field of every line the control writes.</summary>
    private const string RunTag = "ragnet-control";

    private readonly ITestOutputHelper _output;

    public BeirComparisonControlTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Gets every described dataset, one case each — no separator axis, because the control runs
    /// the published protocol only: BEIR's SentenceBERT joins title and text with a single space,
    /// and the figures this row must reproduce were measured that way.
    /// </summary>
    /// <returns>Dataset names, so each case is legible and selectable by <c>DisplayName~</c>.</returns>
    public static TheoryData<string> Datasets()
    {
        var data = new TheoryData<string>();
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            data.Add(descriptor.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task NdcgAt10_ThroughTheTrecRunFileBoundary_ReproducesTheRepositoryFigures(
        string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Comparison, out var budgetReason),
            budgetReason);

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);
        // The cache counters bracket the whole run, and they move in the harness's untimed
        // prefetch: RetrieveScoredRunsAsync reads every needed vector into memory before either
        // span starts, so the sidecar's hits/misses describe what the disk held while the timed
        // spans never touch it. The indexing figure is index construction with embedding (and
        // its I/O) already paid for — deliberately not "the cost of indexing", because one disk
        // read per text inside the span measured the OS page cache, not the entrant. The spans
        // themselves live in the harness, around the same operations every entrant brackets:
        // index construction, and each retrieval call with pooling outside.
        var hitsBefore = embeddings.Hits;
        var missesBefore = embeddings.Misses;
        var measured = await BeirHarness.RetrieveScoredRunsAsync(
            descriptor, dataset, BeirHarness.OneChunkPerDocument(dataset.Documents),
            AblationRow.Dense, generator, embeddings, ct);

        var runFilePath = RunFilePath(cacheDirectory, datasetName);
        TrecRunFile.Write(runFilePath, measured.Runs, RunTag);
        TimingsSidecar.Write(
            runFilePath,
            new EntrantTimings(
                RunTag, measured.IndexingSeconds, measured.QueryLatenciesMilliseconds,
                checked((int)(embeddings.Hits - hitsBefore)),
                checked((int)(embeddings.Misses - missesBefore)),
                measured.UnitCount, measured.MaxUnitsPerDocument),
            BeirHarness.RunIndex);
        var readBack = TrecRunFile.Read(runFilePath);

        AssertRoundTripPreservedEveryRanking(measured.Runs, readBack);

        var evaluation = IrMetrics.Evaluate(readBack, dataset.Qrels, BeirHarness.Cutoff);
        _output.WriteLine(Describe(descriptor, runFilePath, evaluation));

        // The acceptance gate itself. The control ran the parity protocol, so its read-back number
        // is a parity measurement that happened to travel through a file — asserted against the
        // parity entries because those hold the published repository figures (0.64593 / 0.37086 /
        // 0.50432) this row must reproduce, FiQA's included the day somebody pays for its leg.
        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.Parity, evaluation.NormalizedDiscountedCumulativeGain, _output);

        // Second, the control row's own pin, so this case follows the same rule as every other
        // measured case: what it reported is recorded under its own protocol and a drift fails.
        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.Comparison, evaluation.NormalizedDiscountedCumulativeGain,
            _output);
    }

    /// <summary>
    /// Where the run file goes: under the BEIR cache, never under the repository, because a run
    /// file's document ids are corpus-derived data and nothing corpus-derived may be committed.
    /// </summary>
    private static string RunFilePath(string cacheDirectory, string datasetName)
    {
        var directory = Path.Combine(cacheDirectory, "runs");
        _ = Directory.CreateDirectory(directory);

        return Path.Combine(directory, datasetName + "-ragnet-control.trec");
    }

    /// <summary>
    /// Asserts the read-back rankings are the written rankings, query by query, position by
    /// position — including ties. <see cref="DocumentRanking"/> breaks pooled-score ties by
    /// ordinal document id and <see cref="TrecRunFile"/> pins that ties keep their emitted order,
    /// so this is where that guarantee is checked on real retrieval output rather than on a
    /// fixture: any reordering across the boundary would move the metric before the reproduction
    /// check could say why.
    /// </summary>
    private static void AssertRoundTripPreservedEveryRanking(
        IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> written,
        IReadOnlyDictionary<string, IReadOnlyList<string>> readBack)
    {
        Assert.Equal(written.Count, readBack.Count);
        foreach (var (queryId, documents) in written)
        {
            var documentIds = new string[documents.Count];
            for (var i = 0; i < documents.Count; i++)
            {
                documentIds[i] = documents[i].DocumentId;
            }

            Assert.Equal(documentIds, readBack[queryId]);
        }
    }

    /// <summary>The line the run prints, leading with the dataset like every other measurement.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, string runFilePath, IrEvaluation evaluation) =>
        FormattableString.Invariant($"""
            {descriptor.Name} COMPARISON CONTROL (parity corpus, scored from the read-back run file)
            nDCG@{evaluation.Cutoff} = {evaluation.NormalizedDiscountedCumulativeGain:F5}, Recall@{evaluation.Cutoff} = {evaluation.Recall:F5}, MRR@{evaluation.Cutoff} = {evaluation.MeanReciprocalRank:F5}
            {evaluation.EvaluatedQueryCount} queries evaluated, {evaluation.ExcludedQueryCount} excluded for lacking a positive judgement
            run file: {runFilePath}
            """);
}
