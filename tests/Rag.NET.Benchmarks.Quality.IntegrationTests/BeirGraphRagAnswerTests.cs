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

    /// <summary>
    /// Comma-separated arms to run. Absent means <b>every arm that has a recorded figure</b> — not
    /// every arm in <see cref="AnswerArm.All"/>.
    /// </summary>
    /// <remarks>
    /// The default deliberately skips arms whose reproduction entry carries an empty figure array,
    /// so a freshly added, unmeasured arm cannot break the replay run that re-verifies the pinned
    /// figures. Naming an arm here overrides that: an unmeasured arm named explicitly still runs,
    /// which is how it gets measured in the first place. The filter is data-driven, so an arm
    /// rejoins the default set the moment a figure is pinned for it — no code change.
    /// </remarks>
    public const string ArmsVariable = "RAGNET_GRAPHRAG_ANSWERS_ARMS";

    /// <summary>The paper's context depth: six chunks.</summary>
    private const int ContextChunks = 6;

    /// <summary>
    /// <c>raptorfiltered</c>'s over-fetch multiplier: pull <c>ContextChunks * 4</c> candidates from
    /// the corpus store before dropping summaries and taking six — #247's option (c)
    /// over-fetch-and-drop shape, reused here. This is a second, unrelated over-fetch factor from
    /// <see cref="RaptorRun.SearchAsync"/>'s own <c>CandidateMultiplier = 3.0</c> pinned for the
    /// <c>raptorboost</c> arm: one over-fetches to survive a drop after retrieval, the other
    /// over-fetches so <c>Boost</c> can promote a summary into the truncated top-k. A reader
    /// comparing the two pinned figures should not read the differing multipliers as a difference in
    /// what was measured.
    /// </summary>
    private const int RaptorFilteredOverFetchMultiplier = 4;

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
        AssertSelectionIsNotEmpty(selection, gold);
        var arms = SelectArms(descriptor.Name, _output);

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
        LogRaptorSummarisationCostSoFar(_output, answering, corpusRun, perDocumentRun);

        _output.WriteLine(FormattableString.Invariant(
            $"graph, article and RAPTOR stores built in {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s"));
        var tallies = await AnswerAllAsync(
            selection, arms, run, articles, corpusRun, perDocumentRun, generator, embeddings, answering, gold, ct);
        _output.WriteLine(DescribeResults(descriptor, selection, tallies, answering, Stopwatch.GetElapsedTime(startedAt)));
        _output.WriteLine("every scored answer: " + DumpAnswers(cacheDirectory, tallies, selection.IsPilot));
        _output.WriteLine("RaptorRun counters: " + DumpRaptorCounters(
            cacheDirectory, selection.IsPilot, ("corpus", corpusRun), ("per-document", perDocumentRun)));

        AssertEveryArmAnsweredEveryQuery(tallies, selection);

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

    /// <summary>Every arm must have answered every selected query — split out of the theory (MA0051).</summary>
    private static void AssertEveryArmAnsweredEveryQuery(Dictionary<string, ArmTally> tallies, QuerySelection selection)
    {
        foreach (var (arm, tally) in tallies)
        {
            Assert.True(
                tally.Answered == selection.Count,
                $"The {arm} arm answered {tally.Answered} of {selection.Count} selected queries.");
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
    /// Logs the run's counters to <paramref name="output"/> the moment the build finishes — the
    /// only point they can be read from a <c>dotnet test</c> transcript, and a paid run should see
    /// them before it spends anything on answers.
    /// <para>
    /// <b><see cref="RaptorRun.CorpusRebuildCount"/> is logged but is not a gate.</b> It is set to
    /// a literal beside the single <c>RebuildAsync</c> call, and under <c>Corpus</c> scope
    /// ingestion is structurally incapable of building a tree, so it reads 1 unconditionally and
    /// cannot detect anything. The counters that can actually move are <c>LeafCount</c> (which
    /// pins the corpus that was ingested), <c>SummaryCount</c> and <c>SummariserCalls</c>.
    /// </para>
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
    /// answer — so the caller's token snapshot, taken right after this returns from the one client
    /// every summariser and every answer call shares, is exactly the summarisation cost: nothing an
    /// answer call adds can have landed in <paramref name="answering"/>'s counters yet. Logged only
    /// when a <see cref="RaptorRun"/> was actually built (either scope), so a run with no RAPTOR arm
    /// selected does not print zeroes into the transcript.
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

        return run;
    }

    /// <summary>
    /// Writes both <see cref="RaptorRun"/>s' counters to a JSON file beside the answers dump.
    /// </summary>
    /// <remarks>
    /// <see cref="LogRaptorRunCounters"/> already writes these to <see cref="ITestOutputHelper"/>,
    /// and that is not enough. This project runs xunit v3 through Microsoft.Testing.Platform, which
    /// does not surface a <b>passing</b> test's output — so on the run that matters, the run that
    /// succeeded, the counters were invisible. Task 4 Step 4 of the RAPTOR plan asks for
    /// <c>LeafCount</c>, <c>SummaryCount</c> and <c>SummariserCalls</c> as deliverables, and
    /// Task 5 derives the full sweep's cost from <c>SummariserCalls</c>; neither could be done
    /// from a green run. The per-query rows already survive this way — see <see cref="DumpAnswers"/>
    /// — and the counters now do too.
    /// <para>
    /// The level shapes go in as well. <c>LeafCount</c> says the tree was built; only the per-level
    /// largest-cluster and imbalance figures say how close it came to not building, which is what
    /// #345's average-not-maximum floor left to measurement.
    /// </para>
    /// </remarks>
    /// <param name="cacheDirectory">The answer cache's parent, receiving the <c>-results</c> subdirectory.</param>
    /// <param name="pilot">Whether this was a bounded pilot, mirroring <see cref="DumpAnswers"/>'s naming.</param>
    /// <param name="runs">Label/run pairs; a null run is skipped, so an unselected scope writes nothing.</param>
    /// <returns>The path written.</returns>
    private static string DumpRaptorCounters(
        string cacheDirectory, bool pilot, params (string Label, RaptorRun? Run)[] runs)
    {
        var directory = Path.Combine(cacheDirectory, AnswersDirectoryName + "-results");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            (pilot ? "pilot-" : "full-") + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".counters.json");

        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var (label, run) in runs)
        {
            if (run is null)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("scope", label);
            writer.WriteNumber("leafCount", run.LeafCount);
            writer.WriteNumber("summaryCount", run.SummaryCount);
            writer.WriteNumber("corpusRebuildCount", run.CorpusRebuildCount);
            writer.WriteNumber("summariserCalls", run.SummariserCalls);
            writer.WriteStartArray("levels");
            foreach (var level in run.Levels)
            {
                writer.WriteStartObject();
                writer.WriteNumber("level", level.Level);
                writer.WriteNumber("chunks", level.ChunkCount);
                writer.WriteNumber("clusters", level.ClusterCount);
                writer.WriteNumber("largestCluster", level.MaxClusterSize);
                writer.WriteBoolean("maxClustersOverridden", level.MaxClustersOverridden);
                writer.WriteBoolean("degenerate", level.Degenerate);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        return path;
    }

    /// <summary>Writes one <see cref="RaptorRun"/>'s counters to <paramref name="output"/>, labelled.</summary>
    private static void LogRaptorRunCounters(ITestOutputHelper output, string label, RaptorRun run)
    {
        output.WriteLine(FormattableString.Invariant(
            $"RaptorRun[{label}]: LeafCount={run.LeafCount}, SummaryCount={run.SummaryCount}, CorpusRebuildCount={run.CorpusRebuildCount}, SummariserCalls={run.SummariserCalls}"));

        // Without this the run reports that the tree built and nothing about how close it came to
        // not building. #345's floor bounds the mean cluster size and not the maximum, so the
        // margin is exactly the imbalance figure below; the design left "is the mean enough?" to
        // this measurement.
        var levels = run.Levels;
        if (levels.Count == 0)
        {
            output.WriteLine(FormattableString.Invariant(
                $"RaptorRun[{label}]: no summarise spans captured — no level was clustered."));
            return;
        }

        foreach (var level in levels)
        {
            var flags = string.Concat(
                level.MaxClustersOverridden ? " MAXCLUSTERS-OVERRIDDEN" : string.Empty,
                level.Degenerate ? " DEGENERATE" : string.Empty);

            var mean = level.ClusterCount <= 0 ? 0 : (double)level.ChunkCount / level.ClusterCount;

            output.WriteLine(FormattableString.Invariant(
                $"RaptorRun[{label}]: level {level.Level}: {level.ChunkCount} chunks -> {level.ClusterCount} clusters, largest {level.MaxClusterSize}, mean {mean:F1}, imbalance {level.Imbalance:F2}x{flags}"));
        }

        var worst = levels.MaxBy(l => l.Imbalance)!;
        output.WriteLine(FormattableString.Invariant(
            $"RaptorRun[{label}]: worst imbalance {worst.Imbalance:F2}x at level {worst.Level} (largest cluster {worst.MaxClusterSize} chunks)"));
    }

    /// <summary>
    /// Logs the cumulative RAPTOR summarisation cost, but only when at least one
    /// <see cref="RaptorRun"/> was actually built — otherwise a run with no RAPTOR arm selected
    /// would print zeroes into every transcript for a cost nothing paid.
    /// </summary>
    private static void LogRaptorSummarisationCostSoFar(
        ITestOutputHelper output, CachedGraphRagClient answering, RaptorRun? corpusRun, RaptorRun? perDocumentRun)
    {
        if (corpusRun is null && perDocumentRun is null)
        {
            return;
        }

        output.WriteLine(FormattableString.Invariant(
            $"RAPTOR summarisation cost so far (every tree built, before any answer is generated): {answering.Calls} calls, {answering.InputTokens} input tokens, {answering.OutputTokens} output tokens"));
    }

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

    /// <summary>
    /// Fails a run whose query selection is empty, naming the smallest bound that would not be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An empty selection used to pass.</b> Nothing was scored, every metric printed
    /// <c>NaN</c>, the answers sidecar was written at zero bytes, and
    /// <see cref="AssertEveryArmAnsweredEveryQuery"/> is vacuously true over no queries — "every
    /// arm answered every query" holds when there are none. Exit code 0. #360.
    /// </para>
    /// <para>
    /// It is checked here, at the selection site, rather than after the arms run: everything
    /// expensive — the generator, the graph store, both RAPTOR trees — is built below this line,
    /// and a corpus tree costs hours and real money. A configuration error should cost seconds.
    /// </para>
    /// <para>
    /// The advice is derived, not hardcoded. A stratum receives at least one query when
    /// <c>round(bound × count / total) >= 1</c>, i.e. when <c>bound >= 0.5 × total / count</c>, so
    /// the <b>rarest</b> type binds. On MultiHop-RAG that is <c>null_query</c> at 301 of 2,556,
    /// giving 4.25 and therefore 5 — but computing it keeps the message true if the mix ever
    /// changes, and a hardcoded 5 would quietly become wrong on another dataset.
    /// </para>
    /// <para>
    /// <b>Flooring each stratum's quota at 1 was considered and rejected.</b> With four types it
    /// would make <see cref="MaxQueriesVariable"/> select four queries when asked for one — a knob
    /// named MAX returning more than its maximum. An option that does not do what it says is the
    /// defect class this phase exists to close; fixing #360 by minting a smaller instance of it
    /// would be a poor trade.
    /// </para>
    /// </remarks>
    /// <param name="selection">The selection just computed by <see cref="SelectQueries"/>.</param>
    /// <param name="gold">The gold answers, read for the type distribution the advice needs.</param>
    private static void AssertSelectionIsNotEmpty(
        QuerySelection selection, IReadOnlyDictionary<string, MultiHopRagAnswer> gold)
    {
        if (selection.Count > 0)
        {
            return;
        }

        var smallest = SmallestBoundCoveringEveryType(gold);
        Assert.Fail(FormattableString.Invariant(
            $"The query selection is empty, so this run would score nothing and report NaN for every metric while passing. Set {MaxQueriesVariable} to at least {smallest}, or unset it to run every query."));
    }

    /// <summary>
    /// The smallest <see cref="MaxQueriesVariable"/> value whose proportional quotas give every
    /// question type at least one query.
    /// </summary>
    /// <remarks>
    /// <see cref="SelectQueries"/> rounds <c>bound × count / total</c> away from zero, so a type
    /// clears 1 once that product reaches 0.5. Solving for the rarest type gives the bound below.
    /// Returns 1 for an empty or single-type gold set, where no bound can round anything to zero.
    /// </remarks>
    /// <param name="gold">The gold answers whose type distribution sets the constraint.</param>
    /// <returns>The smallest workable bound, never less than 1.</returns>
    private static int SmallestBoundCoveringEveryType(IReadOnlyDictionary<string, MultiHopRagAnswer> gold)
    {
        if (gold.Count == 0)
        {
            return 1;
        }

        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var answer in gold.Values)
        {
            byType[answer.QuestionType] = byType.GetValueOrDefault(answer.QuestionType) + 1;
        }

        var rarest = int.MaxValue;
        foreach (var count in byType.Values)
        {
            rarest = System.Math.Min(rarest, count);
        }

        var bound = (int)System.Math.Ceiling(0.5 * gold.Count / rarest);
        return System.Math.Max(bound, 1);
    }

    /// <summary>
    /// The arms this run selects: every arm named explicitly through <see cref="ArmsVariable"/>, or
    /// — when it is unset — <see cref="AnswerArm.All"/> filtered down to the arms
    /// <see cref="SelectDefaultArms"/> says are actually measured.
    /// </summary>
    private static IReadOnlyList<string> SelectArms(string datasetName, ITestOutputHelper output)
    {
        var value = Environment.GetEnvironmentVariable(ArmsVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return SelectDefaultArms(datasetName, output);
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
    /// <see cref="AnswerArm.All"/>, minus whichever arms <see cref="MultiHopRagAnswerReproduction"/>
    /// has never actually measured — an empty <c>Accuracy</c> array, the state the four RAPTOR arms
    /// are in until Phase 6.2.1's sweep pins them.
    /// </summary>
    /// <remarks>
    /// <b>Only the default selection is filtered here; a name given through <see cref="ArmsVariable"/>
    /// is never touched by this method.</b> Without this, the canonical full run
    /// <c>docs/reference/ci.md</c> documents — no <see cref="ArmsVariable"/> set — hits
    /// <c>BuildCorpusRaptorRunAsync</c> for an unmeasured RAPTOR arm: in replay mode the answering
    /// client is <c>inner: null</c> with refuse-on-miss and throws on the first summarisation, before
    /// any of the arms this run could actually check ever gets there. Gating on whether the run is
    /// generating would only fix that half — with <see cref="GenerateVariable"/> set the same default
    /// run would instead pay to build the corpus tree and throw on #345's context-length error. This
    /// self-heals the moment an arm's pin stops being empty, and an operator naming the arm through
    /// <see cref="ArmsVariable"/> — Task 4's pilot, for one — still reaches it, unmeasured or not.
    /// </remarks>
    private static IReadOnlyList<string> SelectDefaultArms(string datasetName, ITestOutputHelper output)
    {
        var selected = new List<string>(AnswerArm.All.Count);
        var skipped = new List<string>();
        foreach (var arm in AnswerArm.All)
        {
            if (MultiHopRagAnswerReproduction.HasRecordedFigure(datasetName, arm))
            {
                selected.Add(arm);
            }
            else
            {
                skipped.Add(arm);
            }
        }

        if (skipped.Count > 0)
        {
            output.WriteLine(FormattableString.Invariant(
                $"skipping {skipped.Count} unmeasured arm(s) from the default selection: {string.Join(", ", skipped)} -- no recorded figure yet in {nameof(MultiHopRagAnswerReproduction)}. Name an arm explicitly via {ArmsVariable} to run it anyway."));
        }

        return selected;
    }

    /// <summary>
    /// The trap #360 reports: over MultiHop-RAG's type mix, a bound of 1 selects <b>nothing</b>.
    /// </summary>
    /// <remarks>
    /// Proportional stratification rounds every quota to zero at <c>bound = 1</c> — 816 inference,
    /// 856 comparison, 583 temporal and 301 null over 2,556 gold answers each round to 0 — so the
    /// smallest value the knob accepts is the one value that produces no data. Pinned rather than
    /// left as folklore, because the arithmetic is what makes the guard below necessary and a
    /// future change to the mix should have to notice it.
    /// </remarks>
    [Fact]
    public void SelectQueries_BoundTooSmallForEveryStratum_SelectsNothing()
    {
        var previous = Environment.GetEnvironmentVariable(MaxQueriesVariable);
        try
        {
            Environment.SetEnvironmentVariable(MaxQueriesVariable, "1");
            var (dataset, gold) = MultiHopRagShapedSelectionFixture();

            var selection = SelectQueries(dataset, gold);

            Assert.Empty(selection.Queries);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxQueriesVariable, previous);
        }
    }

    /// <summary>
    /// An empty selection fails, and the failure names the smallest bound that would work.
    /// </summary>
    /// <remarks>
    /// A pilot that selects nothing scores nothing, prints <c>NaN</c> for every metric, writes a
    /// zero-byte sidecar and — before this guard — passed. A pilot is what decides whether the full
    /// sweep is worth paying for, so one that reports success having measured nothing spends the
    /// operator's confidence rather than their money. The bound is computed from the gold
    /// distribution rather than hardcoded, so the advice stays true if the mix changes.
    /// </remarks>
    [Fact]
    public void AssertSelectionIsNotEmpty_EmptySelection_FailsNamingTheSmallestWorkableBound()
    {
        var (_, gold) = MultiHopRagShapedSelectionFixture();
        var empty = new QuerySelection([], JudgedCount: 0, IsPilot: true);

        var error = Assert.ThrowsAny<Exception>(
            () => AssertSelectionIsNotEmpty(empty, gold));

        // The rarest type binds: round(bound * 301/2556) >= 1 needs bound >= 4.25, so 5.
        Assert.Contains("5", error.Message, StringComparison.Ordinal);
        Assert.Contains(MaxQueriesVariable, error.Message, StringComparison.Ordinal);
    }

    /// <summary>A selection that found queries passes the guard untouched.</summary>
    [Fact]
    public void AssertSelectionIsNotEmpty_NonEmptySelection_DoesNotThrow()
    {
        var (dataset, gold) = MultiHopRagShapedSelectionFixture();
        var selection = new QuerySelection(dataset.Queries, dataset.Queries.Count, IsPilot: true);

        AssertSelectionIsNotEmpty(selection, gold);
    }

    /// <summary>
    /// A dataset and gold set carrying MultiHop-RAG's real type proportions at 1/100th scale:
    /// 8 inference, 9 comparison, 6 temporal, 3 null. The ratios are what the quota arithmetic
    /// reads, so the scaled fixture reproduces the full corpus's rounding behaviour without
    /// needing the corpus.
    /// </summary>
    private static (BeirDataset Dataset, IReadOnlyDictionary<string, MultiHopRagAnswer> Gold)
        MultiHopRagShapedSelectionFixture()
    {
        var counts = new (string Type, int Count)[]
        {
            (MultiHopRagAnswers.InferenceType, 8),
            (MultiHopRagAnswers.ComparisonType, 9),
            (MultiHopRagAnswers.TemporalType, 6),
            (MultiHopRagAnswers.NullType, 3),
        };

        var queries = new List<BeirQuery>();
        var gold = new Dictionary<string, MultiHopRagAnswer>(StringComparer.Ordinal);
        foreach (var (type, count) in counts)
        {
            for (var i = 0; i < count; i++)
            {
                var id = FormattableString.Invariant($"{type}-{i}");
                queries.Add(new BeirQuery(id, "question " + id));
                gold[id] = new MultiHopRagAnswer(id, "answer", type);
            }
        }

        var dataset = new BeirDataset(
            "multihop-rag", "test", [], queries,
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal));

        return (dataset, gold);
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
                Assert.Equal(new[] { arm }, SelectArms("multihop-rag", _output));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ArmsVariable, previous);
            }
        }
    }

    /// <summary>
    /// C1's fix: the default selection (<see cref="ArmsVariable"/> unset) must skip every arm
    /// <see cref="MultiHopRagAnswerReproduction"/> has not actually measured yet — today, the four
    /// RAPTOR arms, each pinned with an empty figure array — while every measured arm stays in, and
    /// the run says out loud what it skipped and why.
    /// </summary>
    [Fact]
    public void SelectArms_DefaultSelection_ContainsOnlyArmsWithARecordedFigure()
    {
        var previous = Environment.GetEnvironmentVariable(ArmsVariable);
        try
        {
            Environment.SetEnvironmentVariable(ArmsVariable, null);
            var output = new CapturingTestOutputHelper();

            var arms = SelectArms("multihop-rag", output);

            foreach (var arm in arms)
            {
                Assert.True(
                    MultiHopRagAnswerReproduction.HasRecordedFigure("multihop-rag", arm),
                    $"the default selection included '{arm}', which has no recorded figure.");
            }

            // Every arm is measured as of Task 5 (2026-08-25), so the filter has nothing to remove
            // and the default selection is all of them. This assertion is what makes the state
            // explicit rather than incidental: adding an arm without pinning a figure — or with an
            // empty one — drops it out of the default silently, and this fails when that happens.
            Assert.Equal(AnswerArm.All.OrderBy(a => a, StringComparer.Ordinal),
                arms.OrderBy(a => a, StringComparer.Ordinal));

            // The four RAPTOR arms were the unmeasured ones this test was written around; they are
            // pinned now, and are asserted here so their removal from the default would be caught.
            foreach (var raptorArm in new[] { AnswerArm.RaptorCorpus, AnswerArm.Raptor, AnswerArm.RaptorFiltered, AnswerArm.RaptorBoost })
            {
                Assert.True(
                    MultiHopRagAnswerReproduction.HasRecordedFigure("multihop-rag", raptorArm),
                    $"{raptorArm} lost its recorded figure; Task 5 pinned all four.");
                Assert.Contains(raptorArm, arms, StringComparer.Ordinal);
            }

            // A measured arm is never filtered out of the default.
            Assert.Contains(AnswerArm.Dense, arms, StringComparer.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArmsVariable, previous);
        }
    }

    /// <summary>
    /// C1's other half: naming an unmeasured arm explicitly through <see cref="ArmsVariable"/> must
    /// still select it. The default filter in <see cref="SelectDefaultArms"/> must never reach an
    /// explicit selection — this is how Task 4's pilot runs before anything is pinned.
    /// </summary>
    [Fact]
    public void SelectArms_ExplicitSelection_IsPassedThroughUntouched()
    {
        var previous = Environment.GetEnvironmentVariable(ArmsVariable);
        try
        {
            // RaptorCorpus is measured now, so this no longer demonstrates "explicit beats the
            // filter" the way it did before Task 5. It still pins the other half of the contract:
            // an explicit selection is passed through untouched, one arm in and one arm out.
            Environment.SetEnvironmentVariable(ArmsVariable, AnswerArm.RaptorCorpus);
            var output = new CapturingTestOutputHelper();

            var arms = SelectArms("multihop-rag", output);

            Assert.Equal(new[] { AnswerArm.RaptorCorpus }, arms);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArmsVariable, previous);
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
    /// Warns loudly, but does not throw, whenever fewer than <see cref="ContextChunks"/> chunks
    /// survive — whatever the cause: a corpus too small to hold that many non-summary candidates
    /// at all, or an over-fetch multiplier that turns out too small once summaries crowd the top
    /// of the ranking for a given query. The check does not also require proving more candidates
    /// existed somewhere — that would exclude the exact under-fill case it exists to catch. Task
    /// 4's validation gate (<c>raptorfiltered − dense</c> ≈ 0) is only trustworthy if this would
    /// have been seen firing were it going to fire.
    /// </remarks>
    private static async Task<IReadOnlyList<SearchResult>> RetrieveRaptorFilteredAsync(
        string query, RaptorRun corpusRun, ITestOutputHelper output, CancellationToken ct)
    {
        var pool = await corpusRun.SearchAsync(
            query, RaptorRetrievalMode.Blend, ContextChunks * RaptorFilteredOverFetchMultiplier, ct);
        var survivors = pool.Where(r => !r.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        var filtered = Head(survivors, ContextChunks);

        if (filtered.Count < ContextChunks)
        {
            output.WriteLine(FormattableString.Invariant(
                $"WARNING: raptorfiltered under-filled for query '{query}': only {filtered.Count} of {ContextChunks} chunks came back — {survivors.Count} non-summary candidates were available out of an over-fetched pool of {pool.Count} — check the over-fetch multiplier and the Head() wiring."));
        }

        return filtered;
    }

    /// <summary>
    /// Proves the guard in <see cref="RetrieveRaptorFilteredAsync"/> can actually fire, rather than
    /// resting on the claim that it can: a one-chunk corpus cannot possibly hand back six
    /// non-summary candidates, so the guard must warn; a sixty-leaf corpus comfortably can, so it
    /// must stay silent. No model, no downloaded corpus — a fake chat client and a fake embedder,
    /// the same shape <c>RaptorRunTests</c> uses.
    /// </summary>
    [Fact]
    public async Task RetrieveRaptorFilteredAsync_WarnsOnUnderFill_AndStaysSilentWhenFull()
    {
        var ct = TestContext.Current.CancellationToken;
        var cacheRoot = Path.Combine(Path.GetTempPath(), "ragnet-raptorfiltered-guard-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(cacheRoot);

        try
        {
            var chatClient = new EchoRaptorSummaryChatClient();

            var sparseCache = new EmbeddingCache(cacheRoot, "raptorfiltered-guard-sparse@fake");
            await using (var sparse = await RaptorRun.BuildAsync(
                FakeRaptorFilteredCorpus(documentCount: 1, chunksPerDocument: 1),
                RaptorTreeScope.Corpus, new RandomVectorEmbedder(new Random(Seed: 4242)), sparseCache, chatClient,
                ":memory:", ct))
            {
                var loud = new CapturingTestOutputHelper();
                var filtered = await RetrieveRaptorFilteredAsync("does the guard fire", sparse, loud, ct);

                Assert.True(filtered.Count < ContextChunks, "a one-chunk corpus must under-fill by construction");
                Assert.Contains(
                    loud.Lines, line => line.Contains("WARNING: raptorfiltered under-filled", StringComparison.Ordinal));
            }

            var ampleCache = new EmbeddingCache(cacheRoot, "raptorfiltered-guard-ample@fake");
            await using (var ample = await RaptorRun.BuildAsync(
                FakeRaptorFilteredCorpus(documentCount: 15, chunksPerDocument: 4),
                RaptorTreeScope.Corpus, new RandomVectorEmbedder(new Random(Seed: 24242)), ampleCache, chatClient,
                ":memory:", ct))
            {
                var silent = new CapturingTestOutputHelper();
                var filtered = await RetrieveRaptorFilteredAsync("does the guard stay silent", ample, silent, ct);

                Assert.Equal(ContextChunks, filtered.Count);
                Assert.Empty(silent.Lines);
            }
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// <paramref name="documentCount"/> fake documents of <paramref name="chunksPerDocument"/>
    /// paragraphs apiece, padded so <c>RecursiveChunkingStrategy</c> yields exactly one chunk per
    /// paragraph — the same padding reasoning <c>RaptorRunTests.FillerLength</c> documents.
    /// </summary>
    private static IReadOnlyList<BeirDocument> FakeRaptorFilteredCorpus(int documentCount, int chunksPerDocument)
    {
        var documents = new List<BeirDocument>(documentCount);
        for (var i = 0; i < documentCount; i++)
        {
            var text = BuildFakeRaptorFilteredDocumentText(i, chunksPerDocument);
            documents.Add(new BeirDocument(
                Id: FormattableString.Invariant($"guard-doc-{i}"), Title: string.Empty, Text: text, RetrievalText: text));
        }

        return documents;
    }

    private static string BuildFakeRaptorFilteredDocumentText(int documentIndex, int chunksPerDocument)
    {
        var paragraphs = new string[chunksPerDocument];
        var filler = new string('x', 400);
        for (var i = 0; i < chunksPerDocument; i++)
        {
            paragraphs[i] = FormattableString.Invariant($"guard-doc{documentIndex} paragraph{i} {filler}");
        }

        return string.Join("\n\n", paragraphs);
    }

    /// <summary>
    /// An embedder that returns an independent random vector per text, ignoring the text itself —
    /// sufficient here because the guard test needs only "leaves vastly outnumber summaries in the
    /// corpus", not any real semantic structure.
    /// </summary>
    private sealed class RandomVectorEmbedder(Random rng) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int Dimensions = 8;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);

            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var unusedText in values)
            {
                _ = unusedText;
                generated.Add(new Embedding<float>(RandomVector(rng)));
            }

            return Task.FromResult(generated);
        }

        /// <summary>A fresh independent random vector, ignoring the text it stands in for.</summary>
        private static float[] RandomVector(Random rng) =>
            Enumerable.Range(0, Dimensions).Select(_ => (float)rng.NextDouble()).ToArray();

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>A chat client that echoes its prompt back as the RAPTOR cluster "summary".</summary>
    private sealed class EchoRaptorSummaryChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var text = string.Join(" ", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("RaptorIngestionBehavior does not stream summaries.");

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>Captures every line written to it, so a test can assert on what a guard logged.</summary>
    private sealed class CapturingTestOutputHelper : ITestOutputHelper
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public string Output => string.Join(Environment.NewLine, _lines);

        public void Write(string message) => _lines.Add(message);

        public void Write(string format, params object[] args) =>
            _lines.Add(string.Format(CultureInfo.InvariantCulture, format, args));

        public void WriteLine(string message) => _lines.Add(message);

        public void WriteLine(string format, params object[] args) =>
            _lines.Add(string.Format(CultureInfo.InvariantCulture, format, args));
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
