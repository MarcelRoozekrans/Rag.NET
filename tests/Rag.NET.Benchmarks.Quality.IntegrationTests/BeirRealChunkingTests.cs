using Rag.NET.Benchmarks.Quality;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The <b>real</b> run: the same corpora put through Rag.NET's own chunking and aggregated back to
/// documents by max-pooling — what this library actually does, rather than what BEIR's evaluation
/// script did.
/// <para>
/// <b>Its number is never compared to a published figure, and that is the point of the phase.</b>
/// Published nDCG@10 for <c>all-MiniLM-L6-v2</c> was produced by embedding each corpus entry as one
/// sequence truncated at 256 tokens. Chunking indexes different text, retrieves over a different
/// candidate set and aggregates before the cut, so a chunked number judged against that reference
/// would be a number produced under one protocol measured against one produced under another — the
/// single error this phase exists to avoid. What it is compared to is
/// <see cref="BeirParityTests"/>' protocol, re-measured here on the same corpus in the same process
/// with the same vectors, so the only thing between the two figures is the chunking.
/// </para>
/// <para>
/// <b>And it must differ.</b> Until now the parity run indexed one chunk per document, which makes
/// max-pooling a no-op: <c>DocumentRankingTests</c>' seven-hit fixture has been the only thing
/// guarding the pool-before-cut ordering, on any corpus, ever. If the two runs here agree exactly
/// then either the chunker did not chunk or the aggregation did not aggregate, and either is a
/// finding rather than a pass — so the difference is asserted directly, alongside
/// <see cref="BeirRunResult.IndexedChunkCount"/> and <see cref="BeirRunResult.PooledQueryCount"/>,
/// which say which of the two it was.
/// </para>
/// <para>
/// <b>How expensive, measured rather than guessed.</b>
/// <see cref="Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments"/> runs in seconds and reports
/// what the default strategy actually produces: SciFact 56,707 units from 5,183 documents, ArguAna
/// 82,618 from 8,674, FiQA <b>429,850 from 57,638</b> — roughly 7.5× the corpus, and up to 1,723
/// units from a single document. That is not the ~2× a 512-character chunk size over a 522-character
/// median document suggests, because
/// <see cref="RecursiveChunkingStrategy"/> emits every split part as its own chunk and never merges
/// short ones back up towards <see cref="ChunkingOptions.MaxChunkSize"/>; a document of short lines
/// becomes one chunk per line. So FiQA's real leg embeds 7.5× the parity leg's texts and
/// <see cref="InMemoryVectorStore"/> sorts 429,850 scored entries per query for 6,648 queries. Run
/// SciFact and ArguAna first.
/// </para>
/// <para>
/// Skipped unless <c>RAGNET_ONNX_EMBED_MODEL</c>, <c>RAGNET_ONNX_EMBED_VOCAB</c> and
/// <c>RAGNET_BEIR_CACHE</c> are all usable. Long: this measures each dataset <b>twice</b>. The
/// embedding cache is what makes that affordable — the parity leg's vectors are the ones
/// <see cref="BeirParityTests"/> already wrote, so on a second run only the chunk vectors are new.
/// </para>
/// <para>
/// <b>Selecting one measurement.</b> <c>--filter "DisplayName~arguana"</c> takes every case for one
/// dataset; <c>--filter "DisplayName~BeirRealChunkingTests&amp;DisplayName~arguana"</c> takes this
/// file's two. It must be <c>DisplayName</c> — <c>FullyQualifiedName</c> stops at the method name
/// and carries no theory arguments, so <c>FullyQualifiedName~arguana</c> selects nothing whatsoever
/// and reports that as "no test matches" rather than as a failure.
/// </para>
/// </summary>
public sealed class BeirRealChunkingTests
{
    /// <summary>
    /// How far below the parity run the real run may land before it counts as broken rather than
    /// different.
    /// </summary>
    /// <remarks>
    /// <b>This is not a parity band and must never be read as one.</b> There is no published figure
    /// for this protocol, none is invented here, and nothing in the literature says what chunking
    /// ought to do to nDCG on these corpora — the run is being measured precisely because nobody
    /// knows. What this catches is collapse: chunk ids reaching the metrics instead of document ids
    /// scores near zero, and pooling that drops documents scores far below it. Both are factors, not
    /// fractions of a point, so a half-and-half-again envelope catches them without pretending to
    /// know the true effect size.
    /// </remarks>
    private const double CollapseFloor = 0.5;

    /// <summary>How far above the parity run the real run may land. See <see cref="CollapseFloor"/>.</summary>
    /// <remarks>
    /// Two-sided for the same reason the parity band is: a large jump upwards is more likely a leak
    /// than a win. Chunking genuinely can help — a long document whose one relevant passage is past
    /// token 256 is invisible to the parity run and retrievable here — but not by half.
    /// </remarks>
    private const double LeakCeiling = 1.5;

    private readonly ITestOutputHelper _output;

    public BeirRealChunkingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Gets every described dataset by name.</summary>
    /// <returns>Dataset names.</returns>
    /// <remarks>
    /// One separator only, the default. The separator ablation belongs to the parity run, where the
    /// number it moves is compared to a published one; repeating it here would double the cost of
    /// the most expensive test in the repository to answer a question that has already been asked.
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
    public async Task NdcgAt10_UnderRagNetsOwnChunking_DiffersFromOurParityRunAndStaysNearIt(
        string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        // Every dataset's real leg is opt-in — see BeirRunBudget for why the cheapest of them still
        // does not fit a 120-minute job that builds the solution first. The cheap half of this
        // file's coverage, Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments, is deliberately not
        // gated: it needs no model, runs in seconds, and is what still catches a chunker that
        // stopped chunking on the nightly.
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Real, out var budgetReason),
            budgetReason);

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        // Both legs in one process, off one cache, so the comparison is between two protocols and
        // not between two runs of the machine.
        var parity = await BeirHarness.MeasureAsync(
            descriptor, dataset, BeirHarness.OneChunkPerDocument(dataset.Documents), generator, embeddings, ct);
        var real = await BeirHarness.MeasureAsync(
            descriptor, dataset, await ChunkAsync(dataset.Documents, ct), generator, embeddings, ct);

        _output.WriteLine(Describe(descriptor, parity, real));

        AssertTheProtocolActuallyChunkedAndAggregated(descriptor, real);
        AssertTheTwoRunsDiffer(descriptor, parity, real);
        AssertTheRealRunDidNotCollapse(descriptor, parity, real);

        // Both legs, because this test's headline result is the DELTA between them and a delta is
        // pinned by pinning its ends. The collapse envelope above is 0.5x-1.5x of parity — it
        // catches a protocol that fell over, and it is the right instrument for that — but ArguAna's
        // real leg can lose 0.020 inside it without a word. That is the whole gap:
        // BeirReproduction's window is 0.005 and it is centred on what this repository measured, not
        // on anything published, because for this protocol there is nothing published to centre on.
        BeirReproduction.AssertReproduces(datasetName, BeirProtocol.Parity, parity.NdcgAt10, _output);
        BeirReproduction.AssertReproduces(datasetName, BeirProtocol.Real, real.NdcgAt10, _output);
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments(string datasetName)
    {
        // The cheap half of the expensive test, and worth running on its own: it needs no model and
        // finishes in seconds, so a chunker that stopped chunking is found here rather than an hour
        // later by an nDCG that failed to move. The counts it prints are also what sets the
        // over-retrieval factor in BeirHarness — if MaxChunksPerDocument is 1, retrieval is
        // retrieving exactly the cutoff and pooling has nothing to pool.
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check chunking against the corpora.");

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        var units = await ChunkAsync(dataset.Documents, ct);
        var (maxPerDocument, distinctDocuments) = Summarise(units);

        _output.WriteLine(FormattableString.Invariant(
            $"{descriptor.Name}: {units.Count} units over {distinctDocuments} of {dataset.Documents.Count} documents, max {maxPerDocument} per document"));

        Assert.True(units.Count > dataset.Documents.Count);
        Assert.True(maxPerDocument > 1);
    }

    /// <summary>Gets the largest unit count for one document, and how many documents produced any.</summary>
    private static (int MaxPerDocument, int DistinctDocuments) Summarise(IReadOnlyList<TextChunk> units)
    {
        var perDocument = new Dictionary<string, int>(StringComparer.Ordinal);
        var most = 0;

        for (var i = 0; i < units.Count; i++)
        {
            var documentId = units[i].DocumentId.Value;
            perDocument.TryGetValue(documentId, out var count);
            count++;
            perDocument[documentId] = count;
            if (count > most)
            {
                most = count;
            }
        }

        return (most, perDocument.Count);
    }

    /// <summary>
    /// Chunks every document with the library's default strategy and its default options.
    /// </summary>
    /// <param name="documents">The corpus.</param>
    /// <param name="cancellationToken">Cancels the chunking.</param>
    /// <returns>Every chunk, in corpus order then chunk order.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="RecursiveChunkingStrategy"/> at stock <see cref="ChunkingOptions"/> — 512
    /// characters, 50 of overlap — because "what does this library actually do" is a question about
    /// its defaults. Tuning them would make this a measurement of a configuration nobody ships with.
    /// </para>
    /// <para>
    /// <see cref="BeirDocument.RetrievalText"/> rather than <see cref="BeirDocument.Text"/>, so the
    /// title is inside the chunked text exactly as it is inside the parity run's single unit. Feeding
    /// the two protocols different text would put a second difference between them and there would be
    /// no telling which one moved the number.
    /// </para>
    /// <para>
    /// A document whose text is empty yields no chunks at all, and the strategy is right to do that.
    /// FiQA has 38 such entries — one of them judged relevant — so the real run indexes 38 fewer
    /// documents than the parity run there. That is a genuine protocol difference and it is reported
    /// as <see cref="BeirRunResult.UnindexedDocumentCount"/> rather than papered over with a
    /// placeholder chunk.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<TextChunk>> ChunkAsync(
        IReadOnlyList<BeirDocument> documents, CancellationToken cancellationToken)
    {
        var strategy = new RecursiveChunkingStrategy();
        var options = new ChunkingOptions();
        var units = new List<TextChunk>(documents.Count * 2);

        for (var i = 0; i < documents.Count; i++)
        {
            var section = new DocumentSection
            {
                Text = documents[i].RetrievalText,
                DocumentId = new DocumentId(documents[i].Id),
            };

            await foreach (var chunk in strategy.ChunkAsync(section, options, cancellationToken))
            {
                units.Add(chunk);
            }
        }

        return units;
    }

    /// <summary>
    /// Asserts the two mechanisms this run exists to exercise both did something, before any number
    /// is looked at.
    /// </summary>
    /// <remarks>
    /// Deliberately first. If the difference assertion fails, the next question is always "which of
    /// the two was it" — and asking it here, of the run's shape rather than of its score, is the
    /// difference between a finding and a puzzle.
    /// </remarks>
    private static void AssertTheProtocolActuallyChunkedAndAggregated(
        BeirDatasetDescriptor descriptor, BeirRunResult real)
    {
        Assert.True(
            real.Chunked,
            FormattableString.Invariant($"""
                THE CHUNKER DID NOT CHUNK. {descriptor.Name} produced {real.IndexedChunkCount} units for
                {real.DocumentCount} documents, so every document is one chunk and this run is the parity
                run under another name. RecursiveChunkingStrategy at ChunkingOptions' default 512
                characters should split the {descriptor.DocumentCount}-document corpus into more than
                that; check that RetrievalText is reaching DocumentSection.Text non-empty.
                """));

        Assert.True(
            real.PooledQueryCount > 0,
            FormattableString.Invariant($"""
                THE AGGREGATION DID NOT AGGREGATE. {descriptor.Name} indexed {real.IndexedChunkCount} units
                over {real.IndexedDocumentCount} documents, up to {real.MaxChunksPerDocument} from one
                document, and yet no query retrieved two units of the same document — so max-pooling was
                a no-op on every one of {real.Evaluation.EvaluatedQueryCount} queries and this run still
                does not exercise it. Check the retrieval TopK: pooling cannot see a second chunk that
                top-k truncated away.
                """));
    }

    /// <summary>
    /// Asserts the real run reports a different number from the parity run.
    /// </summary>
    /// <remarks>
    /// The plan's requirement, stated as an assertion rather than as a remark. Two protocols that
    /// index different text over a corpus where half the documents exceed the chunk size cannot
    /// agree to the last digit by accident; if they do, something upstream collapsed them into the
    /// same run and the number is not evidence of anything.
    /// </remarks>
    private static void AssertTheTwoRunsDiffer(
        BeirDatasetDescriptor descriptor, BeirRunResult parity, BeirRunResult real)
    {
        Assert.False(
            Math.Abs(real.NdcgAt10 - parity.NdcgAt10) < double.Epsilon,
            FormattableString.Invariant($"""
                IDENTICAL, WHICH IS A FINDING AND NOT A PASS. {descriptor.Name} scored
                {real.NdcgAt10:F5} under both protocols. The chunked run indexed
                {real.IndexedChunkCount} units against the parity run's {parity.IndexedChunkCount} and
                pooled on {real.PooledQueryCount} queries, so the two runs saw different candidate sets
                and cannot legitimately agree exactly. Something is feeding one measurement's results to
                both.
                """));
    }

    /// <summary>
    /// Asserts the real run stayed in the neighbourhood of the parity run.
    /// </summary>
    /// <remarks>
    /// The one thing the real run is allowed to be measured against, and the message says so, because
    /// the next person to read a failure here will reach for a published figure and there is not one
    /// to reach for.
    /// </remarks>
    private static void AssertTheRealRunDidNotCollapse(
        BeirDatasetDescriptor descriptor, BeirRunResult parity, BeirRunResult real)
    {
        var floor = parity.NdcgAt10 * CollapseFloor;
        var ceiling = parity.NdcgAt10 * LeakCeiling;

        Assert.True(
            real.NdcgAt10 >= floor && real.NdcgAt10 <= ceiling,
            FormattableString.Invariant($"""
                {descriptor.Name} real run nDCG@10 = {real.NdcgAt10:F5}, outside {floor:F5}–{ceiling:F5}.
                That envelope is OUR OWN PARITY RUN ({parity.NdcgAt10:F5}) times {CollapseFloor:F1} and
                {LeakCeiling:F1}. It is NOT a parity band and there is no published figure for this
                protocol — do not reach for {descriptor.ParityTarget.PublishedNdcgAt10:F5}, which was
                measured by truncating each document at 256 tokens and indexing it whole.
                Below the floor: chunk ids reaching IrMetrics instead of document ids, or top-k cutting
                the candidate list before DocumentRanking pooled it.
                Above the ceiling: a leak, the same one the parity band's upper edge watches for.
                {real.Describe()}
                """));
    }

    /// <summary>Both runs side by side, which is the only form in which either is meaningful.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, BeirRunResult parity, BeirRunResult real) =>
        FormattableString.Invariant($"""
            === {descriptor.Name} ===
            PARITY (one chunk per document, truncated at 256) — comparable to published ≈ {descriptor.ParityTarget.PublishedNdcgAt10:F5}
            {parity.Describe()}

            REAL (RecursiveChunkingStrategy at defaults, max-pooled to documents) — comparable to the parity run above, and to NOTHING published
            {real.Describe()}

            delta nDCG@10 = {real.NdcgAt10 - parity.NdcgAt10:+0.00000;-0.00000;0.00000}
            """);
}
