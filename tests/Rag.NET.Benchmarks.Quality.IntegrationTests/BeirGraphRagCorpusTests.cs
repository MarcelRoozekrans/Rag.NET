using System.Diagnostics;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The comparative GraphRAG run issue #173 asks for: the <b>whole</b> 609-article MultiHop-RAG
/// corpus under the graph path, scored with nDCG@10, Recall@10 and MRR@10 over all 2,255 judged
/// queries, in the shape <see cref="BeirRealChunkingTests"/> reports its leg.
/// <para>
/// <b>What it is differenced against, and what that requires.</b> The anchor is the Real leg's
/// pinned <c>0.63967</c> — same corpus, same 609 articles, same 2,255 queries, same ONNX embedder,
/// same nDCG@10 — so the only thing between the two figures is the graph. That holds only if the
/// two protocols cut the corpus into the same text.
/// <see cref="Chunking_UnderTheGraphPath_IsIdenticalToTheRealProtocols"/> asserts exactly that, it
/// needs no model, and it runs in seconds: <b>read it as the precondition for reading the number
/// below at all.</b>
/// </para>
/// <para>
/// <b>Local search is scored; global search is not, and that is a decision rather than an
/// omission.</b> <c>GraphLocalSearchBehavior</c> returns document chunks, so pooling them to
/// documents and scoring against qrels measures the same quantity the Real leg measures. Global
/// search map-reduces community reports into a synthesised answer; qrels judge documents, so an
/// nDCG over that would be a category error dressed as a comparison. Global search is exercised for
/// one query and described — call count, retrieval count, what came back — and no figure is quoted
/// for it.
/// </para>
/// <para>
/// <b>Two figures come out of one retrieval, and the second is what makes the first attributable.</b>
/// Moving from the dense run to this one changes two things at once: the store gains roughly a
/// quarter of a million entity, relationship and community-report chunks, and
/// <c>GraphLocalSearchBehavior</c> reorders and deduplicates what the store returns. So the run
/// scores the candidate set as well as the behavior's output —
/// <see cref="GraphRagRun.LocalSearchWithCandidatesAsync"/>, one extra pooling per query, no extra
/// retrieval — and prints both. A delta against 0.63967 with only the first figure could not say
/// which of the two changes produced it.
/// </para>
/// <para>
/// <b>Zero model calls, and a miss is a failure rather than a fallback.</b> Extraction and community
/// reports are both replayed out of <see cref="GraphExtractionCache"/> in refuse-on-miss mode with
/// no inner client, exactly as <see cref="GraphRagFunctionsTests"/> does. The corpus needs the full
/// extraction cache (35,296 entries, ~41,000 requests) <b>and</b> a report cache covering the full
/// corpus graph's communities — the slice's reports are keyed on the slice's prompts and are not
/// enough. Neither is ever committed, so this case can no more run on a fresh runner than the Hyde
/// cells can, and an opted-in run without them fails naming the missing key.
/// </para>
/// <para>
/// <b>It has run, and the figure is pinned.</b> Measured 2026-08-15 in 43 m 29 s:
/// nDCG@10 = <c>0.56897</c> for the graph path against <c>0.59658</c> for the candidate-set control,
/// so <b>the graph behaviour costs 0.02761 of nDCG over the candidates it was handed</b> —
/// <see cref="BeirReproduction"/>'s <c>multihop-rag</c> / <c>GraphRag</c> cell holds the figure, the
/// provenance and the two comparisons, and the call below now pins it. What is asserted here
/// besides is collapse, not quality: the corpus went through whole, every judged query was scored,
/// and document ids rather than chunk ids reached the metrics. No quality band is asserted and none
/// may be added — <see cref="BeirReproduction"/>'s ±0.005 is the drift check and this file's
/// assertions must stay answerable without knowing what GraphRAG ought to score.
/// </para>
/// <para>
/// <b>The figure is reproducible only with issue #230's fix (#231) applied.</b> Before it,
/// <c>GraphLocalSearchBehavior</c> deduplicated on <c>ChunkIndex</c> alone rather than on
/// <c>(DocumentId, ChunkIndex)</c> and discarded roughly a third of every candidate set at corpus
/// scale, arbitrarily by document. The run's own <c>chunks in / chunks out</c> counters are the
/// check: this measurement printed 500.0 in, 500.0 out, 0.0 dropped.
/// </para>
/// <para>
/// Gated like every other expensive case: applicability, then provisioning, then
/// <see cref="BeirRunBudget"/> — whose <c>multihop-rag</c> / <c>GraphRag</c> cell states what this
/// costs and prints the command that runs it.
/// </para>
/// </summary>
public sealed class BeirGraphRagCorpusTests
{
    /// <summary>The rank cutoff every figure in this project is quoted at.</summary>
    private const int Cutoff = BeirHarness.Cutoff;

    /// <summary>How often the long run says where it has got to.</summary>
    /// <remarks>
    /// A run measured in hours that prints nothing for the first of them is indistinguishable from
    /// a hung one, and the operator's only recourse is to kill it and lose the whole graph. Every
    /// 250 queries is nine lines over the query set — enough to extrapolate the finish from, and
    /// not so much that the interesting output is buried.
    /// </remarks>
    private const int ProgressEvery = 250;

    private readonly ITestOutputHelper _output;

    public BeirGraphRagCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Gets every dataset that declares the GraphRag protocol applicable, by name.</summary>
    /// <returns>Dataset names.</returns>
    /// <remarks>
    /// A theory over the applicable descriptors rather than a fact about MultiHop-RAG, for two
    /// reasons. The dataset name lands in the display name, which is the only way
    /// <see cref="BeirRunBudget"/>'s <c>DisplayName~GraphRag&amp;DisplayName~multihop-rag</c> filter
    /// can select this case — <c>FullyQualifiedName</c> stops at the method name and carries no
    /// theory arguments, and no method name may contain a hyphen. And a second corpus declaring the
    /// protocol joins here rather than being forgotten.
    /// </remarks>
    public static TheoryData<string> Datasets()
    {
        var data = new TheoryData<string>();
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            if (descriptor.Supports(BeirProtocol.GraphRag))
            {
                data.Add(descriptor.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// The confound check, and the cheapest assertion in this file by several orders of magnitude.
    /// </summary>
    /// <param name="datasetName">The dataset to check.</param>
    /// <remarks>
    /// <para>
    /// <b>The comparison this file exists to make is worthless if this fails.</b> The graph run's
    /// extraction cache was filled through <c>GraphRagSliceIngestion.ChunkAsync</c> — the generation
    /// tool's chunker — while the Real leg's 0.63967 was measured through
    /// <see cref="BeirRealChunkingTests.ChunkAsync"/>. Two chunkers cutting the corpus differently
    /// would put a second difference between the two figures, and nothing downstream could separate
    /// it from the graph's effect.
    /// </para>
    /// <para>
    /// <b>And the failure would not be recoverable by re-chunking.</b> The extraction cache is keyed
    /// on the rendered prompt, which contains the chunk text: changing the chunking invalidates all
    /// 35,296 entries, which cost roughly 41,000 paid model calls. So this is asserted, deliberately,
    /// as a property that must already hold rather than as something the harness could fix.
    /// </para>
    /// <para>
    /// Not gated by <see cref="BeirRunBudget"/> and needing no model, exactly like
    /// <see cref="BeirRealChunkingTests.Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments"/>: it
    /// finishes in seconds, and a divergence found here costs nothing where the same divergence
    /// found by a puzzling nDCG costs the whole run.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task Chunking_UnderTheGraphPath_IsIdenticalToTheRealProtocols(string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to a writable directory to check the two chunkers against the " +
            "corpus they both have to agree on.");

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        var dense = await BeirRealChunkingTests.ChunkAsync(dataset.Documents, ct);
        var graph = await ChunkThroughTheGraphPathAsync(dataset.Documents, ct);

        var callsPerChunk = 1 + GraphRagSliceIngestion.CreateOptions().GleaningPasses;
        _output.WriteLine(FormattableString.Invariant($"""
            {descriptor.Name}: the Real protocol cuts {dataset.Documents.Count} articles into {dense.Count} chunks; the graph path's ingestion driver cuts them into {graph.Count}
            extraction requests that implies: {dense.Count} chunks x {callsPerChunk} calls = {dense.Count * callsPerChunk}
            distinct chunk texts: {CountDistinctTexts(graph)}, so {dense.Count - CountDistinctTexts(graph)} chunks repeat verbatim and share a cache key with an earlier one
            """));

        AssertTheTwoChunkersAgree(descriptor, dense, graph);
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task NdcgAt10_UnderTheGraphPath_IsMeasuredOverTheWholeCorpus(string datasetName)
    {
        // The descriptor first, because the first gate is a question about the dataset rather than
        // about this machine: an inapplicable case reporting "no model file" sends the reader to
        // their environment for something no environment can fix.
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.GraphRag),
            $"{datasetName} does not declare the GraphRag protocol applicable, so measuring it " +
            "would produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(descriptor.Name, BeirProtocol.GraphRag, out var budgetReason),
            budgetReason);

        await MeasureTheCorpusAsync(descriptor, modelPath, vocabPath, cacheDirectory);
    }

    /// <summary>Builds the corpus graph, scores every judged query against it, and reports.</summary>
    private async Task MeasureTheCorpusAsync(
        BeirDatasetDescriptor descriptor, string modelPath, string vocabPath, string cacheDirectory)
    {
        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        var startedAt = Stopwatch.GetTimestamp();
        var hitsBefore = embeddings.Hits;
        var missesBefore = embeddings.Misses;

        // Every article the loader returned, in corpus order — which is what the generation tool's
        // --corpus full run ingested, and the order matters twice over: entity descriptions merge by
        // concatenation, so it decides what every merged description says, and those descriptions
        // are inside the community-report prompts the report cache is keyed on.
        await using var run = await GraphRagRun.BuildAsync(
            dataset.Documents, generator, embeddings, OpenExtractions(cacheDirectory),
            OpenReports(cacheDirectory), ct);

        _output.WriteLine(DescribeTheGraph(dataset, run, Stopwatch.GetElapsedTime(startedAt)));

        var scored = await ScoreEveryJudgedQueryAsync(descriptor, dataset, run, ct);
        var result = Summarise(
            dataset, run, scored, Stopwatch.GetElapsedTime(startedAt),
            embeddings.Hits - hitsBefore, embeddings.Misses - missesBefore);

        _output.WriteLine(Describe(descriptor, run, scored, result));
        await DescribeGlobalSearchAsync(run, dataset, ct);

        AssertTheWholeCorpusWentThroughTheGraphPath(descriptor, run);
        AssertEveryJudgedQueryWasScored(descriptor, result);
        AssertDocumentIdsReachedTheMetrics(descriptor, scored, result);

        BeirReproduction.AssertReproduces(
            descriptor.Name, BeirProtocol.GraphRag, result.NdcgAt10, _output);
    }

    /// <summary>The extraction cache, refuse-on-miss and with no model behind it.</summary>
    private static GraphExtractionCache OpenExtractions(string cacheDirectory) =>
        new(cacheDirectory,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.RefuseOnMiss);

    /// <summary>The community-report cache, refuse-on-miss and in its own directory.</summary>
    private static GraphExtractionCache OpenReports(string cacheDirectory) =>
        new(cacheDirectory,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.RefuseOnMiss,
            GraphExtractionCache.ReportsDirectoryName);

    /// <summary>
    /// Runs local search for every judged query and aggregates both rankings it produces.
    /// </summary>
    /// <remarks>
    /// The judged queries only, through <see cref="BeirHarness.JudgedQueries"/> rather than through
    /// a filter of this file's own: which queries a run retrieves for is the harness's protocol, and
    /// a case that selected its own way could measure a different query set than the leg it is
    /// differenced against. On this corpus that happens to be every query, which is precisely why
    /// the shared helper rather than the coincidence.
    /// </remarks>
    private async Task<GraphScoredRuns> ScoreEveryJudgedQueryAsync(
        BeirDatasetDescriptor descriptor,
        BeirDataset dataset,
        GraphRagRun run,
        CancellationToken cancellationToken)
    {
        var queries = BeirHarness.JudgedQueries(dataset);
        var scored = new GraphScoredRuns(queries.Count);
        var startedAt = Stopwatch.GetTimestamp();

        for (var i = 0; i < queries.Count; i++)
        {
            var excluded = descriptor.ExcludesSelfRetrievedDocument ? queries[i].Id : null;
            var outcome = await run.LocalSearchWithCandidatesAsync(queries[i].Text, cancellationToken);
            scored.Add(queries[i].Id, outcome, excluded);

            if ((i + 1) % ProgressEvery == 0)
            {
                _output.WriteLine(FormattableString.Invariant(
                    $"  local search: {i + 1} of {queries.Count} queries, {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s so far"));
            }
        }

        scored.Elapsed = Stopwatch.GetElapsedTime(startedAt);
        return scored;
    }

    /// <summary>Turns the scored runs into the metrics and the run shape, one <see cref="BeirRunResult"/> each.</summary>
    private static GraphRunSummary Summarise(
        BeirDataset dataset,
        GraphRagRun run,
        GraphScoredRuns scored,
        TimeSpan elapsed,
        long cacheHits,
        long cacheMisses)
    {
        var indexed = run.ChunkCount + run.GraphChunkCount + run.CommunityReportCount;

        var graph = new BeirRunResult(
            IrMetrics.Evaluate(scored.GraphRuns, dataset.Qrels, Cutoff),
            dataset.Documents.Count,
            indexed,
            run.IndexedDocumentCount,
            run.MaxChunksPerDocument,
            scored.PooledQueryCount,
            elapsed,
            cacheHits,
            cacheMisses);

        var candidates = new BeirRunResult(
            IrMetrics.Evaluate(scored.CandidateRuns, dataset.Qrels, Cutoff),
            dataset.Documents.Count,
            indexed,
            run.IndexedDocumentCount,
            run.MaxChunksPerDocument,
            scored.CandidatePooledQueryCount,
            scored.Elapsed,
            cacheHits,
            cacheMisses);

        return new GraphRunSummary(graph, candidates);
    }

    /// <summary>Chunks every document through the driver the extraction cache was filled with.</summary>
    private static async Task<IReadOnlyList<TextChunk>> ChunkThroughTheGraphPathAsync(
        IReadOnlyList<BeirDocument> documents, CancellationToken cancellationToken)
    {
        var units = new List<TextChunk>(documents.Count * 2);
        for (var i = 0; i < documents.Count; i++)
        {
            var chunks = await GraphRagSliceIngestion.ChunkAsync(documents[i], cancellationToken);
            for (var j = 0; j < chunks.Count; j++)
            {
                units.Add(chunks[j]);
            }
        }

        return units;
    }

    /// <summary>
    /// Asserts the two chunkers produced the same units in the same order, text for text.
    /// </summary>
    /// <remarks>
    /// Text, document id and chunk index all three, because they fail differently and only one of
    /// them is visible in a count. Equal counts with different text is a chunker whose boundaries
    /// moved; equal text under a different document id is a run whose hits would join to the wrong
    /// qrels row; a different chunk index changes the vector store's key and would have one chunk
    /// silently overwrite another.
    /// </remarks>
    private static void AssertTheTwoChunkersAgree(
        BeirDatasetDescriptor descriptor,
        IReadOnlyList<TextChunk> dense,
        IReadOnlyList<TextChunk> graph)
    {
        Assert.True(
            dense.Count == graph.Count,
            FormattableString.Invariant($"""
                THE TWO CHUNKERS DISAGREE ON HOW MANY UNITS {descriptor.Name} HAS. The Real protocol
                cut it into {dense.Count} and the graph path's ingestion driver into {graph.Count}.
                Those two runs are differenced against each other, so a difference here is chunking
                and GraphRAG mixed into one number with no way to separate them — and it cannot be
                fixed by re-chunking, because the extraction cache is keyed on prompts containing the
                chunk text and re-chunking invalidates every paid-for entry.
                """));

        for (var i = 0; i < dense.Count; i++)
        {
            Assert.True(
                UnitsMatch(dense[i], graph[i]),
                FormattableString.Invariant($"""
                    THE TWO CHUNKERS DISAGREE ON UNIT {i} OF {dense.Count} FOR {descriptor.Name}.
                    Real: document {dense[i].DocumentId.Value}, chunk {dense[i].ChunkIndex},
                    {dense[i].Text.Length} characters.
                    Graph: document {graph[i].DocumentId.Value}, chunk {graph[i].ChunkIndex},
                    {graph[i].Text.Length} characters.
                    Both are RecursiveChunkingStrategy at stock ChunkingOptions over
                    BeirDocument.RetrievalText, so they can only diverge if one of the two stopped
                    being that — and the comparative GraphRAG figure means nothing while they do.
                    """));
        }
    }

    /// <summary>
    /// How many of these units carry text no earlier unit already carried.
    /// </summary>
    /// <remarks>
    /// <b>Printed so that "the cache holds fewer entries than the run makes requests" is a known
    /// quantity rather than a scare.</b> <see cref="GraphExtractionCache"/> is keyed on the rendered
    /// prompt and the prompt contains the chunk text, so two chunks with identical text are one
    /// entry serving two requests. A corpus of news articles repeats boilerplate, so the counts
    /// legitimately differ — and without this line the only reading available for a short entry
    /// count is a generation run that did not finish.
    /// </remarks>
    private static int CountDistinctTexts(IReadOnlyList<TextChunk> units)
    {
        var seen = new HashSet<string>(units.Count, StringComparer.Ordinal);
        for (var i = 0; i < units.Count; i++)
        {
            _ = seen.Add(units[i].Text);
        }

        return seen.Count;
    }

    /// <summary>Reports whether two units are the same text of the same document at the same index.</summary>
    private static bool UnitsMatch(TextChunk left, TextChunk right) =>
        string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && string.Equals(left.DocumentId.Value, right.DocumentId.Value, StringComparison.Ordinal)
        && left.ChunkIndex == right.ChunkIndex;

    /// <summary>
    /// Asserts the run really did put the whole corpus through the graph path.
    /// </summary>
    /// <remarks>
    /// Weakest first, so a failure names the stage: the corpus arrived whole, extraction produced a
    /// graph, detection clustered it, and every community carries a replayed report. A metric
    /// computed over a graph that failed any of these is a number about nothing, and reading it
    /// before checking them is how a defect gets published as a result.
    /// </remarks>
    private static void AssertTheWholeCorpusWentThroughTheGraphPath(
        BeirDatasetDescriptor descriptor, GraphRagRun run)
    {
        Assert.True(
            run.IndexedDocumentCount == descriptor.DocumentCount,
            FormattableString.Invariant($"""
                {run.IndexedDocumentCount} OF {descriptor.Name}'s {descriptor.DocumentCount}
                ARTICLES CONTRIBUTED A CHUNK. This figure is compared to the Real leg's, measured
                over all of them; a corpus short by any amount is a smaller experiment reported under
                the same name, and the documents that went missing are unretrievable rather than
                badly ranked.
                """));

        Assert.True(
            run.Graph.Entities.Count > 0 && run.Graph.Relationships.Count > 0,
            FormattableString.Invariant($"""
                EXTRACTION PRODUCED {run.Graph.Entities.Count} ENTITIES AND
                {run.Graph.Relationships.Count} RELATIONSHIPS over {run.ChunkCount} chunks. With
                either at zero there is no graph in front of retrieval, local search finds no entity
                chunk to seed from, and this run measures the dense retriever under another name.
                """));

        Assert.True(
            run.Graph.Communities.Count > 1,
            FormattableString.Invariant($"""
                COMMUNITY DETECTION RETURNED {run.Graph.Communities.Count} COMMUNITIES over
                {run.Graph.Entities.Count} entities. One community is the absence of clustering, and
                its report is a summary of the whole corpus.
                """));

        Assert.True(
            run.ReplayedReports == run.Graph.Communities.Count,
            FormattableString.Invariant($"""
                {run.ReplayedReports} REPORT REQUESTS WENT THROUGH THE CACHE FOR
                {run.Graph.Communities.Count} COMMUNITIES. It is one call per community by
                construction, so a mismatch means detection ran over a different community set than
                the graph store now holds — and the report prompts, and therefore the cache keys,
                belong to a graph this run did not build.
                """));
    }

    /// <summary>Asserts the metric was averaged over the query set the anchor was averaged over.</summary>
    /// <remarks>
    /// A run that silently scored fewer queries would still produce a plausible nDCG, and it would
    /// be a different mean from the one 0.63967 is. <see cref="IrEvaluation.ExcludedQueryCount"/>
    /// is checked alongside because a query judged with no positive grade is excluded from the mean
    /// by <see cref="IrMetrics.Evaluate"/> rather than scored zero, and on this corpus there are
    /// none — a number appearing there is the qrels having changed underneath the anchor.
    /// </remarks>
    private static void AssertEveryJudgedQueryWasScored(
        BeirDatasetDescriptor descriptor, GraphRunSummary summary)
    {
        var evaluated = summary.Graph.Evaluation.EvaluatedQueryCount;

        Assert.True(
            evaluated == descriptor.TestQueryCount,
            FormattableString.Invariant($"""
                {evaluated} OF {descriptor.Name}'s {descriptor.TestQueryCount} JUDGED QUERIES WERE
                SCORED, with {summary.Graph.Evaluation.ExcludedQueryCount} excluded for lacking a
                positive judgement. The Real leg's 0.63967 is a mean over all of them; a mean over a
                subset is a different quantity and differencing the two would report the difference
                between two query sets as a difference between two protocols.
                """));
    }

    /// <summary>
    /// Asserts the ranking is made of documents rather than of chunks — the one collapse this file
    /// can detect without knowing what the answer should be.
    /// </summary>
    /// <remarks>
    /// <b>Not a quality band, and nothing here may become one.</b> What GraphRAG scores on this
    /// corpus is now measured — 0.56897, below the candidate-set control it was handed — but that
    /// belongs in <see cref="BeirReproduction"/>, where drift is checked at ±0.005 against a figure
    /// carrying its machine and its date. An envelope in this file would be a second, looser opinion
    /// on the same number, and the reviewer of Phase 3.12 showed what those are worth: a
    /// cut-then-pool mutation passed a ±0.02 band and a 0.5x-1.5x envelope green. What is caught
    /// here instead is the failure that scores exactly zero
    /// and looks like a result: chunk ids reaching <see cref="IrMetrics"/> instead of document ids,
    /// which never matches a qrels row and never throws.
    /// </remarks>
    private static void AssertDocumentIdsReachedTheMetrics(
        BeirDatasetDescriptor descriptor, GraphScoredRuns scored, GraphRunSummary summary)
    {
        Assert.True(
            summary.NdcgAt10 > 0,
            FormattableString.Invariant($"""
                {descriptor.Name} UNDER THE GRAPH PATH SCORED nDCG@{Cutoff} = 0 over
                {summary.Graph.Evaluation.EvaluatedQueryCount} queries, having retrieved
                {scored.RetrievedDocumentCount} document slots in total. Zero is what a run scores
                when chunk ids reach IrMetrics instead of document ids: no chunk id ever matches a
                qrels row, so the metric fails silently rather than loudly. Check that
                DocumentRanking is being handed ChunkHit.DocumentId and not a chunk key.
                """));
    }

    /// <summary>What the graph came out as, printed before a single query is run.</summary>
    /// <remarks>
    /// Printed at this point on purpose: building the corpus graph is the expensive half, and an
    /// operator who has waited an hour for it should see what it produced before waiting another
    /// for the queries — including the counts issue #209 measured, which are what say whether the
    /// clustering is the healthy one or the degenerate one.
    /// </remarks>
    private static string DescribeTheGraph(
        BeirDataset dataset, GraphRagRun run, TimeSpan elapsed) =>
        FormattableString.Invariant($"""
            === multihop-rag GRAPHRAG (whole corpus) — graph built in {elapsed.TotalSeconds:F1} s ===
            {dataset.Documents.Count} articles, {run.ChunkCount} article chunks over {run.IndexedDocumentCount} of them (max {run.MaxChunksPerDocument} from one)
            {run.ReplayedRequests} extraction requests and {run.ReplayedReports} report requests, every one replayed from the cache — no model was called
            graph: {run.Graph.Entities.Count} entities, {run.Graph.Relationships.Count} relationships, {run.Graph.Communities.Count} communities
            indexed: {run.ChunkCount} article chunks + {run.GraphChunkCount} entity/relationship chunks + {run.CommunityReportCount} community reports
            largest community report prompt: {run.LongestReportPrompt} characters
            """);

    /// <summary>
    /// Both figures side by side with the shape that explains them, and the anchor named rather than
    /// subtracted.
    /// </summary>
    /// <remarks>
    /// The delta against the Real leg is deliberately <b>not</b> computed here.
    /// <see cref="BeirReproduction"/> holds 0.63967 as a pinned measurement of a different case, and
    /// a subtraction printed by this test would turn into a headline the moment somebody quoted it
    /// without the caveats underneath — chief among them that the two runs' candidate depths differ
    /// by construction. The number is printed, the anchor is named, and the arithmetic is left to a
    /// human who has read both entries.
    /// </remarks>
    private static string Describe(
        BeirDatasetDescriptor descriptor,
        GraphRagRun run,
        GraphScoredRuns scored,
        GraphRunSummary summary) =>
        FormattableString.Invariant($"""

            === {descriptor.Name} GRAPHRAG (whole corpus, local search, max-pooled to documents) ===
            {summary.Graph.Describe()}

            CANDIDATE-SET CONTROL (the same dense top-{scored.MaxCandidates} the behavior was handed, scored without it)
            {summary.Candidates.Describe()}

            what the behavior did to its candidates, averaged over {scored.QueryCount} queries:
              {scored.MeanCandidates:F1} chunks in, {scored.MeanResults:F1} out — {scored.MeanDroppedByDeduplication:F1} dropped by the ChunkIndex deduplication
              {scored.MeanCandidateDocuments:F1} distinct documents in, {scored.MeanResultDocuments:F1} out
              a community report reached the top {Cutoff} on {scored.QueriesWithCommunityDocumentInTopK} of them, where it is judged by nothing and can only cost rank
            anchor: this dataset's Real leg measured nDCG@{Cutoff} = 0.63967 over the same 609 articles and the same
              {descriptor.TestQueryCount} queries. That leg's store held {run.ChunkCount} chunks and it retrieved
              {Cutoff * run.MaxChunksPerDocument} of them per query; this one held {run.ChunkCount + run.GraphChunkCount + run.CommunityReportCount}
              and retrieved {scored.MaxCandidates}. The chunking is identical — Chunking_UnderTheGraphPath_IsIdenticalToTheRealProtocols
              asserts it — but the candidate depth is not, and any delta carries that with it.
            """);

    /// <summary>
    /// Runs global search once and says what it did, without scoring it.
    /// </summary>
    /// <remarks>
    /// <b>Described, never measured, and the distinction is the whole reason this method returns a
    /// string instead of a number.</b> Global search map-reduces community reports into a
    /// synthesised answer; the qrels judge documents. An nDCG over that output would be a category
    /// error that looks exactly like a comparison, and once printed beside the local figure nothing
    /// would stop it being read as one. One query, because the map-reduce is the same mechanism on
    /// all of them and 2,255 of it would double an already multi-hour run to say the same sentence.
    /// </remarks>
    private async Task DescribeGlobalSearchAsync(
        GraphRagRun run, BeirDataset dataset, CancellationToken cancellationToken)
    {
        var queries = BeirHarness.JudgedQueries(dataset);
        if (queries.Count == 0)
        {
            return;
        }

        var callsBefore = run.GlobalSearchCalls;
        var retrievalsBefore = run.RetrievalCalls;
        var startedAt = Stopwatch.GetTimestamp();
        var global = await run.GlobalSearchAsync(queries[0].Text, cancellationToken);
        var rank = await run.FirstCommunityReportRankAsync(queries[0].Text, cancellationToken);

        _output.WriteLine(FormattableString.Invariant($"""

            GLOBAL SEARCH, described and deliberately NOT scored — its output is a synthesised answer over
            community reports and the qrels judge documents, so no nDCG for it appears anywhere in this file.
              query {queries[0].Id}: {global.Count} results, {run.GlobalSearchCalls - callsBefore} map/reduce calls in {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s
              it reached its reports in {run.RetrievalCalls - retrievalsBefore} retrievals (2 = it went back for them with its own metadata filter)
              best community report ranks {rank} in an unfiltered scan of the whole store
            """));
    }

    /// <summary>The two <see cref="BeirRunResult"/>s one graph run produces.</summary>
    /// <param name="Graph">What <c>GraphLocalSearchBehavior</c> returned, scored.</param>
    /// <param name="Candidates">The dense candidate set it was handed, scored without it.</param>
    private sealed record GraphRunSummary(BeirRunResult Graph, BeirRunResult Candidates)
    {
        /// <summary>Gets the headline figure — the graph path's, not the control's.</summary>
        public double NdcgAt10 => Graph.NdcgAt10;
    }

    /// <summary>
    /// The two document rankings every query produces, and the shape counters that say what the
    /// graph behavior did between them.
    /// </summary>
    /// <remarks>
    /// A mutable accumulator rather than a projection, because the run walks 2,255 queries once and
    /// each one is an expensive retrieval: everything anybody could want to know afterwards has to
    /// be taken on the way past.
    /// </remarks>
    private sealed class GraphScoredRuns
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _graph;
        private readonly Dictionary<string, IReadOnlyList<string>> _candidates;
        private long _candidateChunks;
        private long _resultChunks;
        private long _candidateDocuments;
        private long _resultDocuments;

        public GraphScoredRuns(int queryCount)
        {
            _graph = new Dictionary<string, IReadOnlyList<string>>(queryCount, StringComparer.Ordinal);
            _candidates = new Dictionary<string, IReadOnlyList<string>>(
                queryCount, StringComparer.Ordinal);
        }

        /// <summary>Gets the graph path's document rankings, by query id.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> GraphRuns => _graph;

        /// <summary>Gets the candidate-set control's document rankings, by query id.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> CandidateRuns => _candidates;

        /// <summary>Gets how many queries were scored.</summary>
        public int QueryCount => _graph.Count;

        /// <summary>Gets how long the whole query pass took.</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>Gets the deepest candidate set any query was handed.</summary>
        public int MaxCandidates { get; private set; }

        /// <summary>Gets how many queries pooled two chunks of one document, on the graph path.</summary>
        public int PooledQueryCount { get; private set; }

        /// <summary>Gets the same count for the candidate-set control.</summary>
        public int CandidatePooledQueryCount { get; private set; }

        /// <summary>Gets how many queries put a community report inside the scored cutoff.</summary>
        public int QueriesWithCommunityDocumentInTopK { get; private set; }

        /// <summary>Gets how many document slots were filled across every query's ranking.</summary>
        public long RetrievedDocumentCount { get; private set; }

        /// <summary>Gets the mean candidate-set size.</summary>
        public double MeanCandidates => Mean(_candidateChunks);

        /// <summary>Gets the mean result-list size after the behavior deduplicated.</summary>
        public double MeanResults => Mean(_resultChunks);

        /// <summary>Gets how many chunks per query the behavior's deduplication removed.</summary>
        public double MeanDroppedByDeduplication => MeanCandidates - MeanResults;

        /// <summary>Gets the mean number of distinct documents in the candidate set.</summary>
        public double MeanCandidateDocuments => Mean(_candidateDocuments);

        /// <summary>Gets the mean number of distinct documents surviving into the result.</summary>
        public double MeanResultDocuments => Mean(_resultDocuments);

        /// <summary>Records one query's two rankings and everything measurable about them.</summary>
        public void Add(
            string queryId, GraphRagRun.GraphLocalSearchOutcome outcome, string? excludedDocumentId)
        {
            var candidateHits = ToHits(outcome.Candidates);
            var resultHits = ToHits(outcome.Results);

            var ranking = DocumentRanking.TopDocumentIds(resultHits, Cutoff, excludedDocumentId);
            _graph[queryId] = ranking;
            _candidates[queryId] = DocumentRanking.TopDocumentIds(
                candidateHits, Cutoff, excludedDocumentId);

            _candidateChunks += candidateHits.Count;
            _resultChunks += resultHits.Count;
            _candidateDocuments += CountDistinctDocuments(candidateHits);
            _resultDocuments += CountDistinctDocuments(resultHits);
            RetrievedDocumentCount += ranking.Count;

            MaxCandidates = Math.Max(MaxCandidates, candidateHits.Count);
            PooledQueryCount += HasRepeatedDocument(resultHits, excludedDocumentId) ? 1 : 0;
            CandidatePooledQueryCount +=
                HasRepeatedDocument(candidateHits, excludedDocumentId) ? 1 : 0;
            QueriesWithCommunityDocumentInTopK += ContainsCommunityDocument(ranking) ? 1 : 0;
        }

        private double Mean(long total) => QueryCount == 0 ? 0 : (double)total / QueryCount;

        /// <summary>
        /// Projects search results to the shape <see cref="DocumentRanking"/> takes, which is the
        /// one place the qrels join happens.
        /// </summary>
        /// <remarks>
        /// The parent document id is <see cref="TextChunk.DocumentId"/> for every chunk in the
        /// store, and for entity and relationship chunks that is the article extraction ran over —
        /// so a graph chunk retrieving is that article retrieving, which is exactly the credit
        /// GraphRAG is claiming. The exception is the community reports, whose synthetic document
        /// id is judged by nothing; they are left in rather than filtered out, because a pipeline
        /// returning them is a pipeline whose caller sees them, and hiding them here would flatter
        /// the run.
        /// </remarks>
        private static IReadOnlyList<ChunkHit> ToHits(IReadOnlyList<SearchResult> results)
        {
            var hits = new ChunkHit[results.Count];
            for (var i = 0; i < results.Count; i++)
            {
                var chunk = results[i].Chunk;
                hits[i] = new ChunkHit(
                    FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                    chunk.DocumentId.Value,
                    results[i].Score);
            }

            return hits;
        }

        /// <summary>How many distinct parent documents a hit list names.</summary>
        private static int CountDistinctDocuments(IReadOnlyList<ChunkHit> hits)
        {
            var seen = new HashSet<string>(hits.Count, StringComparer.Ordinal);
            for (var i = 0; i < hits.Count; i++)
            {
                _ = seen.Add(hits[i].DocumentId);
            }

            return seen.Count;
        }

        /// <summary>
        /// Reports whether any document surviving the exclusion contributed two or more hits — the
        /// condition under which max-pooling does anything at all.
        /// </summary>
        private static bool HasRepeatedDocument(
            IReadOnlyList<ChunkHit> hits, string? excludedDocumentId)
        {
            var seen = new HashSet<string>(hits.Count, StringComparer.Ordinal);
            for (var i = 0; i < hits.Count; i++)
            {
                if (string.Equals(hits[i].DocumentId, excludedDocumentId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!seen.Add(hits[i].DocumentId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reports whether the synthetic community document reached a scored ranking.</summary>
        private static bool ContainsCommunityDocument(IReadOnlyList<string> ranking)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                if (string.Equals(
                        ranking[i], GraphRagSliceIngestion.CommunityDocumentId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
