using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The <b>parity</b> run: every dataset in <see cref="BeirDatasetDescriptor.All"/> measured under
/// BEIR's own protocol — one chunk per document, truncated at the model's 256 tokens — and checked
/// against the published figure that dataset carries in its
/// <see cref="BeirDatasetDescriptor.ParityTarget"/>.
/// <para>
/// <b>This is the only run in the project that may be compared to a published number</b>, because it
/// is the only one that reproduces the protocol those numbers were produced under. The real run
/// lives in <see cref="BeirRealChunkingTests"/> and is compared to this one instead; see that file
/// for why.
/// </para>
/// <para>
/// The measurement itself is <see cref="BeirHarness"/>, shared with the real run so the two differ
/// in what they index and in nothing else.
/// </para>
/// <para>
/// <b>One test over the datasets, not one file per dataset.</b> The target and the band come off the
/// descriptor, so a second dataset is a second descriptor rather than a copy of this file with three
/// constants edited. Copies are how a band ends up widened to fit a number that should have been
/// investigated instead.
/// </para>
/// <para>
/// Skipped unless <c>RAGNET_ONNX_EMBED_MODEL</c>, <c>RAGNET_ONNX_EMBED_VOCAB</c> and
/// <c>RAGNET_BEIR_CACHE</c> are all usable. This project declares
/// <c>&lt;RequiresSecrets&gt;true&lt;/RequiresSecrets&gt;</c> so nightly.yml selects it and supplies
/// them; it is a project of its own so that declaration does not drag
/// <c>Rag.NET.Benchmarks.Quality.Tests</c>' unit tests out of the gating tier with it.
/// </para>
/// </summary>
public sealed class BeirParityTests
{
    private readonly ITestOutputHelper _output;

    public BeirParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Gets every described dataset, crossed with both title/text separators where that can matter.
    /// </summary>
    /// <returns>Dataset name and separator pairs.</returns>
    /// <remarks>
    /// <para>
    /// Names rather than descriptors, because theory data that is not serializable costs the run its
    /// per-case test names — and a parity failure that cannot say which dataset failed is most of the
    /// way to useless. It is also what makes a single dataset selectable, by
    /// <c>--filter "DisplayName~fiqa"</c>. <b>DisplayName, not FullyQualifiedName</b>: the latter
    /// stops at the method and matches every dataset or none, which is checked rather than assumed —
    /// <c>FullyQualifiedName~scifact</c> selects nothing at all.
    /// </para>
    /// <para>
    /// <b>Both separators only where the corpus has titles.</b> The separator sits between title and
    /// text, so on a corpus with no titles <c>title + sep + text</c> trims back to identical bytes and
    /// the second case measures the first again. FiQA titles none of its 57,638 documents, and one
    /// FiQA measurement is roughly an order of magnitude more expensive than SciFact's ~355 s — an
    /// hour spent re-deriving a number that is equal by construction. The count comes off the
    /// descriptor and <see cref="BeirHarness.LoadAsync"/> asserts it against the archive, so a dataset
    /// that quietly gained titles fails loudly rather than silently losing a case.
    /// </para>
    /// </remarks>
    public static TheoryData<string, string> DatasetsAndSeparators()
    {
        var data = new TheoryData<string, string>();
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            data.Add(descriptor.Name, " ");
            if (descriptor.TitledDocumentCount > 0)
            {
                data.Add(descriptor.Name, "\n");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DatasetsAndSeparators))]
    public async Task NdcgAt10_ThroughTheRealPipeline_LandsWithinToleranceOfPublished(
        string datasetName, string sep)
    {
        // The descriptor is fetched before any gate because the first gate is a question about the
        // dataset. ByName throws on a name no descriptor carries, which is right: an unknown
        // dataset name is a bug in the theory data, not a case to skip past.
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        // First of the three, because the answer is a property of the dataset rather than of this
        // machine. An inapplicable case reporting "no model file" would send the reader to their
        // environment for something no environment can fix.
        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.Parity),
            $"{datasetName} does not declare the Parity protocol applicable, so measuring it would " +
            "produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        // Third, and in this order deliberately. An unprovisioned machine should be told it is
        // unprovisioned; the budget message is for the case that could have run and was not asked
        // to, which is exactly the nightly's situation.
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Parity, out var budgetReason),
            budgetReason);

        var ct = TestContext.Current.CancellationToken;

        // The separator is passed explicitly, not left to the default, because it decides what is
        // embedded: BEIR's SentenceBERT joins title and text with a single space and the published
        // figures were produced that way. A later change to the default must not move these numbers
        // without someone editing this line.
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, sep, ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);
        var run = await BeirHarness.MeasureAsync(
            descriptor, dataset, BeirHarness.OneChunkPerDocument(dataset.Documents), AblationRow.Dense,
            generator, embeddings, ct);

        _output.WriteLine("SEPARATOR=" + (string.Equals(sep, " ", StringComparison.Ordinal) ? "SPACE" : "NEWLINE"));
        _output.WriteLine(Describe(descriptor, run));

        Assert.True(descriptor.ParityTarget.Contains(run.NdcgAt10), FailureMessage(descriptor, run));

        // Second, and the two are answering different questions. The band above asks "does this
        // agree with MTEB", which is a question about the harness and is the reason the phase
        // exists. This asks "did OUR number move", which the band cannot: ±0.02 is wider than most
        // defects, and a cut-then-pool mutation of DocumentRanking passes it green. Band first
        // because a harness that disagrees with the literature should say so before it reports a
        // drift from its own history.
        BeirReproduction.AssertReproduces(datasetName, BeirProtocol.Parity, run.NdcgAt10, _output);
    }

    /// <summary>
    /// The line the run prints. It leads with the dataset name because the theory produces one of
    /// these per dataset.
    /// </summary>
    private static string Describe(BeirDatasetDescriptor descriptor, BeirRunResult run)
    {
        var target = descriptor.ParityTarget;

        return FormattableString.Invariant($"""
            {descriptor.Name} PARITY run (one chunk per document, truncated at 256)
            published ≈ {target.PublishedNdcgAt10:F5}, band {target.LowerBound:F3}–{target.UpperBound:F3}
            {run.Describe()}
            """);
    }

    /// <summary>
    /// The message a red run leaves behind. It names the computed value first, because a parity
    /// failure that only says "false is not true" tells whoever sees it nothing at all.
    /// </summary>
    private static string FailureMessage(BeirDatasetDescriptor descriptor, BeirRunResult run)
    {
        var target = descriptor.ParityTarget;

        return FormattableString.Invariant($"""
            {descriptor.Name} parity FAILED. Measured nDCG@{run.Evaluation.Cutoff} = {run.NdcgAt10:F5}, outside {target.LowerBound:F3}–{target.UpperBound:F3} (published ≈ {target.PublishedNdcgAt10:F5}).
            The published figure is recorded as: {target.Source}
            {Diagnose(descriptor, run)}
            {Describe(descriptor, run)}
            Do NOT widen the band to fit the number: the band is ±{target.Tolerance:F2} because the defects it
            exists to catch move a dataset by considerably more than that.
            """);
    }

    /// <summary>
    /// Names the likely cause from the direction the measurement missed in. The two directions have
    /// nothing in common, which is why the band is two-sided.
    /// </summary>
    private static string Diagnose(BeirDatasetDescriptor descriptor, BeirRunResult run)
    {
        if (run.NdcgAt10 >= descriptor.ParityTarget.LowerBound)
        {
            return "ABOVE the band, which is not good news: it indicates a leak, most likely qrels " +
                "reaching the ranker. Nothing in this harness should be able to score better than " +
                "the model's own published figure.";
        }

        if (descriptor.ExcludesSelfRetrievedDocument)
        {
            return FormattableString.Invariant($"""
                BELOW the band. This dataset excludes the query's own document (MTEB's
                ignore_identical_ids), so check that first: on {descriptor.Name} the query text can BE a
                corpus document, and a self-hit left in the ranking takes rank 1 at cosine 1.0 and
                pushes the relevant document down one place on every query it affects. Then look at
                the chunk-to-document step, at whether the vectors were pooled or normalised twice, at
                the title/text separator, and at whether the whole {descriptor.DocumentCount}-document
                corpus was indexed.
                """);
        }

        return FormattableString.Invariant($"""
            BELOW the band: retrieval or aggregation is wrong. Look at the chunk-to-document step, at
            whether the vectors were pooled or normalised twice, at the title/text separator, and at
            whether the whole {descriptor.DocumentCount}-document corpus was indexed.
            """);
    }
}
