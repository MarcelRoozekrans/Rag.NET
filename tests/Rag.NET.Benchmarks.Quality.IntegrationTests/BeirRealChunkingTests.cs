using Rag.NET.Benchmarks.Quality;
using Rag.NET.Abstractions;
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
/// what the default strategy actually produces: SciFact 20,155 units from 5,183 documents, ArguAna
/// 24,003 from 8,674, FiQA <b>121,236 from 57,638</b> — roughly 2.1× the corpus, and up to 41
/// units from a single document. Before Phase 3.16 taught
/// <see cref="RecursiveChunkingStrategy"/> to pack split parts back up towards
/// <see cref="ChunkingOptions.MaxChunkSize"/>, every part became its own chunk and FiQA produced
/// 429,850 units, 7.5× the corpus and up to 1,723 from one document. FiQA's real leg still embeds
/// 2.1× the parity leg's texts and <see cref="InMemoryVectorStore"/> sorts 121,236 scored entries
/// per query for the 648 judged queries — the only ones the harness retrieves for, since an
/// unjudged query's ranking cannot be scored. Run SciFact and ArguAna first.
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
        // The descriptor is fetched before any gate because the first gate is a question about the
        // dataset. ByName throws on a name no descriptor carries, which is right: an unknown
        // dataset name is a bug in the theory data, not a case to skip past.
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        // First of the three, because the answer is a property of the dataset rather than of this
        // machine. An inapplicable case reporting "no model file" would send the reader to their
        // environment for something no environment can fix.
        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.Real),
            $"{datasetName} does not declare the Real protocol applicable, so measuring it would " +
            "produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        // Last: every dataset's real leg is opt-in — see BeirRunBudget for why the cheapest of them
        // still does not fit a 120-minute job that builds the solution first. The cheap half of this
        // file's coverage, Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments, is deliberately not
        // gated: it needs no model, runs in seconds, and is what still catches a chunker that
        // stopped chunking on the nightly.
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Real, out var budgetReason),
            budgetReason);

        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        // Both legs in one process, off one cache, so the comparison is between two protocols and
        // not between two runs of the machine.
        var parity = await BeirHarness.MeasureAsync(
            descriptor, dataset, BeirHarness.OneChunkPerDocument(dataset.Documents), AblationRow.Dense,
            generator, embeddings, ct);
        var real = await BeirHarness.MeasureAsync(
            descriptor, dataset, await ChunkAsync(dataset.Documents, ct), AblationRow.Dense,
            generator, embeddings, ct);

        _output.WriteLine(Describe(descriptor, parity, real));

        AssertTheProtocolActuallyChunkedAndAggregated(descriptor, real);
        AssertTheTwoRunsDiffer(descriptor, parity, real);
        AssertTheRealRunDidNotCollapse(descriptor, parity, real);

        PinEachLegThatHasAnAnchor(descriptor, parity, real);
    }

    /// <summary>
    /// Pins each leg against what this repository last measured for it.
    /// </summary>
    /// <param name="descriptor">The dataset, which decides whether the parity leg has an anchor.</param>
    /// <param name="parity">The internal control.</param>
    /// <param name="real">The chunked run.</param>
    /// <remarks>
    /// <para>
    /// Both legs where both can be pinned, because this test's headline result is the DELTA between
    /// them and a delta is pinned by pinning its ends. The collapse envelope is 0.5x-1.5x of parity
    /// — it catches a protocol that fell over, and it is the right instrument for that — but
    /// ArguAna's real leg can lose 0.020 inside it without a word. That is the whole gap:
    /// <see cref="BeirReproduction"/>'s window is 0.005 and it is centred on what this repository
    /// measured, not on anything published, because for this protocol there is nothing published to
    /// centre on.
    /// </para>
    /// <para>
    /// <b>A dataset may have no parity anchor at all, and the parity leg still runs.</b> The two
    /// facts are easy to conflate and are not the same. MultiHop-RAG declares
    /// <see cref="BeirProtocol.Parity"/> inapplicable because one chunk truncated at 256 tokens
    /// indexes about a tenth of a 10,340-character article, so measuring it <i>against the
    /// literature</i> would report the first tenth of each article as retrieval quality. None of
    /// that touches the leg's other job: re-measured here on the same corpus in the same process
    /// off the same vectors, it is the control the chunking delta is subtracted from, and a delta
    /// needs its control whether or not anybody ever published a number for it. So the leg is
    /// measured, printed and differenced as always; only the pin is skipped, because
    /// <see cref="BeirReproduction"/> holds no entry to pin it to.
    /// </para>
    /// <para>
    /// <b>The predicate is <see cref="BeirDatasetDescriptor.Supports"/> and it is consulted
    /// everywhere.</b> Two candidates could answer this — that, or
    /// <c>double.IsNaN(descriptor.ParityTarget.PublishedNdcgAt10)</c> — and two predicates that can
    /// disagree is precisely the defect this phase has spent itself removing. <c>Supports</c> wins
    /// on three counts. It is the same question the gates at the top of both theories already ask.
    /// It is the cause rather than the consequence: the NaN target exists <i>because</i> the
    /// protocol is inapplicable, and the descriptor says so itself. And it cannot drift from the
    /// registry this method calls into, because
    /// <c>BeirReproductionTests.EveryApplicableCaseIsRecordedAndNoInapplicableOneIs</c> asserts the
    /// biconditional directly — an entry is required where a protocol is supported and refused
    /// where it is not — so "supports Parity" and "has a Parity entry to assert against" are the
    /// same fact, kept the same by a test rather than by a convention.
    /// </para>
    /// </remarks>
    private void PinEachLegThatHasAnAnchor(
        BeirDatasetDescriptor descriptor, BeirRunResult parity, BeirRunResult real)
    {
        if (descriptor.Supports(BeirProtocol.Parity))
        {
            BeirReproduction.AssertReproduces(
                descriptor.Name, BeirProtocol.Parity, parity.NdcgAt10, _output);
        }

        BeirReproduction.AssertReproduces(
            descriptor.Name, BeirProtocol.Real, real.NdcgAt10, _output);
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
    /// <para>
    /// <b>Internal rather than private, and the reason is a confound.</b> The GraphRAG corpus run's
    /// extraction cache was filled through <c>GraphRagSliceIngestion.ChunkAsync</c>, a second copy
    /// of this method living in the generation tool — and if the two ever cut the corpus
    /// differently, the difference between that run's nDCG@10 and this leg's pinned 0.63967 would
    /// be chunking and GraphRAG mixed together, with no way to tell which moved it. Re-chunking is
    /// not available as a fix: it would invalidate every one of the 35,296 cached extraction keys,
    /// which cost real money. So
    /// <see cref="BeirGraphRagCorpusTests.Chunking_UnderTheGraphPath_IsIdenticalToTheRealProtocols"/>
    /// asserts the two agree chunk for chunk, which it can only do by calling this one.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<TextChunk>> ChunkAsync(
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
    /// Produces the <see cref="BeirProtocol.RealLateChunking"/> units: late-chunked, and carrying
    /// the embeddings late chunking computed for them.
    /// </summary>
    /// <param name="documents">The corpus.</param>
    /// <param name="generator">The token-level embedder, over the same model the dense cells use.</param>
    /// <param name="cancellationToken">Cancels the chunking.</param>
    /// <returns>Units whose <c>Embedding</c> is set by the strategy, for the precomputed index path.</returns>
    /// <remarks>
    /// <para>
    /// <b>These units must not be re-embedded</b>, which is the whole reason
    /// <see cref="BeirHarness.RequirePrecomputedEmbeddings"/> exists. Late chunking's claim is that a
    /// chunk's vector carries whole-document token context; embedding the chunk's text afterwards
    /// through the sentence embedder would measure late chunking's BOUNDARIES with ordinary
    /// embeddings and report it under late chunking's name.
    /// </para>
    /// <para>
    /// <b>The same model as every other cell</b> — <c>RAGNET_ONNX_EMBED_MODEL</c> read at token
    /// level rather than pooled — so the comparison against the Real cell varies the technique and
    /// not the encoder. A different model here would make the difference uninterpretable.
    /// </para>
    /// <para>
    /// <b>No embedding cache.</b> <c>EmbeddingCache</c> is keyed on model identity and text, and
    /// these vectors are not a function of the chunk's text alone — the same chunk text in a
    /// different document embeds differently, which is precisely the property under test. Caching
    /// them by text would be wrong rather than merely unhelpful, so this path pays full cost on
    /// every run and the budget entries say so.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<TextChunk>> LateChunkAsync(
        IReadOnlyList<BeirDocument> documents,
        ITokenEmbeddingGenerator generator,
        CancellationToken cancellationToken)
    {
        var strategy = new LateChunkingStrategy(generator, new LateChunkingOptions());
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
    /// Splits late-chunked units into those the strategy embedded and the documents it could not.
    /// </summary>
    /// <param name="units">The units <see cref="LateChunkAsync"/> produced.</param>
    /// <returns>The embedded units, and the distinct document ids excluded.</returns>
    /// <exception cref="InvalidOperationException">
    /// More than <see cref="MaxExcludedFraction"/> of the units carry no embedding.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Excluding is not the same as substituting, and only one of them is honest.</b> A unit the
    /// strategy could not embed is simply not in the index — the corpus is that much smaller and the
    /// cell says by how much. Filling it in with an ordinary sentence embedding would put a
    /// differently-computed vector in the same index under late chunking's name, which is what
    /// <see cref="BeirHarness.RequirePrecomputedEmbeddings"/> refuses. That guard stays absolute;
    /// this decides what is handed to it.
    /// </para>
    /// <para>
    /// <b>Why a ceiling rather than a plain filter.</b> On 2026-09-03 the first late-chunking run
    /// over SciFact left 1,401 of 9,506 units unembedded — a <c>MaxTokens</c> default the model could
    /// not honour, since fixed. A partition that quietly excluded those would have reported a figure
    /// computed over 85% of the corpus and called it a measurement. After the fix the same corpus
    /// leaves 20 of 9,527: a documented tail of text carrying control characters that BERT's own
    /// reference implementation deletes too. <b>A tail is reportable; a collapse is a run to
    /// investigate</b>, and the only thing separating them is a threshold, so there is one.
    /// </para>
    /// <para>
    /// One percent admits the observed tail at 0.21% and refuses the observed collapse at 14.7%,
    /// with an order of magnitude of clearance either side. It is a judgement, not a derivation, and
    /// it is written here so the next person changing it can see what it was chosen against.
    /// </para>
    /// </remarks>
    internal static (IReadOnlyList<TextChunk> Kept, IReadOnlyList<string> ExcludedDocumentIds)
        PartitionLateChunked(IReadOnlyList<TextChunk> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        var kept = new List<TextChunk>(units.Count);
        var excluded = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < units.Count; i++)
        {
            var embedding = units[i].Embedding;
            if (embedding is null || embedding.Value.Length == 0)
            {
                if (seen.Add(units[i].DocumentId.Value))
                {
                    excluded.Add(units[i].DocumentId.Value);
                }

                continue;
            }

            kept.Add(units[i]);
        }

        var missing = units.Count - kept.Count;
        if (missing > units.Count * MaxExcludedFraction)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{missing} of {units.Count} units carry no late-chunked embedding, above the ") +
                FormattableString.Invariant($"{MaxExcludedFraction:P0} this cell will exclude. ") +
                "That is a systemic failure rather than the documented tail of text carrying " +
                "control characters, and excluding it would produce a figure over a corpus this " +
                "size while reporting it as the whole one. Investigate the generator before " +
                "measuring: the first occurrence, 1,401 of 9,506 on SciFact, was a MaxTokens " +
                "default the model could not honour.");
        }

        return (kept, excluded);
    }

    /// <summary>The share of units this cell will exclude before treating the run as broken.</summary>
    private const double MaxExcludedFraction = 0.01;

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
                protocol — {DescribeWhatNotToReachFor(descriptor)}
                Below the floor: chunk ids reaching IrMetrics instead of document ids, or top-k cutting
                the candidate list before DocumentRanking pooled it.
                Above the ceiling: a leak, the same one the parity band's upper edge watches for.
                {real.Describe()}
                """));
    }

    /// <summary>
    /// Names the published figure a reader must not mistake this envelope for — or says there is
    /// no such figure, which for a dataset with no parity anchor is the more useful warning.
    /// </summary>
    /// <remarks>
    /// The absent case must never format <see cref="BeirParityTarget.PublishedNdcgAt10"/>: it is
    /// <see cref="double.NaN"/> by deliberate design, and "do not reach for NaN" reads as a bug in
    /// the harness rather than as the deliberate determination it records.
    /// </remarks>
    /// <remarks>
    /// The supported branch keeps the original wording and its line break exactly, so a dataset
    /// with an anchor produces the same failure text to the byte as it did before this method
    /// existed.
    /// </remarks>
    private static string DescribeWhatNotToReachFor(BeirDatasetDescriptor descriptor) =>
        descriptor.Supports(BeirProtocol.Parity)
            ? FormattableString.Invariant($"""
                do not reach for {descriptor.ParityTarget.PublishedNdcgAt10:F5}, which was
                measured by truncating each document at 256 tokens and indexing it whole.
                """)
            : "and this dataset has no published anchor under any protocol, so there is nothing "
              + "to reach for and nothing to be tempted by.";

    /// <summary>
    /// Says what the parity leg may be compared with outside this process, which for a dataset
    /// with no parity anchor is nothing.
    /// </summary>
    private static string DescribeParityLegHeading(BeirDatasetDescriptor descriptor) =>
        descriptor.Supports(BeirProtocol.Parity)
            ? FormattableString.Invariant(
                $"comparable to published ≈ {descriptor.ParityTarget.PublishedNdcgAt10:F5}")
            : "NO published anchor exists for this dataset, so this leg is the internal control "
              + "and nothing else — it is not pinned and it is not comparable to the literature";

    /// <summary>Both runs side by side, which is the only form in which either is meaningful.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor, BeirRunResult parity, BeirRunResult real) =>
        FormattableString.Invariant($"""
            === {descriptor.Name} ===
            PARITY (one chunk per document, truncated at 256) — {DescribeParityLegHeading(descriptor)}
            {parity.Describe()}

            REAL (RecursiveChunkingStrategy at defaults, max-pooled to documents) — comparable to the parity run above, and to NOTHING published
            {real.Describe()}

            delta nDCG@10 = {real.NdcgAt10 - parity.NdcgAt10:+0.00000;-0.00000;0.00000}
            """);
}
