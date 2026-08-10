using System.Diagnostics;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Phase 3.14 Task 4: the Semantic Kernel row of the library comparison, measured through the same
/// TREC run-file boundary as the control — index through <see cref="SemanticKernelEntrant"/>,
/// write the rankings with <see cref="TrecRunFile.Write"/>, read them back, and score the
/// read-back with the one <see cref="IrMetrics"/> behind every published figure.
/// <para>
/// <b>The embedder-identity check is the row's licence to exist.</b> The comparison's design
/// matches exactly one element across entrants — the pinned <c>all-MiniLM-L6-v2</c> — and rejects
/// tables that silently measure embedders. Semantic Kernel embeds inside its own upsert and search
/// paths, so the identity is proven two ways: a known string's vector obtained through SK's search
/// path is asserted equal to <c>OnnxEmbeddingGenerator</c> called directly
/// (<see cref="SemanticKernelsPath_EmbedsAKnownString_ToThePinnedGeneratorsExactVector"/>), and
/// every measured run asserts that the texts SK requested embeddings for are exactly the corpus
/// and query texts the harness supplied, unmodified.
/// </para>
/// <para>
/// <b>The self-exclusion is the writer's, verified empirically.</b> ArguAna's protocol drops the
/// document sharing the query's id before the ranking is cut (MTEB's
/// <c>ignore_identical_ids</c>); Task 2 established this happens before the file exists, because a
/// published run file must already hold the post-exclusion ranking for an outsider's
/// <c>trec_eval</c> to score what we scored. The run here asserts the written file contains zero
/// lines whose query id equals its document id — checked on the file's own bytes, not on the
/// in-memory rankings that produced it.
/// </para>
/// <para>
/// Gated per dataset through <see cref="BeirRunBudget"/> under
/// <see cref="BeirProtocol.SemanticKernel"/>: FiQA's leg would pay the same 1 h 11 m of embedding
/// as its parity leg, so it runs only under <c>RAGNET_BEIR_LONG_RUNS</c> like every expensive
/// case — and its figure is recorded nowhere until somebody pays for it.
/// </para>
/// </summary>
public sealed class BeirSemanticKernelDefaultsTests
{
    /// <summary>
    /// The last field of every line the entrant writes — derived from the assembly actually
    /// loaded, never written down here; see <see cref="SemanticKernelEntrant.LoadedVersion"/> for
    /// the run files the written-down one would have misnamed.
    /// </summary>
    private static string RunTag => SemanticKernelEntrant.RunTag;

    /// <summary>A sentence that appears in no BEIR corpus, embedded on both sides of the check.</summary>
    private const string KnownText =
        "Semantic Kernel must hand this exact sentence to the pinned all-MiniLM-L6-v2 embedder.";

    /// <summary>A second sentence, so the identity check also pins a non-trivial cosine score.</summary>
    private const string OtherText =
        "A different sentence, stored beside it, whose similarity the connector must compute.";

    private readonly ITestOutputHelper _output;

    public BeirSemanticKernelDefaultsTests(ITestOutputHelper output)
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

    [Fact]
    public async Task SemanticKernelsPath_EmbedsAKnownString_ToThePinnedGeneratorsExactVector()
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out _),
            BeirHarness.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        // The direct side: the pinned generator alone, one single-text call — the same batch
        // shape SK's query path produces, so the comparison below may demand bit equality.
        var direct = await generator.GenerateAsync([KnownText], cancellationToken: ct);
        var directKnown = direct[0].Vector.ToArray();
        var directOther = (await generator.GenerateAsync([OtherText], cancellationToken: ct))[0]
            .Vector.ToArray();

        // The SK side: no cache in the source, deliberately — a cache hit satisfying the
        // comparison would prove the cache works, not that SK's path reached this generator.
        using var recorder = new RecordingEmbeddingGenerator(
            (texts, token) => GenerateDirectlyAsync(generator, texts, token));
        var results = await SearchTwoRecordCollectionAsync(recorder, ct);

        // SK asked to embed exactly the two record texts at upsert, then the query at search.
        Assert.Equal([KnownText, OtherText, KnownText], recorder.RequestedTexts);

        // The vector for the known string obtained THROUGH SEMANTIC KERNEL'S SEARCH PATH is the
        // pinned generator's, float for float. This is the Task 4 identity requirement: without
        // it the row could silently measure a different embedder.
        Assert.Equal(384, directKnown.Length);
        Assert.Equal(directKnown, recorder.ProducedVectors[2]);

        // And the connector scored with those vectors, unchanged: cosine of the known string
        // with itself is 1, and with the other text it is exactly the direct vectors' dot
        // product (both are L2-normalised). The tolerance absorbs only the connector's
        // float-precision cosine against this double-precision expectation.
        Assert.Equal("known", results[0].DocumentId);
        Assert.Equal(1.0, results[0].Score, 0.00001);
        Assert.Equal("other", results[1].DocumentId);
        Assert.Equal(Dot(directKnown, directOther), results[1].Score, 0.00001);
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task NdcgAt10_ThroughSemanticKernelAtItsDefaults_ThroughTheTrecRunFileBoundary(
        string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);
        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.SemanticKernel, out var budgetReason),
            budgetReason);

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);
        using var recorder = new RecordingEmbeddingGenerator(
            (texts, token) => BeirHarness.EmbedAsync(generator, embeddings, texts, token));

        // The same spans the Python harness brackets (run_entrant.py): index construction only,
        // then each retrieval call only. Every vector the run will need is prefetched into
        // memory before either span starts, so the indexing figure is SK building its store
        // from vectors it already has — not "the cost of indexing"; embedding and its disk I/O
        // are excluded by construction, and the sidecar's hit/miss counts describe the untimed
        // prefetch, i.e. what the disk really held. A cold cache fails loudly there.
        var hitsBefore = embeddings.Hits;
        var missesBefore = embeddings.Misses;
        var judged = BeirHarness.JudgedQueries(dataset);
        PrefetchEveryVectorTheRunWillNeed(embeddings, dataset.Documents, judged);
        var indexingStartedAt = Stopwatch.GetTimestamp();
        using var collection = SemanticKernelEntrant.CreateCollection(recorder);
        await SemanticKernelEntrant.IndexAsync(collection, dataset.Documents, ct);
        var indexingSeconds = Stopwatch.GetElapsedTime(indexingStartedAt).TotalSeconds;
        var (runs, queryLatencies) = await SemanticKernelEntrant.RetrieveAsync(
            collection, judged, descriptor.ExcludesSelfRetrievedDocument, ct);

        AssertSemanticKernelEmbeddedExactlyTheSuppliedTexts(dataset, judged, recorder);

        var runFilePath = RunFilePath(cacheDirectory, datasetName);
        TrecRunFile.Write(runFilePath, runs, RunTag);
        WriteTimingsSidecar(
            runFilePath, indexingSeconds, queryLatencies,
            checked((int)(embeddings.Hits - hitsBefore)),
            checked((int)(embeddings.Misses - missesBefore)),
            dataset.Documents.Count);
        if (descriptor.ExcludesSelfRetrievedDocument)
        {
            AssertTheWrittenFileHoldsThePostExclusionRanking(runFilePath);
        }

        var readBack = TrecRunFile.Read(runFilePath);
        var evaluation = IrMetrics.Evaluate(readBack, dataset.Qrels, BeirHarness.Cutoff);
        _output.WriteLine(Describe(
            descriptor, runFilePath, evaluation, indexingSeconds, TotalSeconds(queryLatencies)));

        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.SemanticKernel,
            evaluation.NormalizedDiscountedCumulativeGain, _output);
    }

    /// <summary>
    /// Reads every vector the run will need — one per corpus document and one per judged query,
    /// the exact texts Semantic Kernel embeds through the recorder — from disk into the cache's
    /// memory, before any timed span starts. All the run's cache I/O happens here; a cold cache
    /// fails loudly in <see cref="EmbeddingCache.Prefetch"/> rather than being timed.
    /// </summary>
    private static void PrefetchEveryVectorTheRunWillNeed(
        EmbeddingCache embeddings,
        IReadOnlyList<BeirDocument> documents,
        IReadOnlyList<BeirQuery> judged)
    {
        var texts = new string[documents.Count + judged.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            texts[i] = documents[i].RetrievalText;
        }

        for (var i = 0; i < judged.Count; i++)
        {
            texts[documents.Count + i] = judged[i].Text;
        }

        embeddings.Prefetch(texts);
    }

    /// <summary>The pinned generator called directly, in the shape the recorder's source takes.</summary>
    private static async Task<IReadOnlyList<float[]>> GenerateDirectlyAsync(
        Rag.NET.Embeddings.Onnx.OnnxEmbeddingGenerator generator,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var generated = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        var vectors = new float[generated.Count][];
        for (var i = 0; i < generated.Count; i++)
        {
            vectors[i] = generated[i].Vector.ToArray();
        }

        return vectors;
    }

    /// <summary>
    /// Indexes the two known sentences through the entrant's own path and searches with the first,
    /// returning the connector's ranking.
    /// </summary>
    private static async Task<IReadOnlyList<ScoredDocument>> SearchTwoRecordCollectionAsync(
        RecordingEmbeddingGenerator recorder, CancellationToken cancellationToken)
    {
        using var collection = SemanticKernelEntrant.CreateCollection(recorder);
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        await collection.UpsertAsync(
            [
                new SemanticKernelEntrant.CorpusRecord { Id = "known", Text = KnownText },
                new SemanticKernelEntrant.CorpusRecord { Id = "other", Text = OtherText },
            ],
            cancellationToken);

        var results = new List<ScoredDocument>(2);
        await foreach (var result in collection.SearchAsync(
            KnownText, top: 2, options: null, cancellationToken))
        {
            Assert.NotNull(result.Score);
            results.Add(new ScoredDocument(result.Record.Id, result.Score.Value));
        }

        return results;
    }

    /// <summary>
    /// Asserts the texts Semantic Kernel requested embeddings for are exactly the texts the
    /// harness supplied: one per corpus document, one per judged query, none invented and none
    /// modified. A transformed text would not throw anywhere — the pinned model would embed it
    /// happily — so this is the only place a silent rewrite could be caught.
    /// </summary>
    private static void AssertSemanticKernelEmbeddedExactlyTheSuppliedTexts(
        BeirDataset dataset, IReadOnlyList<BeirQuery> judged, RecordingEmbeddingGenerator recorder)
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in dataset.Documents)
        {
            _ = supplied.Add(document.RetrievalText);
        }

        foreach (var query in judged)
        {
            _ = supplied.Add(query.Text);
        }

        var requested = recorder.RequestedTexts;
        Assert.Equal(dataset.Documents.Count + judged.Count, requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            Assert.True(
                supplied.Contains(requested[i]),
                $"Semantic Kernel asked to embed a text the harness never supplied (request {i}): " +
                "either a corpus/query text was modified in SK's pipeline before embedding, or " +
                "something else was embedded — both would make this row measure a different input " +
                "than every other entrant.");
        }
    }

    /// <summary>
    /// Reads the written run file's own lines and asserts none pairs a query with the document
    /// sharing its id — the empirical form of the writer-side self-exclusion check. On ArguAna,
    /// 1,298 of 1,406 queries are byte-identical to their same-id corpus document, so without the
    /// exclusion the file would hold roughly that many self-lines at rank 1; zero is not
    /// achievable by accident.
    /// </summary>
    private static void AssertTheWrittenFileHoldsThePostExclusionRanking(string runFilePath)
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
                !string.Equals(fields[0], fields[2], StringComparison.Ordinal),
                $"'{runFilePath}' line {lineNumber} ranks document '{fields[2]}' for query " +
                $"'{fields[0]}' — the query's own document, which this dataset's protocol " +
                "(ignore_identical_ids) excludes before the ranking is cut. The published run " +
                "file must already hold the post-exclusion ranking.");
        }

        Assert.True(lineNumber > 0, $"'{runFilePath}' is empty.");
    }

    /// <summary>
    /// Where the run file goes: under the BEIR cache, never under the repository, because a run
    /// file's document ids are corpus-derived data and nothing corpus-derived may be committed.
    /// </summary>
    private static string RunFilePath(string cacheDirectory, string datasetName)
    {
        var directory = Path.Combine(cacheDirectory, "runs");
        _ = Directory.CreateDirectory(directory);

        return Path.Combine(directory, datasetName + "-semantic-kernel.trec");
    }

    /// <summary>Cosine of two already-L2-normalised vectors.</summary>
    private static double Dot(float[] left, float[] right)
    {
        double sum = 0;
        for (var i = 0; i < left.Length; i++)
        {
            sum += (double)left[i] * right[i];
        }

        return sum;
    }

    /// <summary>
    /// Writes the entrant's self-measured timings beside its run file. One record per document is
    /// Semantic Kernel's own default — no chunker — so the unit count is the corpus size and no
    /// document contributes more than one unit.
    /// </summary>
    private static void WriteTimingsSidecar(
        string runFilePath,
        double indexingSeconds,
        IReadOnlyDictionary<string, double> queryLatenciesMilliseconds,
        int embeddingCacheHits,
        int embeddingCacheMisses,
        int unitCount) =>
        TimingsSidecar.Write(
            runFilePath,
            new EntrantTimings(
                RunTag, indexingSeconds, queryLatenciesMilliseconds,
                embeddingCacheHits, embeddingCacheMisses, unitCount, MaxUnitsPerDocument: 1),
            BeirHarness.RunIndex);

    /// <summary>
    /// The raw per-query spans summed, for the human-readable line only — derived from the
    /// sidecar's samples rather than timed separately, so the print and the data cannot drift.
    /// </summary>
    private static double TotalSeconds(IReadOnlyDictionary<string, double> latenciesMilliseconds)
    {
        var totalMilliseconds = 0.0;
        foreach (var latency in latenciesMilliseconds.Values)
        {
            totalMilliseconds += latency;
        }

        return totalMilliseconds / 1000;
    }

    /// <summary>The line the run prints, leading with the dataset like every other measurement.</summary>
    private static string Describe(
        BeirDatasetDescriptor descriptor,
        string runFilePath,
        IrEvaluation evaluation,
        double indexingSeconds,
        double retrievalSeconds) =>
        FormattableString.Invariant($"""
            {descriptor.Name} SEMANTIC KERNEL AT ITS DEFAULTS (SK 1.78.0, InMemory connector 1.74.0-preview, MEVD 10.1.0)
            unchunked documents (SK ships no chunker), cosine (connector default), top {SemanticKernelEntrant.RetrievalDepth(descriptor.ExcludesSelfRetrievedDocument)} (protocol: metric cutoff{(descriptor.ExcludesSelfRetrievedDocument ? " + 1 for the self-exclusion" : string.Empty)}), pinned all-MiniLM-L6-v2
            nDCG@{evaluation.Cutoff} = {evaluation.NormalizedDiscountedCumulativeGain:F5}, Recall@{evaluation.Cutoff} = {evaluation.Recall:F5}, MRR@{evaluation.Cutoff} = {evaluation.MeanReciprocalRank:F5}
            {evaluation.EvaluatedQueryCount} queries evaluated, {evaluation.ExcludedQueryCount} excluded for lacking a positive judgement
            indexing {indexingSeconds:F1} s + retrieval {retrievalSeconds:F1} s (self-timed spans; sidecar beside the run file); run file: {runFilePath}
            """);
}
