using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The tag-filtered cell: SciFact retrieved out of a store that also holds FiQA.
/// </summary>
/// <remarks>
/// <para>
/// <b>This cell has a target, not a score.</b> Filtering back to SciFact must reproduce SciFact's
/// standalone Real dense figure exactly — the filter either restores the single-corpus ranking or it
/// does not. That framing is the whole reason the cell exists: BEIR chunks carry no tags, so a
/// filtered leg built the obvious way would have invented a tag vocabulary and then measured the
/// invention. The corpus a document came from is a fact about the data.
/// </para>
/// <para>
/// <b>The contamination control is not optional.</b> If FiQA's chunks never displaced SciFact's in
/// the top-k, the unfiltered store would score the same as the filtered one, the filter would be a
/// no-op, and a green cell would be proving nothing at all. So the cell measures the SAME store
/// unfiltered and requires it to score strictly worse. That assertion is what makes the reproduction
/// meaningful rather than decorative.
/// </para>
/// <para>
/// <b>The harness's own summary line is wrong for this cell, and harmlessly so.</b> It prints
/// "141391 units indexed over 62770 of 5183 documents (max 41 per document, -57587 contributed
/// nothing)" — a document count larger than the dataset's and a NEGATIVE contribution figure,
/// because <c>SummariseUnits</c> reasonably assumes the indexed units come from the dataset being
/// measured. Here they deliberately do not: 57,587 of those documents are FiQA's. The metrics are
/// unaffected — they are computed from qrels, not from that line — but a reader meeting a negative
/// count should know it is a one-corpus assumption meeting a two-corpus store rather than a defect
/// in the run.
/// </para>
/// </remarks>
public sealed class BeirTagFilteredTests(ITestOutputHelper output)
{
    private const string OtherCorpus = "fiqa";

    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("scifact")]
    public async Task NdcgAt10_UnderTagFilterOverTwoCorpora_ReproducesTheSingleCorpusFigure(
        string datasetName)
    {
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.RealTagFiltered),
            $"{datasetName} does not declare the RealTagFiltered protocol applicable, so measuring " +
            "it would produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.RealTagFiltered, out var budgetReason),
            budgetReason);

        var ct = TestContext.Current.CancellationToken;

        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);
        var other = await BeirHarness.LoadAsync(
            BeirDatasetDescriptor.ByName(OtherCorpus), cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        var own = Tag(await BeirRealChunkingTests.ChunkAsync(dataset.Documents, ct), datasetName);
        var foreign = Tag(await BeirRealChunkingTests.ChunkAsync(other.Documents, ct), OtherCorpus);

        // One list, one store. The order matters only in that it must not be relied on: the filter
        // has to work on identity, not on where a chunk happens to sit.
        var combined = new List<TextChunk>(own.Count + foreign.Count);
        combined.AddRange(own);
        combined.AddRange(foreign);

        AssertTheStoreIsGenuinelyMixed(own.Count, foreign.Count, datasetName);

        var row = new TagFilteredAblationRow(datasetName, corpusCount: 2);
        var filtered = await BeirHarness.MeasureAsync(
            descriptor, dataset, combined, row, generator, embeddings, ct);

        // The control: the SAME two-corpus store with no filter at all.
        var contaminated = await BeirHarness.MeasureAsync(
            descriptor, dataset, combined, AblationRow.Dense, generator, embeddings, ct);

        Report(descriptor, row, own.Count, foreign.Count, filtered, contaminated);

        // Before the figure is read: the boundary held for every hit, not merely for the judged
        // ones. A filter that leaks only where nothing is judged moves no metric and is still wrong.
        row.AssertEveryHitCarriedTheTag(descriptor.Name);

        AssertTheFilterChangedTheOutcome(filtered.NdcgAt10, contaminated.NdcgAt10, descriptor.Name);

        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.RealTagFiltered, filtered.NdcgAt10, _output);
    }

    /// <summary>Prints both legs together, because either alone invites the wrong reading.</summary>
    private void Report(
        BeirDatasetDescriptor descriptor,
        TagFilteredAblationRow row,
        int own,
        int foreign,
        BeirRunResult filtered,
        BeirRunResult contaminated) =>
        _output.WriteLine(FormattableString.Invariant($"""
            === {descriptor.Name} · {row.Name} ===
            {own} units tagged '{descriptor.Name}', {foreign} tagged '{OtherCorpus}'.
            Hits {row.HitCount} over {row.QueryCount} queries; leaked {row.LeakedHitCount}, untagged {row.UntaggedHitCount}.
            filtered   nDCG@10 {filtered.NdcgAt10:F5}
            unfiltered nDCG@10 {contaminated.NdcgAt10:F5}  (the same store, no filter)
            {filtered.Describe()}
            """));

    /// <summary>Tags every unit with the corpus it was chunked from.</summary>
    /// <remarks>
    /// <see cref="TextChunk.Metadata"/> is <c>init</c>-only but holds a mutable dictionary, so the
    /// tag is added in place rather than by rebuilding every chunk — which would copy 90,000 units
    /// to set one key.
    /// </remarks>
    private static IReadOnlyList<TextChunk> Tag(IReadOnlyList<TextChunk> units, string corpus)
    {
        for (var i = 0; i < units.Count; i++)
        {
            units[i].Metadata[TagFilteredAblationRow.TagKey] = corpus;
        }

        return units;
    }

    /// <summary>
    /// Asserts both corpora actually reached the unit list, before anything is concluded from a
    /// filter applied to them.
    /// </summary>
    /// <remarks>
    /// If the foreign corpus failed to load or chunk, the "two-corpus store" would be a one-corpus
    /// store, the filter would trivially reproduce the single-corpus figure, and the cell would pass
    /// while measuring nothing. That failure is silent in every other assertion here.
    /// </remarks>
    private static void AssertTheStoreIsGenuinelyMixed(int own, int foreign, string datasetName)
    {
        Assert.True(
            own > 0,
            $"{datasetName}: chunking produced no units for the dataset under measurement.");

        Assert.True(
            foreign > 0,
            FormattableString.Invariant(
                $"{datasetName}: chunking produced no units for '{OtherCorpus}', so the store holds ") +
            "one corpus and the filter has nothing to exclude. The cell would reproduce the " +
            "single-corpus figure trivially and prove nothing.");
    }

    /// <summary>
    /// Asserts the unfiltered store scores strictly worse, so the filter demonstrably did work.
    /// </summary>
    /// <remarks>
    /// <b>The anti-vacuity guard, and the one most likely to fire.</b> SciFact's queries are
    /// scientific claims and FiQA's corpus is financial question-answering, so it is entirely
    /// possible that FiQA chunks rarely out-score SciFact's for a SciFact query. If they never did,
    /// the unfiltered figure would equal the filtered one, the filter would be changing nothing, and
    /// the reproduction assertion below it would be comparing SciFact retrieval with itself — green,
    /// and empty. Should this fire, the cell needs a corpus pairing that genuinely competes, not a
    /// relaxed threshold.
    /// </remarks>
    private static void AssertTheFilterChangedTheOutcome(
        double filtered, double contaminated, string datasetName)
    {
        Assert.True(
            contaminated < filtered,
            FormattableString.Invariant(
                $"{datasetName}: the unfiltered two-corpus store scored {contaminated:F5} against ") +
            FormattableString.Invariant($"the filtered {filtered:F5} — not worse, so '{OtherCorpus}' ") +
            "never displaced a judged document and the filter changed nothing. The reproduction " +
            "check would then be comparing single-corpus retrieval with itself. This needs a corpus " +
            "pairing that actually competes, not a softer assertion.");
    }
}
