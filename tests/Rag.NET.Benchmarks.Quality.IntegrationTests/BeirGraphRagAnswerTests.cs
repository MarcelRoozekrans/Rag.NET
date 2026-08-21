using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Raptor;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Phase 5.2.2: does GraphRAG help <b>answers</b>? Three retrieval arms answer MultiHop-RAG's
/// queries with one model, one prompt and top-6 context, and every answer is scored against the
/// dataset's gold answer by the dataset authors' own rule — the currency the paper reports in,
/// which the retrieval measurements of 5.2 could not see.
/// <para>
/// <b>Arms.</b> <c>dense</c>: the Real leg's article chunks alone, dense top-6. <c>local</c>:
/// the graph run's store, dense top-500 through <c>GraphLocalSearchBehavior</c> at
/// <c>PageRankWeight = 0.3</c> — the shipped default when these figures were measured, and no longer
/// the default since #239 set it to 0, so <c>GraphRagRun</c> pins it explicitly rather than
/// inheriting it — top-6 of what it returns: article, entity, relationship and
/// report chunks as they come. <c>global</c>: <c>GraphGlobalSearchBehavior</c>'s map/reduce over
/// the community reports, its synthesised answer first and the next five candidates behind it.
/// Same corpus, same queries, same embedder, same answering model at temperature 0.
/// </para>
/// <para>
/// <b>Every model call is cached and replayed refuse-on-miss, like extractions and reports.</b>
/// The answers live in <see cref="AnswersDirectoryName"/> under the same cache root and identity;
/// the default run reads them and calls no model. Generation is opted into explicitly with
/// <see cref="GenerateVariable"/> and an <c>OPENROUTER_API_KEY</c>, and is bounded by
/// <see cref="MaxQueriesVariable"/> for the pilot — the design says 100 stratified queries before
/// the full run, and the pilot's cost is what decides how the <c>global</c> arm runs. <b>This is a
/// deviation from the programme's rule that generation is a tool, not a test</b>, recorded here and
/// in the phase entry: the graph build, the embedder and the retrieval paths this needs all live in
/// <see cref="GraphRagRun"/> and <see cref="BeirHarness"/>, in this project, and moving them into
/// the generation tool is a refactor with its own name. Until then the gate is the same shape as
/// the tool's: nothing is spent unless the operator asks for it in the environment.
/// </para>
/// <para>
/// <b>Scored two ways, and both are printed.</b> The paper's rule (any shared word after
/// lower-casing) is what makes the figures comparable in shape to its Table 6, and the strict rule
/// beside it says how much of that is the rule being generous. Per query type and overall; the
/// 301 null queries separately, as an abstention rate. Pinned in
/// <see cref="MultiHopRagAnswerReproduction"/> on a full run, never on a pilot.
/// </para>
/// </summary>
public sealed class BeirGraphRagAnswerTests
{
    /// <summary>The cache directory the answers live in, beside extractions and reports.</summary>
    public const string AnswersDirectoryName = "graph-answers";

    /// <summary>Set to anything but 0/false, with an <c>OPENROUTER_API_KEY</c>, to fill the answer cache.</summary>
    public const string GenerateVariable = "RAGNET_GRAPHRAG_ANSWERS_GENERATE";

    /// <summary>Bounds the run to N queries, stratified by type — the pilot. Absent means every query.</summary>
    public const string MaxQueriesVariable = "RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES";

    /// <summary>Comma-separated arms to run: <c>dense</c>, <c>local</c>, <c>global</c>. Absent means all three.</summary>
    public const string ArmsVariable = "RAGNET_GRAPHRAG_ANSWERS_ARMS";

    /// <summary>The paper's context depth: six chunks.</summary>
    private const int ContextChunks = 6;

    private const int ProgressEvery = 100;

    /// <summary>Queries in flight at once through the parallel phase — the bound #226 measured clean against OpenRouter.</summary>
    private const int AnswerConcurrency = 8;
    private const int SlabSize = 512;
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    /// <summary>
    /// The one prompt every arm answers with. Versioned in its text: changing a character changes
    /// every cache key and the run refuses on the first miss until regenerated.
    /// </summary>
    private const string PromptTemplate =
        "Answer the question using only the context below. If the context does not contain enough " +
        "information to answer, answer exactly: Insufficient information\n" +
        MultiHopRagAnswerJudge.AnswerInstruction + "\n\n" +
        "Question: {question}\n\nContext:\n{context}";

    private readonly ITestOutputHelper _output;

    public BeirGraphRagAnswerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> Datasets() => BeirGraphRagCorpusTests.Datasets();

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task Accuracy_AgainstTheGoldAnswers_ThreeArms(string datasetName)
    {
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.GraphRag),
            $"{datasetName} does not declare the GraphRag protocol applicable.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(descriptor.Name, BeirProtocol.GraphRag, out var budgetReason),
            budgetReason);

        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);
        var gold = MultiHopRagAnswers.Load(new BeirDatasetCache(cacheDirectory).DirectoryFor(descriptor));
        var selection = SelectQueries(dataset, gold);
        var arms = SelectArms();

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);
        using var answering = OpenAnsweringClient(cacheDirectory, out var generating);

        _output.WriteLine(DescribePlan(descriptor, selection, arms, generating, answering.Cache));

        var startedAt = Stopwatch.GetTimestamp();
        await using var run = await GraphRagRun.BuildAsync(
            dataset.Documents, generator, embeddings, OpenExtractions(cacheDirectory),
            OpenReports(cacheDirectory), ct);
        using var articles = await IndexArticlesAsync(dataset, generator, embeddings, ct);
        await using var corpusRun = await BuildCorpusRaptorRunAsync(arms, dataset, generator, embeddings, answering, _output, ct);
        await using var perDocumentRun = await BuildPerDocumentRaptorRunAsync(arms, dataset, generator, embeddings, answering, _output, ct);
        _output.WriteLine(FormattableString.Invariant(
            $"graph, article and RAPTOR stores built in {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s"));
        var tallies = await AnswerAllAsync(
            selection, arms, run, articles, corpusRun, perDocumentRun, generator, embeddings, answering, gold, ct);
        _output.WriteLine(DescribeResults(descriptor, selection, tallies, answering, Stopwatch.GetElapsedTime(startedAt)));
        _output.WriteLine("every scored answer: " + DumpAnswers(cacheDirectory, tallies, selection.IsPilot));

        foreach (var (arm, tally) in tallies)
        {
            Assert.True(
                tally.Answered == selection.Count,
                $"The {arm} arm answered {tally.Answered} of {selection.Count} selected queries.");
        }

        if (selection.IsPilot)
        {
            _output.WriteLine("PILOT — nothing is pinned. Run without " + MaxQueriesVariable + " to pin.");
            return;
        }

        foreach (var (arm, tally) in tallies)
        {
            MultiHopRagAnswerReproduction.AssertReproduces(
                descriptor.Name, arm, tally.Accuracy(selection.JudgedCount, Rule.Paper), _output);
        }
    }

    /// <summary>
    /// Builds the corpus-scope <see cref="RaptorRun"/> that <see cref="AnswerArm.RaptorCorpus"/>,
    /// <see cref="AnswerArm.RaptorFiltered"/> and <see cref="AnswerArm.RaptorBoost"/> all read from
    /// — one ingestion shared by three arms, not three — or <see langword="null"/> when none of
    /// them was selected, so an unselected arm costs nothing, the same economy the local-search
    /// pass above already gets. The leaf store is in-memory: it exists only to let
    /// <c>RaptorTreeRebuilder</c> enumerate leaves for the single corpus-wide rebuild inside
    /// <see cref="RaptorRun.BuildAsync"/>, and nothing outside that one call ever reads it, so a
    /// file-backed store would only risk leaves accumulating across repeated runs for no benefit.
    /// </summary>
    /// <remarks>
    /// Logs <see cref="RaptorRun.CorpusRebuildCount"/> and the run's other counters to
    /// <paramref name="output"/> the moment the build finishes — the only point this run's stop
    /// condition (a paid run must see <c>CorpusRebuildCount == 1</c> before spending anything on
    /// answers) can actually be read from a <c>dotnet test</c> transcript.
    /// </remarks>
    private static async Task<RaptorRun?> BuildCorpusRaptorRunAsync(
        IReadOnlyList<string> arms,
        BeirDataset dataset,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        CachedGraphRagClient answering,
        ITestOutputHelper output,
        CancellationToken ct)
    {
        var needed = arms.Contains(AnswerArm.RaptorCorpus, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.RaptorFiltered, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.RaptorBoost, StringComparer.Ordinal);

        if (!needed)
        {
            return null;
        }

        var run = await RaptorRun.BuildAsync(
            dataset.Documents, RaptorTreeScope.Corpus, generator, embeddings, answering, ":memory:", ct);
        LogRaptorRunCounters(output, "corpus (raptorcorpus / raptorfiltered / raptorboost)", run);
        return run;
    }

    /// <summary>
    /// Builds the per-document-scope <see cref="RaptorRun"/> <see cref="AnswerArm.Raptor"/> reads
    /// from, the retired variant kept selectable as <see cref="AnswerArm.RaptorCorpus"/>'s control —
    /// or <see langword="null"/> when that arm was not selected.
    /// </summary>
    /// <remarks>
    /// Both <see cref="RaptorRun"/>s are finished before this method returns — RAPTOR summarisation
    /// happens strictly before <c>AnswerAllAsync</c> ever calls <paramref name="answering"/> for an
    /// answer — so the token snapshot logged here, taken from the one client every summariser and
    /// every answer call shares, is exactly the summarisation cost: nothing an answer call adds can
    /// have landed in <paramref name="answering"/>'s counters yet.
    /// </remarks>
    private static async Task<RaptorRun?> BuildPerDocumentRaptorRunAsync(
        IReadOnlyList<string> arms,
        BeirDataset dataset,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        CachedGraphRagClient answering,
        ITestOutputHelper output,
        CancellationToken ct)
    {
        var needed = arms.Contains(AnswerArm.Raptor, StringComparer.Ordinal);
        var run = needed
            ? await RaptorRun.BuildAsync(
                dataset.Documents, RaptorTreeScope.PerDocument, generator, embeddings, answering, ":memory:", ct)
            : null;

        if (run is not null)
        {
            LogRaptorRunCounters(output, "per-document (raptor)", run);
        }

        output.WriteLine(FormattableString.Invariant(
            $"RAPTOR summarisation cost so far (both trees, before any answer is generated): {answering.Calls} calls, {answering.InputTokens} input tokens, {answering.OutputTokens} output tokens"));
        return run;
    }

    /// <summary>Writes one <see cref="RaptorRun"/>'s counters to <paramref name="output"/>, labelled.</summary>
    private static void LogRaptorRunCounters(ITestOutputHelper output, string label, RaptorRun run) =>
        output.WriteLine(FormattableString.Invariant(
            $"RaptorRun[{label}]: LeafCount={run.LeafCount}, SummaryCount={run.SummaryCount}, CorpusRebuildCount={run.CorpusRebuildCount}, SummariserCalls={run.SummariserCalls}"));

    // ── Selection and gates ───────────────────────────────────────────────

    /// <summary>The queries to answer: every judged one plus every null one, or a stratified pilot.</summary>
    private static QuerySelection SelectQueries(BeirDataset dataset, IReadOnlyDictionary<string, MultiHopRagAnswer> gold)
    {
        var max = ReadPositiveInt(MaxQueriesVariable);
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var quota = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var answer in gold.Values)
        {
            byType[answer.QuestionType] = byType.GetValueOrDefault(answer.QuestionType) + 1;
        }

        if (max is { } bound)
        {
            // Proportional stratification, largest remainders last, so a 100-query pilot mirrors
            // 816/856/583/301 rather than taking the first hundred inference queries.
            foreach (var (type, count) in byType)
            {
                quota[type] = (int)Math.Round(bound * (double)count / gold.Count, MidpointRounding.AwayFromZero);
            }
        }

        var selected = new List<BeirQuery>();
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var query in dataset.Queries)
        {
            var type = gold[query.Id].QuestionType;
            if (max is not null && taken.GetValueOrDefault(type) >= quota.GetValueOrDefault(type))
            {
                continue;
            }

            taken[type] = taken.GetValueOrDefault(type) + 1;
            selected.Add(query);
        }

        var judged = selected.Count(q => !string.Equals(gold[q.Id].QuestionType, MultiHopRagAnswers.NullType, StringComparison.Ordinal));
        return new QuerySelection(selected, judged, max is not null);
    }

    private static IReadOnlyList<string> SelectArms()
    {
        var value = Environment.GetEnvironmentVariable(ArmsVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return AnswerArm.All;
        }

        var arms = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant()).ToList();
        foreach (var arm in arms)
        {
            Assert.True(AnswerArm.All.Contains(arm, StringComparer.Ordinal), $"{ArmsVariable} names an unknown arm '{arm}'.");
        }

        return arms;
    }

    /// <summary>
    /// The four RAPTOR arms are selectable through <see cref="ArmsVariable"/> and are members of
    /// <see cref="AnswerArm.All"/> — checked without a model, a corpus or an API key, so a missing
    /// pin or a typo'd name fails here in milliseconds rather than at the end of a paid run.
    /// </summary>
    [Fact]
    public void SelectArms_AcceptsEveryRaptorArmName()
    {
        foreach (var arm in new[] { AnswerArm.RaptorCorpus, AnswerArm.Raptor, AnswerArm.RaptorFiltered, AnswerArm.RaptorBoost })
        {
            Assert.Contains(arm, AnswerArm.All, StringComparer.Ordinal);

            var previous = Environment.GetEnvironmentVariable(ArmsVariable);
            try
            {
                Environment.SetEnvironmentVariable(ArmsVariable, arm);
                Assert.Equal(new[] { arm }, SelectArms());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ArmsVariable, previous);
            }
        }
    }

    private static int? ReadPositiveInt(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static bool IsOn(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The answering client: fill mode with a real model when asked for, refuse-on-miss otherwise.</summary>
    private static CachedGraphRagClient OpenAnsweringClient(string cacheDirectory, out bool generating)
    {
        generating = IsOn(GenerateVariable);
        var identity = GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature);
        var cache = new GraphExtractionCache(
            cacheDirectory, identity,
            generating ? GraphExtractionCacheMode.Fill : GraphExtractionCacheMode.RefuseOnMiss,
            AnswersDirectoryName);

        if (!generating)
        {
            return new CachedGraphRagClient(cache, inner: null, GraphExtractionModelIdentity.ExtractionTemperature);
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(apiKey),
            $"{GenerateVariable} is set but {ApiKeyVariable} is not; nothing can be generated without a key.");

        var model = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
            .GetChatClient(GraphExtractionModelIdentity.ModelName)
            .AsIChatClient();
        return new CachedGraphRagClient(cache, model, GraphExtractionModelIdentity.ExtractionTemperature);
    }

    private static GraphExtractionCache OpenExtractions(string cacheDirectory) =>
        new(cacheDirectory,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.RefuseOnMiss);

    private static GraphExtractionCache OpenReports(string cacheDirectory) =>
        new(cacheDirectory,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.RefuseOnMiss,
            GraphExtractionCache.ReportsDirectoryName);

    // ── The article-only store for the dense arm ──────────────────────────

    /// <summary>Indexes the Real leg's chunks — the same units, through the same chunker — into their own store.</summary>
    private static async Task<InMemoryVectorStore> IndexArticlesAsync(
        BeirDataset dataset, OnnxEmbeddingGenerator generator, EmbeddingCache embeddings, CancellationToken ct)
    {
        var units = await BeirRealChunkingTests.ChunkAsync(dataset.Documents, ct);
        var store = new InMemoryVectorStore();
        for (var start = 0; start < units.Count; start += SlabSize)
        {
            var end = Math.Min(start + SlabSize, units.Count);
            var texts = new string[end - start];
            for (var i = start; i < end; i++)
            {
                texts[i - start] = units[i].Text;
            }

            var vectors = await BeirHarness.EmbedAsync(generator, embeddings, texts, ct);
            var stored = new EmbeddedChunk[end - start];
            for (var i = start; i < end; i++)
            {
                stored[i - start] = new EmbeddedChunk { Chunk = units[i], Embedding = vectors[i - start] };
            }

            await store.StoreAsync(stored, ct);
        }

        return store;
    }

    // ── Answering ─────────────────────────────────────────────────────────

    /// <summary>
    /// Answers every selected query under every arm: local search's retrieval sequentially, since
    /// it walks the SQLite graph store, which holds one connection; everything else —
    /// dense retrieval, global search's map/reduce, and the answering calls themselves — under a
    /// bounded degree of parallelism, since 2,255 queries times a dozen calls apiece is a day if
    /// taken one at a time. Query vectors are embedded up front so the parallel phase reads them
    /// from the cache rather than racing into the embedder.
    /// </summary>
    private async Task<Dictionary<string, ArmTally>> AnswerAllAsync(
        QuerySelection selection,
        IReadOnlyList<string> arms,
        GraphRagRun run,
        InMemoryVectorStore articles,
        RaptorRun? corpusRun,
        RaptorRun? perDocumentRun,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        CachedGraphRagClient answering,
        IReadOnlyDictionary<string, MultiHopRagAnswer> gold,
        CancellationToken ct)
    {
        var tallies = arms.ToDictionary(a => a, _ => new ArmTally(), StringComparer.Ordinal);
        var startedAt = Stopwatch.GetTimestamp();

        await EmbedEveryQueryAsync(selection, generator, embeddings, ct);

        // Local search retrieval, sequentially, for every query that needs it.
        var localContexts = new Dictionary<string, IReadOnlyList<SearchResult>>(StringComparer.Ordinal);
        var controlContexts = new Dictionary<string, IReadOnlyList<SearchResult>>(StringComparer.Ordinal);
        var filteredContexts = new Dictionary<string, IReadOnlyList<SearchResult>>(StringComparer.Ordinal);
        var localSpecContexts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arms.Contains(AnswerArm.Local, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.Control, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.Filtered, StringComparer.Ordinal)
            || arms.Contains(AnswerArm.LocalSpec, StringComparer.Ordinal))
        {
            await CollectGraphStoreContextsAsync(
                run, selection, arms, localContexts, controlContexts, filteredContexts, localSpecContexts,
                startedAt, ct);
        }

        // Dense and global retrieval, and every answer, in parallel under the same bound.
        var done = 0;
        await Parallel.ForEachAsync(
            selection.Queries,
            new ParallelOptions { MaxDegreeOfParallelism = AnswerConcurrency, CancellationToken = ct },
            async (query, token) =>
            {
                var expected = gold[query.Id];
                foreach (var arm in arms)
                {
                    var rendered = string.Equals(arm, AnswerArm.LocalSpec, StringComparison.Ordinal)
                        ? localSpecContexts[query.Id]
                        : RenderContext(arm switch
                        {
                            AnswerArm.Local => localContexts[query.Id],
                            AnswerArm.Control => controlContexts[query.Id],
                            AnswerArm.Filtered => filteredContexts[query.Id],
                            _ => await RetrieveContextAsync(
                                arm, query.Text, run, articles, corpusRun, perDocumentRun,
                                generator, embeddings, answering, _output, token),
                        });

                    var prompt = PromptTemplate
                        .Replace("{question}", query.Text, StringComparison.Ordinal)
                        .Replace("{context}", rendered, StringComparison.Ordinal);

                    var response = await answering.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: token);
                    tallies[arm].Record(arm, query.Id, expected, response.Text ?? string.Empty);
                }

                var completed = Interlocked.Increment(ref done);
                if (completed % ProgressEvery == 0)
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"  answered {completed} of {selection.Queries.Count} queries x {arms.Count} arms, {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s so far, {answering.Cache.Hits} cached / {answering.Cache.Misses} generated"));
                }
            });

        return tallies;
    }

    /// <summary>Every selected query.s vector, once, sequentially, so the parallel phase reads them from the cache.</summary>
    private static async Task EmbedEveryQueryAsync(
        QuerySelection selection, OnnxEmbeddingGenerator generator, EmbeddingCache embeddings, CancellationToken ct)
    {
        var texts = new string[selection.Queries.Count];
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = selection.Queries[i].Text;
        }

        for (var start = 0; start < texts.Length; start += SlabSize)
        {
            var slab = new string[Math.Min(SlabSize, texts.Length - start)];
            Array.Copy(texts, start, slab, 0, slab.Length);
            _ = await BeirHarness.EmbedAsync(generator, embeddings, slab, ct);
        }
    }

    /// <summary>
    /// The six chunks the dense, global and RAPTOR arms hand the model, in the arm's own order.
    /// </summary>
    /// <remarks>
    /// <c>raptorcorpus</c>, <c>raptor</c> and <c>raptorboost</c> each read straight from a
    /// <see cref="RaptorRun"/> built once for every query that needs it — never per query. See the
    /// <c>needsCorpus</c> / <c>needsPerDocument</c> gate and the single <see cref="RaptorRun.BuildAsync"/>
    /// calls in <see cref="Accuracy_AgainstTheGoldAnswers_ThreeArms"/>.
    /// <c>raptorfiltered</c> over-fetches four times the context depth from the same corpus store
    /// and drops every summary chunk before taking six — #247's option (c) shape, reused so that
    /// dropping from an already-truncated six does not under-fill and understate what filtering
    /// buys.
    /// </remarks>
    private static async Task<IReadOnlyList<SearchResult>> RetrieveContextAsync(
        string arm,
        string query,
        GraphRagRun run,
        InMemoryVectorStore articles,
        RaptorRun? corpusRun,
        RaptorRun? perDocumentRun,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        CachedGraphRagClient answering,
        ITestOutputHelper output,
        CancellationToken ct)
    {
        switch (arm)
        {
            case AnswerArm.Dense:
                var vectors = await BeirHarness.EmbedAsync(generator, embeddings, [query], ct);
                return await articles.SearchAsync(vectors[0], new SearchOptions { TopK = ContextChunks }, ct);
            case AnswerArm.Global:
                var global = await run.GlobalSearchAsync(query, answering, ct);
                return Head(global, ContextChunks);
            case AnswerArm.RaptorCorpus:
                return await corpusRun!.SearchAsync(query, RaptorRetrievalMode.Blend, ContextChunks, ct);
            case AnswerArm.Raptor:
                return await perDocumentRun!.SearchAsync(query, RaptorRetrievalMode.Blend, ContextChunks, ct);
            case AnswerArm.RaptorBoost:
                return await corpusRun!.SearchAsync(query, RaptorRetrievalMode.Boost, ContextChunks, ct);
            case AnswerArm.RaptorFiltered:
                return await RetrieveRaptorFilteredAsync(query, corpusRun!, output, ct);
            default:
                throw new ArgumentOutOfRangeException(nameof(arm), arm, "Not an arm retrieved here.");
        }
    }

    /// <summary>
    /// <c>raptorfiltered</c>'s retrieval: over-fetch four times the context depth from the corpus
    /// store at <see cref="RaptorRetrievalMode.Blend"/>, drop every summary chunk, then take six —
    /// #247's option (c) shape, reused so that dropping from an already-truncated six does not
    /// under-fill and understate what filtering buys.
    /// </summary>
    /// <remarks>
    /// <b>The guard below can never fire under today's code</b> — <see cref="Head"/> never returns
    /// fewer results than its source has, so a truncated-below-<see cref="ContextChunks"/> result
    /// with enough survivors to fill it is a contradiction unless something upstream broke. It
    /// exists so a future edit to the over-fetch multiplier, the predicate, or which list gets
    /// handed to <see cref="Head"/> fails loudly here instead of silently biasing
    /// <c>raptorfiltered − dense</c> — the one comparison the pilot's validation gate stops on.
    /// </remarks>
    private static async Task<IReadOnlyList<SearchResult>> RetrieveRaptorFilteredAsync(
        string query, RaptorRun corpusRun, ITestOutputHelper output, CancellationToken ct)
    {
        var pool = await corpusRun.SearchAsync(query, RaptorRetrievalMode.Blend, ContextChunks * 4, ct);
        var survivors = pool.Where(r => !r.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        var filtered = Head(survivors, ContextChunks);

        if (filtered.Count < ContextChunks && survivors.Count >= ContextChunks)
        {
            output.WriteLine(FormattableString.Invariant(
                $"WARNING: raptorfiltered under-filled for query '{query}': only {filtered.Count} of {ContextChunks} chunks came back despite {survivors.Count} non-summary candidates being available in the over-fetched pool of {pool.Count} — check the over-fetch multiplier and the Head() wiring."));
        }

        return filtered;
    }


    /// <summary>The first <paramref name="count"/> results, or all of them when there are fewer.</summary>
    /// <summary>
    /// One local-search pass per query, producing the four contexts that share it: local search's
    /// own results, the unfiltered candidates (<c>control</c>), the candidates with graph-derived
    /// units removed (<c>filtered</c>, #247 option (c)), and Microsoft's local search as specified
    /// (<c>localspec</c>, via <see cref="GraphRagRun.LocalSpecContextAsync"/>).
    /// </summary>
    /// <remarks>
    /// <c>local</c>, <c>control</c> and <c>filtered</c> come from the SAME sequential
    /// <see cref="GraphRagRun.LocalSearchWithCandidatesAsync"/> pass, deliberately. Retrieving
    /// separately per arm would let a difference between them be a difference in what was
    /// retrieved rather than in what was kept, and the whole value of the control is that only one
    /// thing varies. <c>localspec</c> is collected here too, for the same reason, even though its
    /// retrieval does not share the candidate set the other three do — but that retrieval is a
    /// second, wholly separate store round trip per query, so it is skipped when
    /// <paramref name="arms"/> does not select it, the same way an arm not in the selection costs
    /// nothing elsewhere in this file.
    /// </remarks>
    private async Task CollectGraphStoreContextsAsync(
        GraphRagRun run,
        QuerySelection selection,
        IReadOnlyList<string> arms,
        Dictionary<string, IReadOnlyList<SearchResult>> localContexts,
        Dictionary<string, IReadOnlyList<SearchResult>> controlContexts,
        Dictionary<string, IReadOnlyList<SearchResult>> filteredContexts,
        Dictionary<string, string> localSpecContexts,
        long startedAt,
        CancellationToken ct)
    {
        var collectLocalSpec = arms.Contains(AnswerArm.LocalSpec, StringComparer.Ordinal);

        for (var i = 0; i < selection.Queries.Count; i++)
        {
            var local = await run.LocalSearchWithCandidatesAsync(selection.Queries[i].Text, ct);
            localContexts[selection.Queries[i].Id] = Head(local.Results, ContextChunks);
            controlContexts[selection.Queries[i].Id] = Head(local.Candidates, ContextChunks);
            filteredContexts[selection.Queries[i].Id] =
                Head(WithoutGraphDerivedChunks(local.Candidates), ContextChunks);

            if (collectLocalSpec)
            {
                localSpecContexts[selection.Queries[i].Id] =
                    await run.LocalSpecContextAsync(selection.Queries[i].Text, ct);
            }

            if ((i + 1) % ProgressEvery == 0)
            {
                _output.WriteLine(FormattableString.Invariant(
                    $"  local search retrieval: {i + 1} of {selection.Queries.Count}, {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s so far"));
            }
        }
    }

    /// <summary>
    /// Drops every graph-derived chunk — entity, relationship and community report — leaving the
    /// article chunks in their existing order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the <c>graph_type</c> tag the graph behaviours already write
    /// (<c>GraphEntityExtractionBehavior</c>, <c>CommunityDetectionBehavior</c>), so nothing is
    /// re-indexed and nothing about the store changes. That the discriminator already exists is
    /// what makes #247's option (c) cheap; the issue reads as though it needed a migration.
    /// </para>
    /// <para>
    /// The tag is tested for <b>presence</b>, not for a particular value: a future graph kind that
    /// nobody updated this list for would still be excluded, which is the failure direction that
    /// keeps the control honest. Order is preserved, so the survivors are the same ranking the
    /// store produced with the synthetic units simply removed.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SearchResult> WithoutGraphDerivedChunks(IReadOnlyList<SearchResult> results)
    {
        var kept = new List<SearchResult>(results.Count);
        foreach (var result in results)
        {
            var metadata = result.Chunk.Metadata;
            if (metadata is null || !metadata.ContainsKey("graph_type"))
            {
                kept.Add(result);
            }
        }

        return kept;
    }

    private static IReadOnlyList<SearchResult> Head(IReadOnlyList<SearchResult> results, int count)
    {
        var take = Math.Min(count, results.Count);
        var head = new SearchResult[take];
        for (var i = 0; i < take; i++)
        {
            head[i] = results[i];
        }

        return head;
    }

    private static string RenderContext(IReadOnlyList<SearchResult> context)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < context.Count; i++)
        {
            builder.Append('[').Append(i + 1).Append("] ").Append(context[i].Chunk.Text).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    // ── Reporting ─────────────────────────────────────────────────────────

    private static string DescribePlan(
        BeirDatasetDescriptor descriptor, QuerySelection selection, IReadOnlyList<string> arms, bool generating, GraphExtractionCache cache) =>
        FormattableString.Invariant($"""
            === {descriptor.Name} ANSWER-LEVEL EVALUATION (Phase 5.2.2) ===
            {selection.Queries.Count} queries selected ({selection.JudgedCount} judged, {selection.Queries.Count - selection.JudgedCount} null){(selection.IsPilot ? " — PILOT, stratified by type" : "")}
            arms: {string.Join(", ", arms)}; context: top-{ContextChunks}; model: {GraphExtractionModelIdentity.ModelName} at temperature {GraphExtractionModelIdentity.ExtractionTemperature}
            answers: {(generating ? "FILL mode — misses call the model and are cached" : "refuse-on-miss — no model is called")}, entries in {cache.EntryDirectory}
            """);

    private static string DescribeResults(
        BeirDatasetDescriptor descriptor,
        QuerySelection selection,
        Dictionary<string, ArmTally> tallies,
        CachedGraphRagClient answering,
        TimeSpan elapsed)
    {
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant($"""

            === {descriptor.Name} ACCURACY AGAINST THE GOLD ANSWERS — {elapsed.TotalSeconds:F1} s, {answering.Calls} answer requests, {answering.Cache.Hits} cached / {answering.Cache.Misses} generated, {answering.Retries} retries ===
            paper = qa_evaluate.py's any-shared-word rule over punctuation-stripped tokens (headline); raw = that rule with punctuation attached, as the script wrote it; strict = normalised equality
            over the {selection.JudgedCount} judged queries; the null queries are an abstention rate and are reported separately

            """));

        foreach (var (arm, t) in tallies)
        {
            var judged = selection.JudgedCount;
            builder.Append(FormattableString.Invariant($"""
                {arm,-7} overall: paper {t.Accuracy(judged, Rule.Paper):F4}  raw {t.Accuracy(judged, Rule.PaperRaw):F4}  strict {t.Accuracy(judged, Rule.Strict):F4}  | answer sentence used {t.UsedSentence} of {t.Answered}
                {DescribeType(t, MultiHopRagAnswers.InferenceType, "inference ")}
                {DescribeType(t, MultiHopRagAnswers.ComparisonType, "comparison")}
                {DescribeType(t, MultiHopRagAnswers.TemporalType, "temporal  ")}
                {DescribeType(t, MultiHopRagAnswers.NullType, "null      ")}

                """));
        }

        return builder.ToString().TrimEnd();
    }

    private static string DescribeType(ArmTally t, string type, string label) => FormattableString.Invariant(
        $"        {label}  paper {t.Rate(type, Rule.Paper):F4}  raw {t.Rate(type, Rule.PaperRaw):F4}  strict {t.Rate(type, Rule.Strict):F4}  (n={t.Total(type)})  answers: {t.Shapes(type)}");

    /// <summary>
    /// Writes every scored answer to a JSONL file beside the answer cache, so a run can be read
    /// query by query — which is how the pilot found that the raw paper rule was scoring the
    /// model's punctuation rather than its answers.
    /// </summary>
    private static string DumpAnswers(string cacheDirectory, Dictionary<string, ArmTally> tallies, bool pilot)
    {
        var directory = Path.Combine(cacheDirectory, AnswersDirectoryName + "-results");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, (pilot ? "pilot-" : "full-") + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".jsonl");

        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream);
        foreach (var tally in tallies.Values)
        {
            foreach (var a in tally.Answers)
            {
                writer.WriteStartObject();
                writer.WriteString("query", a.QueryId);
                writer.WriteString("type", a.Type);
                writer.WriteString("arm", a.Arm);
                writer.WriteString("gold", a.Gold);
                writer.WriteString("prediction", a.Prediction);
                writer.WriteBoolean("paper", a.Paper);
                writer.WriteBoolean("raw", a.PaperRaw);
                writer.WriteBoolean("strict", a.Strict);
                writer.WriteBoolean("usedSentence", a.UsedSentence);
                writer.WriteEndObject();
                writer.Flush();
                stream.WriteByte((byte)'\n');
                writer.Reset();
            }
        }

        return path;
    }

    // ── Types ─────────────────────────────────────────────────────────────

    private sealed record QuerySelection(IReadOnlyList<BeirQuery> Queries, int JudgedCount, bool IsPilot)
    {
        public int Count => Queries.Count;
    }

    /// <summary>The three rules an answer is scored under.</summary>
    private enum Rule
    {
        /// <summary>The paper's rule over punctuation-stripped tokens — the headline.</summary>
        Paper,

        /// <summary>The paper's rule as the script wrote it, punctuation attached — the diagnostic.</summary>
        PaperRaw,

        /// <summary>Normalised equality.</summary>
        Strict,
    }

    /// <summary>One scored answer, kept so a run can be read query by query afterwards.</summary>
    private sealed record ScoredAnswer(
        string QueryId, string Type, string Gold, string Arm, string Prediction, bool Paper, bool PaperRaw, bool Strict, bool UsedSentence, string Shape);

    /// <summary>
    /// What kind of answer a prediction is, so the report can say whether an arm committed, abstained,
    /// or leaned one way on the yes/no types — the pilot showed global search never saying "no", which
    /// an accuracy alone would have read as comprehension.
    /// </summary>
    private static string ShapeOf(string prediction)
    {
        var p = prediction.Trim().TrimStart('*').ToLowerInvariant();
        if (p.StartsWith("insufficient information", StringComparison.Ordinal)) return "abstain";
        if (p.StartsWith("yes", StringComparison.Ordinal)) return "yes";
        if (p.StartsWith("no", StringComparison.Ordinal) && (p.Length == 2 || !char.IsLetter(p[2]))) return "no";
        if (p.StartsWith("before", StringComparison.Ordinal)) return "before";
        if (p.StartsWith("after", StringComparison.Ordinal)) return "after";
        return "other";
    }

    /// <summary>Correct/total per type under each rule, for one arm, and every scored answer behind them.</summary>
    private sealed class ArmTally
    {
        private readonly Dictionary<string, int[]> _byType = new(StringComparer.Ordinal);
        private readonly List<ScoredAnswer> _answers = [];
        private readonly Dictionary<string, Dictionary<string, int>> _shapes = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public int Answered { get; private set; }

        public int UsedSentence { get; private set; }

        public IReadOnlyList<ScoredAnswer> Answers => _answers;

        public void Record(string arm, string queryId, MultiHopRagAnswer expected, string reply)
        {
            var prediction = MultiHopRagAnswerJudge.ExtractAnswer(reply);
            var scored = new ScoredAnswer(
                queryId, expected.QuestionType, expected.Answer, arm, prediction,
                MultiHopRagAnswerJudge.MatchesByThePaperRuleIgnoringPunctuation(prediction, expected.Answer),
                MultiHopRagAnswerJudge.MatchesByThePaperRule(prediction, expected.Answer),
                MultiHopRagAnswerJudge.MatchesStrictly(prediction, expected.Answer),
                MultiHopRagAnswerJudge.UsedTheAnswerSentence(reply),
                ShapeOf(prediction));

            lock (_gate)
            {
                if (!_shapes.TryGetValue(expected.QuestionType, out var shapes))
                {
                    shapes = new Dictionary<string, int>(StringComparer.Ordinal);
                    _shapes[expected.QuestionType] = shapes;
                }

                shapes[scored.Shape] = shapes.GetValueOrDefault(scored.Shape) + 1;
                if (!_byType.TryGetValue(expected.QuestionType, out var counts))
                {
                    counts = new int[4];
                    _byType[expected.QuestionType] = counts;
                }

                counts[(int)Rule.Paper] += scored.Paper ? 1 : 0;
                counts[(int)Rule.PaperRaw] += scored.PaperRaw ? 1 : 0;
                counts[(int)Rule.Strict] += scored.Strict ? 1 : 0;
                counts[3]++;
                Answered++;
                UsedSentence += scored.UsedSentence ? 1 : 0;
                _answers.Add(scored);
            }
        }

        public int Total(string type) => _byType.TryGetValue(type, out var c) ? c[3] : 0;

        /// <summary>How the arm answered one type: "yes 17, abstain 10, no 0, other 2", most common first.</summary>
        public string Shapes(string type) =>
            _shapes.TryGetValue(type, out var s)
                ? string.Join(", ", s.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " " + kv.Value.ToString(CultureInfo.InvariantCulture)))
                : "-";

        public double Rate(string type, Rule rule) =>
            _byType.TryGetValue(type, out var c) && c[3] > 0 ? c[(int)rule] / (double)c[3] : double.NaN;

        /// <summary>Accuracy over the judged types (null queries excluded) under one rule.</summary>
        public double Accuracy(int judged, Rule rule)
        {
            if (judged == 0)
            {
                return double.NaN;
            }

            var sum = 0;
            foreach (var (type, counts) in _byType)
            {
                if (!string.Equals(type, MultiHopRagAnswers.NullType, StringComparison.Ordinal))
                {
                    sum += counts[(int)rule];
                }
            }

            return sum / (double)judged;
        }
    }
}
