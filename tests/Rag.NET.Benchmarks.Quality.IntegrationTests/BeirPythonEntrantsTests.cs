using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Phase 3.14 Stage 2: the Python rows of the library comparison — LangChain, LlamaIndex and
/// Haystack, each at its own defaults — scored on this side of the <b>TREC run-file boundary</b>.
/// <para>
/// <b>The Python harness emits rankings and nothing else.</b> The pinned environment under
/// <c>benchmarks/library-comparison-python</c> (uv lockfile committed) chunks, indexes and
/// retrieves through each library's own code paths, max-pools chunk hits to documents
/// writer-side by <c>DocumentRanking.TopDocuments</c>' exact rule, and writes a run file this
/// project's <see cref="TrecRunFile.Read"/> accepts. Every figure is then computed here, by the
/// one <see cref="IrMetrics"/> behind the published BEIR numbers — no Python code computes a
/// metric, so no difference between rows can come from evaluation code.
/// </para>
/// <para>
/// <b>The embedder identity is proven by measurement, not construction.</b> The .NET entrants
/// hand the <c>OnnxEmbeddingGenerator</c> instance to their library; the Python entrants cannot,
/// so <c>identity_check.py</c> runs the same pinned ONNX export through onnxruntime and compares
/// a six-string battery — plain prose, punctuation, accented text, CJK, embedded whitespace and
/// a truncating text — against <c>OnnxEmbeddingGenerator</c>'s own vectors for the same bytes.
/// All six matched bitwise (384/384 floats, max |diff| = 0.0, measured 2026-08-02); the one
/// divergence the check ever found — HF's <c>strip_accents</c> default, fixed before any entrant
/// ran — is recorded in the Stage 2 section of
/// <c>docs/reference/library-comparison-defaults.md</c>. Without that check every Python row
/// would be measuring a different model.
/// </para>
/// <para>
/// <b>These cases score; they do not produce.</b> The run files cost minutes of Python-side
/// embedding each (the <see cref="BeirRunBudget"/> entries hold the measured costs), and this
/// project cannot rebuild them, so an opted-in case whose file is missing FAILS with the command
/// that produces it — the Hyde cell's refuse-on-miss rule — rather than skipping into a green
/// summary. Ungated runs skip through the budget table like every other expensive case.
/// </para>
/// </summary>
public sealed class BeirPythonEntrantsTests
{
    /// <summary>The message a run without the dataset cache skips with.</summary>
    private const string DatasetCacheSkipReason =
        "Set RAGNET_BEIR_CACHE to the directory holding the extracted BEIR datasets to score " +
        "the Python entrants' run files. No model is needed: these cases only read text and a " +
        "run file.";

    private readonly ITestOutputHelper _output;

    public BeirPythonEntrantsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Gets every described dataset, one case each, legible and selectable by name.</summary>
    /// <returns>Dataset names.</returns>
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
    public Task NdcgAt10_ThroughLangChainAtItsDefaults_ScoredFromThePythonRunFile(
        string datasetName) =>
        ScoreAsync(
            datasetName,
            BeirProtocol.LangChain,
            "langchain",
            "langchain-core-1.5.3",
            "RecursiveCharacterTextSplitter defaults (4000 chars, 200 overlap), " +
            "InMemoryVectorStore cosine");

    [Theory]
    [MemberData(nameof(Datasets))]
    public Task NdcgAt10_ThroughLlamaIndexAtItsDefaults_ScoredFromThePythonRunFile(
        string datasetName) =>
        ScoreAsync(
            datasetName,
            BeirProtocol.LlamaIndex,
            "llamaindex",
            "llama-index-core-0.14.23",
            "SentenceSplitter defaults (1024 cl100k tokens, 200 overlap), SimpleVectorStore " +
            "cosine");

    [Theory]
    [MemberData(nameof(Datasets))]
    public Task NdcgAt10_ThroughHaystackAtItsDefaults_ScoredFromThePythonRunFile(
        string datasetName) =>
        ScoreAsync(
            datasetName,
            BeirProtocol.Haystack,
            "haystack",
            "haystack-ai-3.0.0",
            "DocumentSplitter defaults (200 words, 0 overlap), InMemoryDocumentStore " +
            "dot_product (its default; vectors are unit-length)");

    /// <summary>
    /// The one scoring path all three Python rows share: find the entrant's run file, prove the
    /// writer-side protocol held on the file's own bytes, read it back, and score the read-back
    /// with the metric behind every published figure.
    /// </summary>
    private async Task ScoreAsync(
        string datasetName,
        BeirProtocol protocol,
        string entrantFileName,
        string expectedRunTag,
        string configuration)
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory), DatasetCacheSkipReason);
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, protocol, out var budgetReason), budgetReason);

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        var runFilePath = Path.Combine(
            cacheDirectory, "runs", datasetName + "-" + entrantFileName + ".trec");
        Assert.True(File.Exists(runFilePath), MissingRunFile(runFilePath, datasetName, entrantFileName));

        VerifyFileBytes(runFilePath, descriptor.ExcludesSelfRetrievedDocument, expectedRunTag);

        var readBack = TrecRunFile.Read(runFilePath);
        Assert.Equal(descriptor.TestQueryCount, readBack.Count);

        var evaluation = IrMetrics.Evaluate(readBack, dataset.Qrels, BeirHarness.Cutoff);
        _output.WriteLine(Describe(descriptor, runFilePath, expectedRunTag, configuration, evaluation));

        BeirReproduction.AssertReproduces(
            datasetName, protocol, evaluation.NormalizedDiscountedCumulativeGain, _output);
    }

    /// <summary>
    /// Checks the written file's own bytes: one run tag naming the pinned library version on
    /// every line, and — on a dataset whose protocol excludes the query's own document — zero
    /// lines pairing a query with the document sharing its id. On ArguAna, 1,298 of 1,406
    /// queries are byte-identical to their same-id corpus document, so zero self-lines is not
    /// achievable by accident; checked here rather than trusted from the Python side, because
    /// the writer-side exclusion is exactly the invariant an entrant could silently drop.
    /// </summary>
    private static void VerifyFileBytes(string runFilePath, bool excludesSelf, string expectedRunTag)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(runFilePath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Assert.True(
                fields.Length == 6 && string.Equals(fields[5], expectedRunTag, StringComparison.Ordinal),
                $"'{runFilePath}' line {lineNumber} does not carry run tag '{expectedRunTag}': " +
                $"'{line}'. The tag pins which library version produced the row; a different tag " +
                "is a different entrant's file about to be scored under this row's name.");

            if (excludesSelf)
            {
                Assert.True(
                    !string.Equals(fields[0], fields[2], StringComparison.Ordinal),
                    $"'{runFilePath}' line {lineNumber} ranks document '{fields[2]}' for query " +
                    $"'{fields[0]}' — the query's own document, which this dataset's protocol " +
                    "(ignore_identical_ids) excludes before the ranking is cut.");
            }
        }

        Assert.True(lineNumber > 0, $"'{runFilePath}' is empty.");
    }

    /// <summary>The failure a missing run file produces: what is absent and how to produce it.</summary>
    private static string MissingRunFile(string runFilePath, string datasetName, string entrantFileName) =>
        $"""
        '{runFilePath}' does not exist, so there is nothing to score. This case was opted in, and
        an opted-in measurement that quietly skipped would read as a pass — so it fails instead
        (the refuse-on-miss rule the Hyde cell uses). To produce the file, run the pinned Python
        harness (needs RAGNET_BEIR_CACHE, RAGNET_ONNX_EMBED_MODEL, RAGNET_ONNX_EMBED_VOCAB):
          cd benchmarks/library-comparison-python
          uv run python run_entrant.py {datasetName} {entrantFileName}
        """;

    /// <summary>The line the run prints, leading with the dataset like every other measurement.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor,
        string runFilePath,
        string runTag,
        string configuration,
        IrEvaluation evaluation) =>
        FormattableString.Invariant($"""
            {descriptor.Name} PYTHON ENTRANT {runTag} AT ITS DEFAULTS
            {configuration}, pinned all-MiniLM-L6-v2, scored from the read-back run file
            nDCG@{evaluation.Cutoff} = {evaluation.NormalizedDiscountedCumulativeGain:F5}, Recall@{evaluation.Cutoff} = {evaluation.Recall:F5}, MRR@{evaluation.Cutoff} = {evaluation.MeanReciprocalRank:F5}
            {evaluation.EvaluatedQueryCount} queries evaluated, {evaluation.ExcludedQueryCount} excluded for lacking a positive judgement
            run file: {runFilePath}
            """);
}
