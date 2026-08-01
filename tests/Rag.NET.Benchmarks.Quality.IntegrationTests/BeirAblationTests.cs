using Rag.NET.Benchmarks.Quality;
using Rag.NET.Reranking.Onnx;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The ablation table's non-dense rows, measured on the <b>parity</b> protocol — one chunk per
/// document, truncated at 256 — because the table's anchor is the dense row and only the parity
/// protocol produces a dense number validated against a published figure. The dense row itself is
/// not re-measured here: it is <see cref="BeirParityTests"/>, pinned by <see cref="BeirReproduction"/>.
/// <para>
/// <b>Every row asserts its mechanism did something before its number is read.</b> A row whose
/// machinery silently did nothing produces the dense ranking under another label — a passing test
/// displaying a meaningless cell, the failure shape this milestone keeps finding. For the hybrid
/// row that guard is <see cref="HybridBm25AblationRow.AssertBm25Contributed"/>, whose evidence the
/// row collects per query while the harness aggregates identically for every row.
/// </para>
/// <para>
/// <b>Opt-in until its cells are budgeted.</b> <see cref="BeirRunBudget"/> keys costs on
/// dataset × protocol and throws on unmeasured pairs; every ablation cell is such a pair until
/// Task 7 measures them. Gating on <see cref="BeirRunBudget.IsOptedIn"/> directly keeps the
/// unmeasured cells out of the nightly's 120-minute budget without pretending a parity cost entry
/// answers for them. Warm cost is far below the parity run's cold figure — the corpus embeddings
/// are shared through <see cref="EmbeddingCache"/>, so a machine that has run parity pays only
/// query-side search plus seconds of lexical indexing; cold, it pays the parity run's price first.
/// </para>
/// <para>
/// Selecting one dataset: <c>--filter "DisplayName~BeirAblationTests&amp;DisplayName~scifact"</c>.
/// <c>DisplayName</c>, not <c>FullyQualifiedName</c> — the latter stops at the method name and
/// carries no theory arguments.
/// </para>
/// </summary>
public sealed class BeirAblationTests
{
    private readonly ITestOutputHelper _output;

    public BeirAblationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Gets every described dataset by name.</summary>
    /// <returns>Dataset names.</returns>
    /// <remarks>
    /// One separator only, the space: it is what BEIR's SentenceBERT joins title and text with,
    /// so it is the configuration the anchor row's published comparison was produced under. The
    /// separator ablation belongs to <see cref="BeirParityTests"/> and is not repeated per row.
    /// </remarks>
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
    public async Task NdcgAt10_UnderBm25HybridRrf_MeasuresWithBm25ProvablyContributing(
        string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipWhen(!BeirRunBudget.IsOptedIn(), FormattableString.Invariant($"""
            {datasetName} +bm25 hybrid cell is OPT-IN and did NOT run.
            Cost: DERIVED, not measured — roughly the dataset's parity run when the embedding cache is
            cold, and query-side work only when it is warm; Task 7 measures it and records it in
            BeirRunBudget, which throws on unmeasured dataset/protocol pairs and so cannot gate this
            cell yet.
            To run this cell:
              {BeirRunBudget.OptInVariable}=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build --filter "DisplayName~{nameof(BeirAblationTests)}&DisplayName~{datasetName}"
            """));

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;

        // The space separator, explicitly, for the reason BeirParityTests passes it explicitly:
        // it decides what is embedded, and the ablation's anchor row was validated under it.
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        // ONE list, used twice: the lexical index is built over `units` and the harness indexes
        // `units`. That single shared variable is the whole guarantee that BM25 and the vector
        // store rank the same corpus — the seam cannot enforce it, so the call site must show it.
        var units = BeirHarness.OneChunkPerDocument(dataset.Documents);
        using var row = HybridBm25AblationRow.Over(units);
        var run = await BeirHarness.MeasureAsync(
            descriptor, dataset, units, row, generator, embeddings, ct);

        _output.WriteLine(Describe(descriptor, row, run));

        // Before the number is trusted: if BM25 returned nothing, or returned things that never
        // moved a ranking, this cell is the dense cell wearing a hybrid label.
        row.AssertBm25Contributed(descriptor.Name);
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task NdcgAt10_UnderCrossEncoderRerank_MeasuresWithRerankerProvablyReordering(
        string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipUnless(
            BeirHarness.IsRerankerProvisioned(out var rerankModelPath, out var rerankVocabPath),
            BeirHarness.RerankerSkipReason);
        Assert.SkipWhen(!BeirRunBudget.IsOptedIn(), FormattableString.Invariant($"""
            {datasetName} +reranker cell is OPT-IN and did NOT run.
            Cost: DERIVED, not measured — the dataset's parity run when the embedding cache is cold,
            plus one cross-encoder inference per (query, candidate) pair on top of the query-side
            work; Task 7 measures it and records it in BeirRunBudget, which throws on unmeasured
            dataset/protocol pairs and so cannot gate this cell yet.
            To run this cell:
              {BeirRunBudget.OptInVariable}=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build --filter "DisplayName~{nameof(BeirAblationTests)}&DisplayName~{datasetName}"
            """));

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;

        // The space separator, explicitly, for the reason BeirParityTests passes it explicitly:
        // it decides what is embedded, and the ablation's anchor row was validated under it.
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        // MaxLength stays at OnnxRerankerOptions' default of 512 — ms-marco-MiniLM-L6-v2's own
        // limit. The dense candidates it rescores were embedded at 256; see the row's remarks for
        // why the two truncations differ on purpose.
        using var reranker = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = rerankModelPath,
            VocabPath = rerankVocabPath,
        });
        var row = new RerankedAblationRow(reranker);

        var units = BeirHarness.OneChunkPerDocument(dataset.Documents);
        var run = await BeirHarness.MeasureAsync(
            descriptor, dataset, units, row, generator, embeddings, ct);

        _output.WriteLine(Describe(descriptor, row, run));

        // Before the number is trusted: if the cross-encoder returned its input order — all-equal
        // scores, wrong output tensor — this cell is the dense cell wearing a reranker label.
        row.AssertRerankerReordered(descriptor.Name);
    }

    /// <summary>The cell, labelled the way the published table must label it.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, HybridBm25AblationRow row, BeirRunResult run) =>
        FormattableString.Invariant($"""
            === {descriptor.Name} · {row.Name} ===
            Internal comparison ONLY: the dense anchor is comparable to published ≈ {descriptor.ParityTarget.PublishedNdcgAt10:F5}; this row is comparable to NO published BM25 or hybrid figure.
            BM25 non-empty for {row.Bm25ProductiveQueryCount} of {row.QueryCount} queries; fused ranking diverged from dense on {row.DivergedQueryCount}.
            {run.Describe()}
            """);

    /// <summary>The reranked cell, stating the evidence its guard judges.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, RerankedAblationRow row, BeirRunResult run) =>
        FormattableString.Invariant($"""
            === {descriptor.Name} · {row.Name} ===
            Dense anchor comparable to published ≈ {descriptor.ParityTarget.PublishedNdcgAt10:F5}; this row rescores that anchor's top-k with the cross-encoder.
            Reranked order differed from dense order on {row.ReorderedQueryCount} of {row.QueryCount} queries.
            {run.Describe()}
            """);
}
