using Rag.NET.Benchmarks.Quality;
using Rag.NET.Models.Options;
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
/// row collects per query while the harness aggregates identically for every row. Once the
/// mechanism is proven, every cell's figure is checked against <see cref="BeirReproduction"/>'s
/// recorded reproduction at ±0.005 — no published figure exists for any cell, so that window is
/// the only thing pinning the ablation table's numbers to anything.
/// </para>
/// <para>
/// <b>Opt-in, through the budget table.</b> <see cref="BeirRunBudget"/> keys costs on
/// dataset × protocol, throws on unmeasured pairs, and since Phase 3.15 measured every cell it
/// carries an entry for each — so each cell gates through
/// <see cref="BeirRunBudget.IsGatedOff"/> on its own pair and skips with its measured cost and the
/// command that runs it, like every other case the nightly cannot afford. Warm cost is far below
/// the parity run's cold figure — the corpus embeddings are shared through
/// <see cref="EmbeddingCache"/>, so a machine that has run parity pays only query-side search plus
/// seconds of lexical indexing; cold, it pays the parity run's price first.
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
        // The descriptor is fetched before any gate because the first gate is a question about the
        // dataset. ByName throws on a name no descriptor carries, which is right: an unknown
        // dataset name is a bug in the theory data, not a case to skip past.
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        // First of the three, because the answer is a property of the dataset rather than of this
        // machine. An inapplicable case reporting "no model file" would send the reader to their
        // environment for something no environment can fix.
        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.HybridBm25),
            $"{datasetName} does not declare the HybridBm25 protocol applicable, so measuring it " +
            "would produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.HybridBm25, out var budgetReason),
            budgetReason);

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

        // Last, once the mechanism is proven: did OUR number move. No published band exists for
        // this cell at all, so BeirReproduction's ±0.005 window is the only thing pinning it.
        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.HybridBm25, run.NdcgAt10, _output);
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task NdcgAt10_UnderCrossEncoderRerank_MeasuresWithRerankerProvablyReordering(
        string datasetName)
    {
        // The descriptor is fetched before any gate because the first gate is a question about the
        // dataset. ByName throws on a name no descriptor carries, which is right: an unknown
        // dataset name is a bug in the theory data, not a case to skip past.
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        // First of the gates — ahead of BOTH provisioning checks, not just the embedder's — because
        // the answer is a property of the dataset rather than of this machine. An inapplicable case
        // reporting "no reranker model" would send the reader to their environment for something no
        // environment can fix.
        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.Reranked),
            $"{datasetName} does not declare the Reranked protocol applicable, so measuring it " +
            "would produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipUnless(
            BeirHarness.IsRerankerProvisioned(out var rerankModelPath, out var rerankVocabPath),
            BeirHarness.RerankerSkipReason);
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Reranked, out var budgetReason),
            budgetReason);

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

        // Last, once the mechanism is proven: did OUR number move. The recorded figures are the
        // post-a912187 ones; a run reproducing the pre-fix figure fails here, by design.
        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.Reranked, run.NdcgAt10, _output);
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task NdcgAt10_UnderCachedHyde_MeasuresWithHydeProvablyDiverging(string datasetName)
    {
        // The descriptor is fetched before any gate because the first gate is a question about the
        // dataset. ByName throws on a name no descriptor carries, which is right: an unknown
        // dataset name is a bug in the theory data, not a case to skip past.
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        // First of the three, because the answer is a property of the dataset rather than of this
        // machine. An inapplicable case reporting "no model file" would send the reader to their
        // environment for something no environment can fix.
        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.Hyde),
            $"{datasetName} does not declare the Hyde protocol applicable, so measuring it would " +
            "produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Hyde, out var budgetReason),
            budgetReason);
        // Deliberately NO skip for an absent hypothetical cache. The cache is the experiment: an
        // opted-in run that cannot find an entry must fail through refuse-on-miss, naming the key
        // and the generation tool — a skip here would read, from the summary, like a measurement.

        var ct = TestContext.Current.CancellationToken;

        // The space separator, explicitly, for the reason BeirParityTests passes it explicitly:
        // it decides what is embedded, and the ablation's anchor row was validated under it.
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        // ONE options object feeds every key component: the identity takes its temperature and the
        // cache takes its prompt template from the same HydeOptions the generation tool defaulted,
        // so the keys computed here are byte-for-byte the keys the tool wrote under.
        var hydeOptions = new HydeOptions();
        var hypotheticals = new HypotheticalCache(
            cacheDirectory,
            HypotheticalModelIdentity.For(hydeOptions.HypothesisTemperature),
            hydeOptions.PromptTemplate,
            HypotheticalCacheMode.RefuseOnMiss);
        var row = new HydeAblationRow(hypotheticals, hydeOptions.HypothesisCount);

        var units = BeirHarness.OneChunkPerDocument(dataset.Documents);
        var run = await BeirHarness.MeasureAsync(
            descriptor, dataset, units, row, generator, embeddings, ct);

        _output.WriteLine(Describe(descriptor, row, run));

        // Before the number is trusted: if the hypothetical path collapsed to the query embedding,
        // this cell is the dense cell wearing a HyDE label.
        row.AssertHydeDiverged(descriptor.Name);

        // Last, once the mechanism is proven: did OUR number move. No published band exists for
        // this cell at all, so BeirReproduction's ±0.005 window is the only thing pinning it.
        BeirReproduction.AssertReproduces(datasetName, BeirProtocol.Hyde, run.NdcgAt10, _output);
    }

    [Fact]
    public async Task HypotheticalCacheIdentity_MatchesTheEntriesTheGenerationToolWrote()
    {
        // The no-drift check, against the real cache: the identity and prompt template constructed
        // the way the HyDE cell constructs them must hit entries the generation tool actually
        // wrote. A drifted identity never throws — it just misses every key — so the only place it
        // can be caught is here, where the frozen entries are on hand to compare against.
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory), BeirHarness.SkipReason);

        var hydeOptions = new HydeOptions();
        var hypotheticals = new HypotheticalCache(
            cacheDirectory,
            HypotheticalModelIdentity.For(hydeOptions.HypothesisTemperature),
            hydeOptions.PromptTemplate,
            HypotheticalCacheMode.RefuseOnMiss);
        Assert.SkipUnless(
            Directory.Exists(hypotheticals.EntryDirectory),
            "No hypothetical cache at this BEIR cache root — the generation tool has not run here.");

        var descriptor = BeirDatasetDescriptor.ByName("scifact");
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, " ", TestContext.Current.CancellationToken);

        var judged = FirstJudgedQuery(dataset);
        for (var hypothesisIndex = 0; hypothesisIndex < hydeOptions.HypothesisCount; hypothesisIndex++)
        {
            Assert.True(
                hypotheticals.Contains(judged.Text, hypothesisIndex),
                FormattableString.Invariant($"""
                    IDENTITY DRIFT, OR AN INCOMPLETE GENERATION RUN. scifact query '{judged.Id}'
                    hypothesis {hypothesisIndex} is not in the cache under identity
                    "{hypotheticals.ModelIdentity}" with the library's default prompt template,
                    yet the cache directory exists. Either HypotheticalModelIdentity or
                    HydeOptions' defaults changed since the generation run — orphaning every entry
                    — or the run never covered scifact. Re-run the generation tool to fill the
                    gap, or reconcile the identity before trusting any HyDE cell.
                    """));
        }
    }

    /// <summary>The first query the split judges — the only kind a hypothetical was paid for.</summary>
    private static BeirQuery FirstJudgedQuery(BeirDataset dataset)
    {
        foreach (var query in dataset.Queries)
        {
            if (dataset.Qrels.ContainsKey(query.Id))
            {
                return query;
            }
        }

        throw new InvalidOperationException(
            $"{dataset.Name} has no judged queries at all, which LoadAsync's descriptor " +
            "assertions should have made impossible.");
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

    /// <summary>The HyDE cell, stating the evidence its guard judges.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, HydeAblationRow row, BeirRunResult run) =>
        FormattableString.Invariant($"""
            === {descriptor.Name} · {row.Name} ===
            Dense anchor comparable to published ≈ {descriptor.ParityTarget.PublishedNdcgAt10:F5}; this row searches with the cached hypotheticals' mean vector instead of the query vector.
            HyDE ranking differed from dense ranking on {row.DivergedQueryCount} of {row.QueryCount} queries.
            {run.Describe()}
            """);
}
