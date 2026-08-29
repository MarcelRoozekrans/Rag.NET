using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Raptor;
using Rag.NET.Storage;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Phase 5.2.2: does GraphRAG help <b>answers</b>? Three retrieval arms answer MultiHop-RAG's
/// queries with one model, one prompt and top-6 context, and every answer is scored against the
/// dataset's gold answer by the dataset authors' own rule — the currency the paper reports in,
/// which the retrieval measurements of 5.2 could not see.
/// <para>
/// <b>Arms.</b> <c>dense</c>: the Real leg's article chunks alone, dense top-6. <c>local</c>:
/// the graph run's store, dense top-500 through <c>LegacyPageRankLocalSearch</c> — the frozen copy
/// of the behaviour that shipped under the name <c>GraphLocalSearchBehavior</c> and was deleted
/// from the package; the code now lives in this measurement harness — at
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
        using var answering = OpenAnsweringClient(cacheDirectory, out var generating, out var answeringModel);
        using var engineClients = new EngineArmClients(
            arms, answering.Cache, answeringModel, GraphExtractionModelIdentity.ExtractionTemperature);

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
            selection, arms, run, articles, corpusRun, perDocumentRun, generator, embeddings, answering,
            engineClients, gold, ct);
        _output.WriteLine(DescribeResults(
            descriptor, selection, tallies, answering, engineClients, Stopwatch.GetElapsedTime(startedAt)));
        _output.WriteLine("every scored answer: " + DumpAnswers(cacheDirectory, tallies, selection.IsPilot));
        _output.WriteLine("RaptorRun counters: " + DumpRaptorCounters(
            cacheDirectory, selection.IsPilot, ("corpus", corpusRun), ("per-document", perDocumentRun)));

        AssertEveryArmAnsweredEveryQuery(tallies, selection);
        AssertPinnedFiguresReproduce(descriptor, selection, tallies);
    }

    /// <summary>
    /// The pinned-figure check, split out of the theory (MA0051): a pilot pins nothing and says so,
    /// a full run holds every arm's paper-rule accuracy to
    /// <see cref="MultiHopRagAnswerReproduction"/>.
    /// </summary>
    private void AssertPinnedFiguresReproduce(
        BeirDatasetDescriptor descriptor, QuerySelection selection, Dictionary<string, ArmTally> tallies)
    {
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
    /// <remarks>
    /// <b>Reads <paramref name="answering"/> alone, and is still the whole figure.</b> Every counter
    /// on <c>CachedGraphRagClient</c> is per instance, so since the engine arms got clients of their
    /// own (<see cref="EngineArmClients"/>) most whole-run figures have to be summed across those
    /// too. Not this one: both <see cref="RaptorRun"/>s summarise through this client and nothing
    /// else, and this is called before <see cref="AnswerAllAsync"/>, so the engine clients have made
    /// no call yet and have nothing to contribute.
    /// </remarks>
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
    /// <see cref="MultiHopRagAnswerReproduction"/> has not actually measured yet — the four RAPTOR
    /// arms were the original case, pinned with an empty figure array until Task 5 measured them;
    /// the five answer-engine arms added afterward are the same situation again, wired up and
    /// pinned empty, deliberately left out of the default because they cost real API calls and
    /// have no figure yet — while every measured arm stays in, and the run says out loud what it
    /// skipped and why.
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

            // The four RAPTOR arms were measured as of Task 5 (2026-08-25); the five engine arms
            // (chatengine, mapreduce, refine, flare, flarefixed) were added after that with empty
            // figure arrays and are not measured yet, so the default selection is AnswerArm.All
            // minus those five — not all of AnswerArm.All the way it was before they existed. This
            // assertion is what makes the state explicit rather than incidental: adding an arm
            // without pinning a figure — or with an empty one — drops it out of the default
            // silently, and this fails when that happens.
            Assert.Equal(
                AnswerArm.All.Except(UnmeasuredEngineArms, StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal),
                arms.OrderBy(a => a, StringComparer.Ordinal));

            AssertEngineArmsStayUnmeasuredAndExcluded(arms);

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
    /// The five answer-engine arms Task 1 named and pinned with an empty figure array — wired up,
    /// unmeasured, and deliberately excluded from the default arm selection because they cost real
    /// API calls and have no recorded figure to check a re-measurement against.
    /// </summary>
    private static readonly string[] UnmeasuredEngineArms =
        [AnswerArm.ChatEngine, AnswerArm.MapReduce, AnswerArm.Refine, AnswerArm.Flare, AnswerArm.FlareFixed];

    /// <summary>
    /// Asserts the five answer-engine arms stay unmeasured and excluded from <paramref name="arms"/>
    /// together: if one gets a real figure pinned without <see cref="UnmeasuredEngineArms"/> being
    /// updated to match, this fails and points back here instead of the default selection's shape
    /// changing silently.
    /// </summary>
    private static void AssertEngineArmsStayUnmeasuredAndExcluded(IReadOnlyList<string> arms)
    {
        foreach (var engineArm in UnmeasuredEngineArms)
        {
            Assert.False(
                MultiHopRagAnswerReproduction.HasRecordedFigure("multihop-rag", engineArm),
                $"{engineArm} now has a recorded figure; update UnmeasuredEngineArms and this " +
                "test's expectations rather than leaving it excluded from the default by accident.");
            Assert.DoesNotContain(engineArm, arms, StringComparer.Ordinal);
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
    /// <param name="cacheDirectory">The cache root the answer directory hangs off.</param>
    /// <param name="generating">Whether <see cref="GenerateVariable"/> put this run in fill mode.</param>
    /// <param name="model">
    /// The live model the returned client fills misses from, or <see langword="null"/> on a replay
    /// run. Handed back so <see cref="EngineArmClients"/> can build one sibling
    /// <see cref="CachedGraphRagClient"/> per engine arm over the <b>same</b> cache and the
    /// <b>same</b> model — which is what makes each engine arm's <c>Calls</c>, <c>InputTokens</c>
    /// and <c>OutputTokens</c> that arm's alone rather than a share of one global counter.
    /// <para>
    /// The returned client owns and disposes it; the sibling clients borrow it (see
    /// <see cref="BorrowedChatClient"/>), so nothing disposes it twice and the pinned non-engine
    /// path keeps the exact client, cache and temperature it had before.
    /// </para>
    /// </param>
    private static CachedGraphRagClient OpenAnsweringClient(
        string cacheDirectory, out bool generating, out IChatClient? model)
    {
        generating = IsOn(GenerateVariable);
        var identity = GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature);
        var cache = new GraphExtractionCache(
            cacheDirectory, identity,
            generating ? GraphExtractionCacheMode.Fill : GraphExtractionCacheMode.RefuseOnMiss,
            AnswersDirectoryName);

        if (!generating)
        {
            model = null;
            return new CachedGraphRagClient(cache, inner: null, GraphExtractionModelIdentity.ExtractionTemperature);
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(apiKey),
            $"{GenerateVariable} is set but {ApiKeyVariable} is not; nothing can be generated without a key.");

        model = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
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

    /// <summary>
    /// The <c>AddRagNet</c> container <see cref="AnswerArm.Flare"/>'s lookahead retriever is
    /// resolved from — or <see langword="null"/> when <c>flare</c> is not selected, the same economy
    /// the <see cref="RaptorRun"/> builds get.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built the way <see cref="PipelineParity.RetrieveThroughPipelineAsync"/> builds it, and that
    /// is the whole point: <see cref="PipelineParityTests"/> holds a default <c>AddRagNet</c>
    /// container over a shared store to this harness's own dense row and proves the two return
    /// identical rankings. So <c>flare</c>'s mid-generation lookahead reads the <b>same corpus,
    /// through the same rankings</b>, that <c>flare − flarefixed</c> is differenced over. A
    /// hand-rolled adapter would retrieve from somewhere whose equivalence to the harness nothing
    /// asserts, and the arm's one claim would rest on that.
    /// </para>
    /// <para>
    /// <paramref name="articles"/> is handed over as an instance and shared by identity — the same
    /// store the dense arm searches, not a rebuild of it. Microsoft DI does not dispose instances it
    /// did not create, so the caller's <c>using</c> on the store stays the only disposal.
    /// <see cref="CachingEmbeddingGenerator"/> is what puts the pipeline on the harness's cached
    /// vectors rather than live ONNX output, for the reason
    /// <see cref="PipelineParityTests.DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnSciFact"/>
    /// gives.
    /// </para>
    /// <para>
    /// <b>One difference from <see cref="PipelineParity"/>, deliberately.</b> That type builds a
    /// fresh container per call so a warm <c>ResultCacheBehavior</c> or <c>EmbeddingCacheBehavior</c>
    /// cannot make a re-run agree where a first run did not; this one is built once and shared by
    /// every query. The difference is inert here because neither behaviour has anything to warm:
    /// both take <c>HybridCache</c> and <c>CachingOptions</c> as optional injections and return
    /// <c>next(...)</c> untouched when either is <see langword="null"/>, and the container below
    /// registers neither. Register a cache here and that reasoning lapses — the shared container
    /// would then carry state across queries and <c>flare</c>'s lookahead would stop reading the
    /// live store.
    /// </para>
    /// </remarks>
    private static ServiceProvider? BuildEnginePipeline(
        IReadOnlyList<string> arms,
        InMemoryVectorStore articles,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings)
    {
        // Only flare needs this. flarefixed's retriever is EngineRetrievers' stub and never comes
        // from here, so an unselected flare costs nothing whether or not flarefixed is running.
        if (!arms.Contains(AnswerArm.Flare, StringComparer.Ordinal))
        {
            return null;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore>(articles);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new CachingEmbeddingGenerator(generator, embeddings));
        services.AddRagNet();

        return services.BuildServiceProvider();
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
        EngineArmClients engineClients,
        IReadOnlyDictionary<string, MultiHopRagAnswer> gold,
        CancellationToken ct)
    {
        var tallies = arms.ToDictionary(a => a, _ => new ArmTally(), StringComparer.Ordinal);
        var startedAt = Stopwatch.GetTimestamp();

        await EmbedEveryQueryAsync(selection, generator, embeddings, ct);

        // Local search retrieval, sequentially, for every query that needs it.
        var graphContexts = await CollectGraphStoreContextsIfNeededAsync(run, selection, arms, startedAt, ct);

        using var enginePipeline = BuildEnginePipeline(arms, articles, generator, embeddings);
        var retrievers = new EngineRetrievers(enginePipeline?.GetRequiredService<IRetriever>());
        var failures = new AnswerEngineArms.FailureLog();
        var pass = new AnswerPass(
            arms, AnyEngineArm(arms), run, articles, corpusRun, perDocumentRun, generator, embeddings,
            answering, engineClients, retrievers, failures, graphContexts, gold, tallies);

        // Dense and global retrieval, and every answer, in parallel under the same bound.
        var done = 0;
        await Parallel.ForEachAsync(
            selection.Queries,
            new ParallelOptions { MaxDegreeOfParallelism = AnswerConcurrency, CancellationToken = ct },
            async (query, token) =>
            {
                await AnswerOneQueryAsync(query, pass, token);
                ReportProgress(Interlocked.Increment(ref done), selection, arms, startedAt, answering);
            });

        RunEngineGatesAndReportCosts(arms, selection.Count, engineClients, retrievers, failures, answering, tallies);
        return tallies;
    }

    /// <summary>
    /// The three post-answering gates, and the cost block that is written whether they pass or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every counter read here is read after <c>Parallel.ForEachAsync</c> has completed, so every
    /// write to it happens-before the read.
    /// </para>
    /// <para>
    /// <b>The cost block is written in a <c>finally</c>.</b> A failing gate is exactly the run whose
    /// counters are worth reading — a paid pilot that spent real money and then tripped a gate used
    /// to print no cost table at all, because the assert threw before the write and
    /// <see cref="DescribeResults"/> is in the caller. The gate still fails the test; it no longer
    /// takes the evidence with it.
    /// </para>
    /// </remarks>
    private void RunEngineGatesAndReportCosts(
        IReadOnlyList<string> arms,
        int queries,
        EngineArmClients engineClients,
        EngineRetrievers retrievers,
        AnswerEngineArms.FailureLog failures,
        CachedGraphRagClient answering,
        Dictionary<string, ArmTally> tallies)
    {
        try
        {
            retrievers.AssertLookaheadStayedOff();
            retrievers.AssertLookaheadFired(arms, queries, _output);
            failures.AssertNoExceptionWasSwallowed();
        }
        finally
        {
            var costs = DescribeEngineArmCosts(arms, engineClients, retrievers, failures, answering, tallies);
            if (costs.Length > 0)
            {
                _output.WriteLine(costs);
            }
        }
    }

    /// <summary>Whether any selected arm generates through an <see cref="IAnswerEngine"/>.</summary>
    private static bool AnyEngineArm(IReadOnlyList<string> arms)
    {
        for (var i = 0; i < arms.Count; i++)
        {
            if (AnswerEngineArms.IsEngineArm(arms[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One query answered under every selected arm, and the two per-query pilot gates that go with
    /// the engine arms.
    /// </summary>
    /// <remarks>
    /// Extracted from the <c>Parallel.ForEachAsync</c> lambda it used to be (MA0051). The two
    /// branches stay exactly as far apart as they were: <see cref="AnswerThroughPromptAsync"/> is
    /// the pinned <see cref="PromptTemplate"/> path, byte for byte, and
    /// <see cref="AnswerThroughEngineAsync"/> is the engines' own prompts, which are new cache keys.
    /// </remarks>
    private async Task AnswerOneQueryAsync(BeirQuery query, AnswerPass pass, CancellationToken token)
    {
        var expected = pass.Gold[query.Id];
        var denseReference = await RetrieveDenseGateReferenceAsync(query, pass, token);

        for (var i = 0; i < pass.Arms.Count; i++)
        {
            var arm = pass.Arms[i];
            var answerText = AnswerEngineArms.IsEngineArm(arm)
                ? await AnswerThroughEngineAsync(arm, query, denseReference!, pass, token)
                : await AnswerThroughPromptAsync(arm, query, pass, token);

            pass.Tallies[arm].Record(arm, query.Id, expected, answerText);
        }
    }

    /// <summary>
    /// The <c>dense</c> arm's own retrieval for this query, retrieved once and reused as Gate 1's
    /// reference — or <see langword="null"/> when no engine arm is selected and there is nothing to
    /// gate.
    /// </summary>
    /// <remarks>
    /// Taken through <see cref="RetrieveContextAsync"/> with <see cref="AnswerArm.Dense"/> rather
    /// than read off the <c>dense</c> arm's own answer, deliberately: the reference must be what
    /// <b>that switch</b> returns for <c>dense</c>, so an edit moving an engine arm off the shared
    /// <c>case AnswerArm.Dense:</c> body is caught even on a run where <c>dense</c> is not selected
    /// at all — the pilot's arm selection names the engine arms and need not name <c>dense</c>.
    /// <para>
    /// The extra retrieval is one in-memory top-6 over <paramref name="pass"/>'s article store per
    /// query, off a query vector <see cref="EmbedEveryQueryAsync"/> has already cached. No model
    /// call, no store round trip beyond the in-process one, and nothing written.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<SearchResult>?> RetrieveDenseGateReferenceAsync(
        BeirQuery query, AnswerPass pass, CancellationToken token)
    {
        if (!pass.HasEngineArm)
        {
            return null;
        }

        return await RetrieveContextAsync(
            AnswerArm.Dense, query.Text, pass.Run, pass.Articles, pass.CorpusRun, pass.PerDocumentRun,
            pass.Generator, pass.Embeddings, pass.Answering, _output, token);
    }

    /// <summary>
    /// The non-engine arms' answer: the arm's rendered context substituted into
    /// <see cref="PromptTemplate"/> and sent through the shared answering client.
    /// </summary>
    /// <remarks>
    /// <b>Moved verbatim out of the parallel lambda; not one character of the prompt, the ordering
    /// or the client changed.</b> This is the path every pinned <c>dense</c>, <c>global</c> and
    /// RAPTOR figure's cache keys were generated under, and the answer cache is keyed on the
    /// rendered prompt, so any edit here would miss every existing entry.
    /// </remarks>
    private async Task<string> AnswerThroughPromptAsync(
        string arm, BeirQuery query, AnswerPass pass, CancellationToken token)
    {
        var rendered = string.Equals(arm, AnswerArm.LocalSpec, StringComparison.Ordinal)
            ? pass.GraphContexts.LocalSpec[query.Id]
            : RenderContext(arm switch
            {
                AnswerArm.Local => pass.GraphContexts.Local[query.Id],
                AnswerArm.Control => pass.GraphContexts.Control[query.Id],
                AnswerArm.Filtered => pass.GraphContexts.Filtered[query.Id],
                _ => await RetrieveContextAsync(
                    arm, query.Text, pass.Run, pass.Articles, pass.CorpusRun, pass.PerDocumentRun,
                    pass.Generator, pass.Embeddings, pass.Answering, _output, token),
            });

        var prompt = PromptTemplate
            .Replace("{question}", query.Text, StringComparison.Ordinal)
            .Replace("{context}", rendered, StringComparison.Ordinal);

        var response = await pass.Answering.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], cancellationToken: token);
        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// Which <see cref="IRetriever"/> each engine arm gets, and the one assertion that says
    /// <see cref="AnswerArm.FlareFixed"/> held retrieval fixed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This type exists because the guarantee has been wrong three times.</b> The design gave
    /// <c>flarefixed</c> a retriever that throws — but <c>FlareAnswerEngine.TryLookaheadRetrievalAsync</c>
    /// catches and swallows every exception, so the throw proved nothing. That was fixed with
    /// <see cref="AnswerEngineArms.UnreachableRetriever.WasCalled"/>, a flag set before the throw
    /// and therefore immune to the swallow. Then the harness handed <c>flarefixed</c> the same real
    /// retriever it built for <c>flare</c> — so <c>Create</c>'s <c>?? new UnreachableRetriever()</c>
    /// never substituted the stub, and the flag was not merely unread but absent, in precisely the
    /// run that matters: the one where both arms are selected, which is the only run
    /// <c>flare − flarefixed</c> can be computed from.
    /// </para>
    /// <para>
    /// So the whole chain is closed here rather than in one more link of it. The stub is
    /// <b>installed</b> — <see cref="For"/> returns it for <c>flarefixed</c> and never returns the
    /// real retriever for that arm, whatever else is selected. It is <b>one instance for the run</b>,
    /// so a call from any query lands on the object this type holds. And it is <b>readable</b>:
    /// <see cref="AssertLookaheadStayedOff"/> runs after the last answer and fails the test if the
    /// flag is set. Passing <see langword="null"/> instead would reinstate the stub but leave it
    /// unreachable — <c>Create</c> would build a fresh one per call and drop it — which is the same
    /// unobservability in a different place.
    /// </para>
    /// </remarks>
    private sealed class EngineRetrievers
    {
        private readonly AnswerEngineArms.UnreachableRetriever _flareFixed = new();
        private readonly CountingRetriever? _flare;

        /// <param name="flare">
        /// The real pipeline retriever, or <see langword="null"/> when <c>flare</c> is not selected.
        /// <see cref="AnswerEngineArms.Create"/> throws if <c>flare</c> is ever built without one,
        /// so a mis-wiring fails loudly rather than quietly measuring a stub. Wrapped in a
        /// <see cref="CountingRetriever"/> here so Gate 3 can read how often the lookahead fired
        /// from the outside, without inspecting <c>FlareAnswerEngine</c>'s internals.
        /// </param>
        public EngineRetrievers(IRetriever? flare) =>
            _flare = flare is null ? null : new CountingRetriever(flare);

        /// <summary>How many times <c>flare</c>'s lookahead retrieved, across the whole run.</summary>
        public int FlareLookaheads => _flare?.Calls ?? 0;

        /// <summary>The retriever <paramref name="arm"/>'s engine is constructed with.</summary>
        /// <remarks>
        /// The three non-FLARE arms get <see langword="null"/> explicitly rather than the real
        /// retriever they would ignore. <see cref="AnswerEngineArms.Create"/> does ignore it for
        /// them today, so this changes nothing — but it makes "only <c>flare</c> is handed a live
        /// retriever" a property of this method rather than a property of the factory it calls,
        /// which is the sort of one-layer-away reasoning that put the wrong retriever in
        /// <c>flarefixed</c>'s hands to begin with.
        /// </remarks>
        public IRetriever? For(string arm)
        {
            if (string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal))
            {
                return _flareFixed;
            }

            return string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal) ? _flare : null;
        }

        /// <summary><c>flarefixed</c> never retrieved mid-generation.</summary>
        /// <remarks>
        /// Read after <c>Parallel.ForEachAsync</c> has completed, so every write to the flag
        /// happens-before this read. Unconditional: when <c>flarefixed</c> is not selected the stub
        /// is simply never handed out and the flag is trivially clear, which costs nothing and
        /// removes a gate that could itself be wrong.
        /// </remarks>
        public void AssertLookaheadStayedOff() =>
            Assert.False(
                _flareFixed.WasCalled,
                "flarefixed retrieved mid-generation. MaxRetrievals is 0, so this is unreachable " +
                "unless FLARE's lookahead guard changed — the arm is no longer holding retrieval " +
                "fixed and its comparison against mapreduce/refine is invalid.");

        /// <summary>
        /// <b>Gate 3.</b> <c>flare</c>'s lookahead retrieved at least once across the run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this gate exists.</b> <c>SelfAssessmentConfidenceScorer</c> fails open: any error
        /// or unparsable output returns <c>1.0</c>, which is above the <c>0.6</c> threshold, so no
        /// lookahead fires. A run whose scorer calls all failed that way would degrade <c>flare</c>
        /// into <c>flarefixed</c> while still labelling it <c>flare</c>, and
        /// <c>flare − flarefixed ≈ 0</c> could no longer distinguish "the lookahead does nothing"
        /// from "the lookahead never ran" — the two conclusions this arm pair exists to separate.
        /// </para>
        /// <para>
        /// <b>Counted from outside the engine.</b> <see cref="CountingRetriever"/> wraps the
        /// retriever <c>flare</c> is constructed with, so the figure is retrievals the engine
        /// actually performed rather than anything read out of its internals.
        /// </para>
        /// <para>
        /// <b>Applicability.</b> It asserts whenever <c>flare</c> is selected and at least one query
        /// was answered, on a fill run and on a cache replay alike. A replay is not exempt: the
        /// scorer's verdicts are themselves cached on their prompts, so a replay reproduces the
        /// same scores, the same lookahead decisions and the same retrievals as the fill run it
        /// replays — a replay in which the lookahead never fires is a replay of a fill run in which
        /// it never fired, which is exactly the state this gate must not let pass. It is skipped,
        /// out loud, only when there is nothing to observe: <c>flare</c> unselected, or no query
        /// answered.
        /// </para>
        /// </remarks>
        /// <param name="arms">The run's selected arms.</param>
        /// <param name="queries">How many queries the run answered.</param>
        /// <param name="output">Where the skip or the pass is reported.</param>
        public void AssertLookaheadFired(IReadOnlyList<string> arms, int queries, ITestOutputHelper output)
        {
            ArgumentNullException.ThrowIfNull(arms);
            ArgumentNullException.ThrowIfNull(output);

            if (!arms.Contains(AnswerArm.Flare, StringComparer.Ordinal))
            {
                output.WriteLine(
                    "GATE 3 (flare lookahead observed) NOT APPLICABLE: the flare arm was not selected.");
                return;
            }

            if (queries == 0)
            {
                output.WriteLine(
                    "GATE 3 (flare lookahead observed) NOT APPLICABLE: no query was answered.");
                return;
            }

            Assert.True(
                _flare is not null,
                "flare was selected but no pipeline retriever was built for it, so its lookahead " +
                "could not have run at all. BuildEnginePipeline returned null for a selection that " +
                "contains flare.");

            var fired = _flare!.Calls;
            Assert.True(
                fired > 0,
                FormattableString.Invariant(
                    $"GATE 3 FAILED: flare's lookahead never retrieved across {queries} queries, so this run measured flarefixed's behaviour under flare's name. SelfAssessmentConfidenceScorer fails open — every error and every unparsable reply scores 1.0, above the 0.6 threshold — so a scorer that is erroring, or replaying misses, silently turns the lookahead off. flare − flarefixed computed from this run cannot tell 'the lookahead does nothing' from 'the lookahead never ran'. Check the scorer's replies before spending anything on the full sweep."));

            var rate = fired / (double)queries;
            output.WriteLine(FormattableString.Invariant(
                $"GATE 3 PASSED: flare's lookahead retrieved {fired} time(s) across {queries} queries ({rate:F3} per query)."));

            if (rate < LowLookaheadRatePerQuery)
            {
                output.WriteLine(FormattableString.Invariant(
                    $"WARNING: that is {rate:F3} lookahead retrievals per query, below {LowLookaheadRatePerQuery:F2}. The gate proves the lookahead CAN fire; at this rate almost every query still answered exactly the way flarefixed would, so the same fail-open the gate exists to catch may be happening on all but a handful of them and flare - flarefixed would still be close to a measurement of nothing. Read the scorer's replies before concluding the lookahead does not help."));
            }
        }

        /// <summary>
        /// The lookahead rate per query below which Gate 3 passes but says so loudly.
        /// </summary>
        /// <remarks>
        /// <b>A smell, not a line, and deliberately not a hard bound.</b> Nobody has measured what
        /// this corpus's lookahead rate should be, so failing a run against a number nobody has
        /// established would be inventing a threshold and then enforcing it. What can be said
        /// honestly is that one retrieval in fifty queries is not evidence the mechanism is working,
        /// only evidence it is reachable — so the run reports it and the reader judges. The observed
        /// rate is printed either way, so a future measurement can replace this guess with a figure.
        /// </remarks>
        private const double LowLookaheadRatePerQuery = 0.1;
    }

    /// <summary>
    /// An <see cref="IRetriever"/> decorator that counts what it forwards — Gate 3's observation
    /// point on <c>flare</c>'s lookahead.
    /// </summary>
    /// <remarks>
    /// One instance for the run, wrapped around the single pipeline retriever
    /// <see cref="BuildEnginePipeline"/> resolves, so a retrieval from any query on any thread lands
    /// on the same counter. <see cref="Interlocked"/> because the answer loop is parallel.
    /// </remarks>
    private sealed class CountingRetriever : IRetriever
    {
        private readonly IRetriever _inner;
        private int _calls;

        public CountingRetriever(IRetriever inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        /// <summary>How many retrievals have been forwarded.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return _inner.RetrieveAsync(query, options, cancellationToken);
        }
    }

    /// <summary>Every <see cref="ProgressEvery"/>th completed query, verbatim as it always was.</summary>
    /// <remarks>
    /// The cache counters it prints are whole without being summed anywhere: every engine arm's
    /// client holds the <b>same</b> <c>GraphExtractionCache</c> instance this one does, so
    /// <c>Hits</c> and <c>Misses</c> have already seen every arm's requests. That is why this line
    /// needed no change when the per-arm clients split <c>Calls</c> and <c>Retries</c>.
    /// </remarks>
    private void ReportProgress(
        int completed,
        QuerySelection selection,
        IReadOnlyList<string> arms,
        long startedAt,
        CachedGraphRagClient answering)
    {
        if (completed % ProgressEvery != 0)
        {
            return;
        }

        _output.WriteLine(FormattableString.Invariant(
            $"  answered {completed} of {selection.Queries.Count} queries x {arms.Count} arms, {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s so far, {answering.Cache.Hits} cached / {answering.Cache.Misses} generated"));
    }

    /// <summary>
    /// An engine arm's answer: the shared dense retrieval, then the arm's own engine over the
    /// <see cref="SearchResult"/>s directly.
    /// </summary>
    /// <remarks>
    /// <b><see cref="PromptTemplate"/> is not on this path at all.</b> The engine builds its own
    /// prompts, so nothing here can reach — or perturb — the cache keys the pinned <c>dense</c>,
    /// <c>global</c> and RAPTOR figures were generated under. That separation is the reason the two
    /// branches in <see cref="AnswerAllAsync"/> are kept apart rather than folded together.
    /// </remarks>
    /// <param name="denseReference">
    /// The <c>dense</c> arm's sources for this query, from
    /// <see cref="RetrieveDenseGateReferenceAsync"/> — Gate 1's reference.
    /// </param>
    private async Task<string> AnswerThroughEngineAsync(
        string arm,
        BeirQuery query,
        IReadOnlyList<SearchResult> denseReference,
        AnswerPass pass,
        CancellationToken ct)
    {
        // No engine arm is localspec and every one of them retrieves the dense way, so this lands
        // on RetrieveContextAsync's shared Dense case.
        var sources = await RetrieveContextAsync(
            arm, query.Text, pass.Run, pass.Articles, pass.CorpusRun, pass.PerDocumentRun,
            pass.Generator, pass.Embeddings, pass.Answering, _output, ct);

        AssertContextIsIdenticalToDense(arm, query.Id, denseReference, sources);

        // Gate 2's counter. One instance per (arm, query), wrapped around THIS arm's client, so
        // what it reads is unambiguously this engine's own calls — see its remarks for why a
        // before/after delta on any shared counter would not be.
        using var counter = new EngineCallCountingChatClient(pass.EngineClients.For(arm));
        var engine = AnswerEngineArms.Create(arm, counter, pass.Retrievers.For(arm), pass.Failures);

        // FLARE's fragments are not complete replies, so the extraction contract cannot ride on
        // them — applied per fragment it makes the model close the answer on every call and never
        // emit <DONE> (2026-08-29: one response carried the closing sentence 256 times). Every
        // other arm emits one complete reply and takes the contract directly.
        var isFlare = string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal)
            || string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal);

        var response = await engine.AskAsync(
            query.Text, sources, isFlare ? FlareLoopOptions : EngineAnswerOptions, ct);

        var answer = isFlare
            ? await ApplyExtractionContractAsync(counter, query.Text, response.Answer, ct)
            : response.Answer;

        AssertCallShapeMatchesPrediction(arm, query.Id, sources.Count, counter.Calls);
        return answer;
    }

    /// <summary>
    /// What every engine arm except FLARE answers under: the <b>extraction contract</b>
    /// <see cref="MultiHopRagAnswerJudge.AnswerInstruction"/>, passed as the system prompt.
    /// </summary>
    /// <remarks>
    /// <b>Without this the engine arms do not measure engines.</b> The judge reads the answer out of
    /// the sentence that instruction asks for, and <b>falls back to the whole reply trimmed</b> when
    /// it is absent — so an engine that was never told the contract gets a discursive paragraph
    /// scored against a few-word gold answer by a shared-word rule. The 2026-08-28 pilot measured
    /// exactly that: <c>dense</c> met the contract on 9 of 9 queries and <b>every engine arm on 0 of
    /// 9</b>, which makes the engine accuracy figures from that run uninterpretable.
    /// <para>
    /// The instruction belongs to the <b>measurement apparatus, not the product</b>. A real
    /// <c>MapReduceAnswerEngine</c> user has no reason to end with that sentence; this harness's
    /// judge has to be able to find the answer, and the <c>dense</c> arm has always carried the same
    /// instruction inside <see cref="PromptTemplate"/>. Passing it here puts every arm under one
    /// output contract so the comparison is about the mechanism.
    /// </para>
    /// <para>
    /// All three non-FLARE engines honour <see cref="RagOptions.SystemPrompt"/> —
    /// <c>ChatAnswerEngine</c> as <c>opts.SystemPrompt ?? DefaultSystemPrompt</c>, MapReduce and
    /// Refine by prepending a system message when it is non-null — so this reaches every call each
    /// engine makes, including MapReduce's per-chunk maps and Refine's rewrites.
    /// </para>
    /// <para>
    /// <b>The two FLARE arms are excluded.</b> FLARE generates one sentence per call and feeds the
    /// growing answer back in as "answer so far", stopping only when the model emits
    /// <c>&lt;DONE&gt;</c>. A terminal instruction applied to every fragment makes the model close
    /// the answer on every call — the closing sentence becomes part of "answer so far" and the model
    /// closes it again, never reaching <c>&lt;DONE&gt;</c> (2026-08-29: one response held the same
    /// sentence 256 times, 86,091 bytes, and two benchmark runs died on HTTP timeouts). FLARE instead
    /// runs its sentence loop under <see cref="FlareLoopOptions"/> — no contract — and
    /// <see cref="ApplyExtractionContractAsync"/> puts the assembled answer under this same contract
    /// once, after the loop.
    /// </para>
    /// <para>
    /// <b>It changes every engine prompt, and therefore every engine cache key.</b> The 323 entries
    /// the 2026-08-28 pilot wrote are orphaned by it. That is the right trade: they answer a
    /// question nobody asked.
    /// </para>
    /// </remarks>
    private static readonly RagOptions EngineAnswerOptions = new()
    {
        SystemPrompt = MultiHopRagAnswerJudge.AnswerInstruction,
    };

    /// <summary>What FLARE's sentence loop runs under: no contract, because fragments are not replies.</summary>
    private static readonly RagOptions FlareLoopOptions = new();

    /// <summary>
    /// Puts FLARE's assembled answer under the same extraction contract every other arm answers
    /// under, in one call after the loop.
    /// </summary>
    /// <remarks>
    /// Counted by Gate 2 like any other call — it goes through the same counting client — so
    /// <see cref="PredictedCallShape"/> carries it in the FLARE bounds rather than the gate being
    /// loosened to hide it.
    /// </remarks>
    private static async Task<string> ApplyExtractionContractAsync(
        IChatClient client, string question, string draft, CancellationToken ct)
    {
        var prompt =
            MultiHopRagAnswerJudge.AnswerInstruction + "\n\n" +
            "Question: " + question + "\n\n" +
            "Draft answer:\n" + draft;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], null, ct);
        return response.Text;
    }

    // ── The pilot's gates ─────────────────────────────────────────────────

    /// <summary>
    /// <b>Gate 1.</b> An engine arm's retrieved sources are identical to the <c>dense</c> arm's:
    /// same chunks, same order, same depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted even though it is true by construction.</b> The five engine arms share
    /// <see cref="RetrieveContextAsync"/>'s <c>case AnswerArm.Dense:</c> body, so there is one
    /// retrieval rather than two paths that have to agree — and "true by construction" is exactly
    /// the claim that stops holding the day somebody edits that switch. Every engine result is
    /// reported as a difference against <c>chatengine</c> or <c>dense</c>, and a difference in what
    /// was retrieved would be published as a difference in what the engine does.
    /// </para>
    /// <para>
    /// <b><c>flare</c> is gated on its initial sources only.</b> Its lookahead retrieves again
    /// mid-generation, inside the engine and after this call, and those additions are the thing
    /// <c>flare − flarefixed</c> prices. Gate 3 is what watches them.
    /// </para>
    /// <para>
    /// <b>Applicability.</b> Retrieval only — no model, no cache, no tokens — so it asserts on
    /// every run that selects an engine arm, replay and fill alike.
    /// </para>
    /// </remarks>
    /// <param name="arm">The engine arm being checked.</param>
    /// <param name="queryId">The query, named in any failure.</param>
    /// <param name="dense">The <c>dense</c> arm's sources for this query.</param>
    /// <param name="actual">The engine arm's sources for this query.</param>
    private static void AssertContextIsIdenticalToDense(
        string arm, string queryId, IReadOnlyList<SearchResult> dense, IReadOnlyList<SearchResult> actual)
    {
        var shared = Math.Min(dense.Count, actual.Count);
        for (var rank = 0; rank < shared; rank++)
        {
            var expected = ChunkIdentityOf(dense[rank]);
            var got = ChunkIdentityOf(actual[rank]);
            if (!string.Equals(expected, got, StringComparison.Ordinal))
            {
                Assert.Fail(FormattableString.Invariant(
                    $"GATE 1 FAILED (context identity): query '{queryId}', arm '{arm}' — the first difference is at rank {rank}, where this arm retrieved chunk '{got}' and the dense arm retrieved '{expected}'. The engine arms are supposed to share RetrieveContextAsync's case AnswerArm.Dense body, so a difference here means that switch was edited and '{arm} - chatengine' would now be a retrieval difference published as an engine difference."));
            }
        }

        if (dense.Count != actual.Count)
        {
            Assert.Fail(FormattableString.Invariant(
                $"GATE 1 FAILED (context identity): query '{queryId}', arm '{arm}' — the first {shared} chunk(s) match but this arm retrieved {actual.Count} chunk(s) where the dense arm retrieved {dense.Count}. Same chunks in the same order also means the same number of them."));
        }
    }

    /// <summary>
    /// A chunk's identity for Gate 1: its document plus its 0-based index within that document,
    /// which <see cref="TextChunk.ChunkIndex"/> documents as the stable per-chunk key.
    /// </summary>
    /// <remarks>
    /// Identity rather than text: two chunks can carry identical text (a repeated boilerplate
    /// paragraph across two articles) and comparing the text would call those interchangeable when
    /// the ranking had in fact changed.
    /// </remarks>
    private static string ChunkIdentityOf(SearchResult result) => FormattableString.Invariant(
        $"{result.Chunk.DocumentId.Value}#{result.Chunk.ChunkIndex}");

    /// <summary>
    /// <b>Gate 2.</b> One engine's calls for one query match the shape its mechanism predicts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The number checked here is the engine's own, not a delta.</b> Every query is answered
    /// under <c>Parallel.ForEachAsync</c>, so snapshotting any counter shared across arms or queries
    /// before and after one <c>AskAsync</c> and subtracting would fold in whatever the other seven
    /// in-flight queries incremented in between. That produces a plausible number that is silently
    /// wrong — worse than no gate. <see cref="EngineCallCountingChatClient"/> is instead constructed
    /// per (arm, query) and handed to that one engine, so it can only ever have counted that
    /// engine's calls.
    /// </para>
    /// <para>
    /// <b>Applicability.</b> The calls are made whether they are served live or from cache — the
    /// cache sits below the counter — so this asserts on every run that answers through an engine.
    /// It is not skipped on a replay.
    /// </para>
    /// <para>
    /// <b>The FLARE bound is derived from <c>FlareOptions</c>, not pinned as a literal.</b> The
    /// sentence loop runs at most <c>MaxSentences</c> times and costs two calls per sentence (one to
    /// generate it, one for the scorer), plus one regeneration per lookahead, capped at
    /// <c>MaxRetrievals</c>. Reading the defaults keeps the bound correct if they change; a literal
    /// would fail a run for a reason that has nothing to do with the arm.
    /// </para>
    /// </remarks>
    /// <param name="arm">The engine arm.</param>
    /// <param name="queryId">The query, named in any failure.</param>
    /// <param name="contextChunks">How many chunks the engine was handed.</param>
    /// <param name="calls">What this engine's own counter observed.</param>
    private static void AssertCallShapeMatchesPrediction(
        string arm, string queryId, int contextChunks, int calls)
    {
        Assert.True(
            contextChunks == ContextChunks,
            FormattableString.Invariant(
                $"GATE 2 FAILED (call shape): query '{queryId}', arm '{arm}' was handed {contextChunks} context chunk(s) where the paper's depth is {ContextChunks}. The per-chunk engines' call counts are predicted from that depth, so a short context would make this gate assert the wrong number rather than catch anything."));

        var (min, max, shape) = PredictedCallShape(arm, contextChunks);
        Assert.True(
            calls >= min && calls <= max,
            FormattableString.Invariant(
                $"GATE 2 FAILED (call shape): query '{queryId}', arm '{arm}' made {calls} model call(s); {shape} predicts {DescribeBound(min, max)} at a context depth of {contextChunks}. Either the engine is not doing what the arm's name says, or the full sweep is mispriced by the ratio between those two numbers."));
    }

    /// <summary>The call count one engine arm's mechanism predicts, and the words for it.</summary>
    private static (int Min, int Max, string Shape) PredictedCallShape(string arm, int contextChunks)
    {
        if (string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal))
        {
            return (1, 1, "a single-shot ChatAnswerEngine");
        }

        if (string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal))
        {
            return (contextChunks, contextChunks, "RefineAnswerEngine's one call per context chunk");
        }

        if (string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal))
        {
            return (contextChunks + 1, contextChunks + 1, "MapReduceAnswerEngine's one map call per context chunk plus one reduce");
        }

        var defaults = new FlareOptions();
        var sentenceCalls = defaults.MaxSentences * 2;

        // +1 for the post-loop extraction-contract call the arm makes through the same counting
        // client; min 2 because that call is unconditional.
        if (string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal))
        {
            return (2, sentenceCalls + 1, FormattableString.Invariant(
                $"FlareAnswerEngine at MaxRetrievals=0 (at most {defaults.MaxSentences} sentences x 2 calls, no regeneration, plus one contract call)"));
        }

        if (string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal))
        {
            return (2, sentenceCalls + defaults.MaxRetrievals + 1, FormattableString.Invariant(
                $"FlareAnswerEngine as shipped (at most {defaults.MaxSentences} sentences x 2 calls, plus one regeneration per lookahead capped at {defaults.MaxRetrievals}, plus one contract call)"));
        }

        throw new ArgumentOutOfRangeException(nameof(arm), arm, "Not an arm with a predicted call shape.");
    }

    private static string DescribeBound(int min, int max) => min == max
        ? FormattableString.Invariant($"exactly {min}")
        : FormattableString.Invariant($"between {min} and {max}");

    // ── Proving the gates can fire ────────────────────────────────────────
    //
    // The gates themselves only run inside the paid answer theory, which needs an ONNX model, a
    // downloaded corpus and an API key, and skips without them. A gate nobody has ever seen fail is
    // a gate nobody knows works — the same reasoning that put
    // RetrieveRaptorFilteredAsync_WarnsOnUnderFill_AndStaysSilentWhenFull in this file. These run
    // on any machine, in milliseconds, with no model and no corpus.

    /// <summary>Gate 1 passes on the identical ranking and fails on a reordered one, naming the rank.</summary>
    [Fact]
    public void AssertContextIsIdenticalToDense_FailsOnAReorderedRanking_AndPassesOnAnIdenticalOne()
    {
        var dense = GateFixtureSources("a", "b", "c");

        AssertContextIsIdenticalToDense("chatengine", "q-1", dense, GateFixtureSources("a", "b", "c"));

        var swapped = GateFixtureSources("a", "c", "b");
        var error = Assert.ThrowsAny<Exception>(
            () => AssertContextIsIdenticalToDense("mapreduce", "q-1", dense, swapped));

        Assert.Contains("GATE 1 FAILED", error.Message, StringComparison.Ordinal);
        Assert.Contains("q-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("mapreduce", error.Message, StringComparison.Ordinal);
        Assert.Contains("rank 1", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Gate 1 also catches a prefix-identical context of the wrong depth.</summary>
    [Fact]
    public void AssertContextIsIdenticalToDense_FailsWhenTheDepthsDiffer()
    {
        var error = Assert.ThrowsAny<Exception>(
            () => AssertContextIsIdenticalToDense(
                "refine", "q-2", GateFixtureSources("a", "b", "c"), GateFixtureSources("a", "b")));

        Assert.Contains("GATE 1 FAILED", error.Message, StringComparison.Ordinal);
        Assert.Contains("2 chunk(s) where the dense arm retrieved 3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Gate 2 accepts each engine's predicted shape and rejects a count off it.</summary>
    [Fact]
    public void AssertCallShapeMatchesPrediction_AcceptsThePredictedShapes_AndRejectsOthers()
    {
        AssertCallShapeMatchesPrediction(AnswerArm.ChatEngine, "q-3", ContextChunks, 1);
        AssertCallShapeMatchesPrediction(AnswerArm.Refine, "q-3", ContextChunks, ContextChunks);
        AssertCallShapeMatchesPrediction(AnswerArm.MapReduce, "q-3", ContextChunks, ContextChunks + 1);
        AssertCallShapeMatchesPrediction(AnswerArm.Flare, "q-3", ContextChunks, 4);
        AssertCallShapeMatchesPrediction(AnswerArm.FlareFixed, "q-3", ContextChunks, 2);

        // The failure this gate exists for: an arm named mapreduce that answers in one call is not
        // doing map-reduce, and the sweep priced against it would be out by ContextChunks.
        var error = Assert.ThrowsAny<Exception>(
            () => AssertCallShapeMatchesPrediction(AnswerArm.MapReduce, "q-3", ContextChunks, 1));
        Assert.Contains("GATE 2 FAILED", error.Message, StringComparison.Ordinal);
        Assert.Contains("exactly 7", error.Message, StringComparison.Ordinal);

        // The FLARE minimum is 2, not 1: the post-loop extraction-contract call is unconditional,
        // so a FLARE arm that made only 1 call skipped it — a single sentence call with no contract
        // applied is not a shape either FLARE arm can produce.
        var tooFew = Assert.ThrowsAny<Exception>(
            () => AssertCallShapeMatchesPrediction(AnswerArm.FlareFixed, "q-3", ContextChunks, 1));
        Assert.Contains("GATE 2 FAILED", tooFew.Message, StringComparison.Ordinal);

        // A FLARE arm that never stops is the other direction; the bound comes from FlareOptions,
        // plus the one post-loop extraction-contract call every FLARE arm makes.
        var runaway = new FlareOptions();
        var loop = Assert.ThrowsAny<Exception>(
            () => AssertCallShapeMatchesPrediction(
                AnswerArm.Flare, "q-3", ContextChunks, (runaway.MaxSentences * 2) + runaway.MaxRetrievals + 2));
        Assert.Contains("GATE 2 FAILED", loop.Message, StringComparison.Ordinal);
    }

    /// <summary>Gate 2's precondition: a short context would make it assert the wrong number.</summary>
    [Fact]
    public void AssertCallShapeMatchesPrediction_FailsWhenTheContextIsNotThePapersDepth()
    {
        var error = Assert.ThrowsAny<Exception>(
            () => AssertCallShapeMatchesPrediction(AnswerArm.Refine, "q-4", ContextChunks - 1, ContextChunks - 1));

        Assert.Contains("GATE 2 FAILED", error.Message, StringComparison.Ordinal);
        Assert.Contains("context chunk(s)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The parallel-counter trap, demonstrated rather than asserted in prose.</b> Two engines
    /// answering concurrently over one shared client each see exactly their own calls, while the
    /// shared client underneath sees both — which is precisely why a before/after delta on the
    /// shared counter cannot be Gate 2's number.
    /// </summary>
    [Fact]
    public async Task EngineCallCountingChatClient_CountsOnlyItsOwnCalls_WhileTheSharedClientSeesBoth()
    {
        const int CallsEach = 200;
        var shared = new SharedCallCountingChatClient();
        var first = new EngineCallCountingChatClient(shared);
        var second = new EngineCallCountingChatClient(shared);
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            Task.Run(() => CallRepeatedlyAsync(first, CallsEach, ct), ct),
            Task.Run(() => CallRepeatedlyAsync(second, CallsEach, ct), ct));

        Assert.Equal(CallsEach, first.Calls);
        Assert.Equal(CallsEach, second.Calls);
        Assert.Equal(CallsEach * 2, shared.Calls);
    }

    private static async Task CallRepeatedlyAsync(IChatClient client, int times, CancellationToken ct)
    {
        for (var i = 0; i < times; i++)
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], cancellationToken: ct);
        }
    }

    /// <summary>
    /// Gate 3 fails when <c>flare</c> ran without its lookahead ever firing, passes once it has
    /// fired, and reports itself not applicable when <c>flare</c> was not selected.
    /// </summary>
    [Fact]
    public async Task AssertLookaheadFired_FailsWhenFlareNeverRetrieved_PassesOnceItHas()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] withFlare = [AnswerArm.Flare, AnswerArm.FlareFixed];
        var retrievers = new EngineRetrievers(new EmptyRetriever());

        var skipped = new CapturingTestOutputHelper();
        retrievers.AssertLookaheadFired([AnswerArm.ChatEngine], queries: 50, skipped);
        Assert.Contains(
            skipped.Lines, line => line.Contains("NOT APPLICABLE", StringComparison.Ordinal));

        var silent = new CapturingTestOutputHelper();
        var error = Assert.ThrowsAny<Exception>(
            () => retrievers.AssertLookaheadFired(withFlare, queries: 50, silent));
        Assert.Contains("GATE 3 FAILED", error.Message, StringComparison.Ordinal);
        Assert.Contains("fails open", error.Message, StringComparison.Ordinal);

        _ = await retrievers.For(AnswerArm.Flare)!.RetrieveAsync("a lookahead", cancellationToken: ct);

        var fired = new CapturingTestOutputHelper();
        retrievers.AssertLookaheadFired(withFlare, queries: 50, fired);
        Assert.Contains(fired.Lines, line => line.Contains("GATE 3 PASSED", StringComparison.Ordinal));
        Assert.Equal(1, retrievers.FlareLookaheads);
    }

    /// <summary>
    /// Gate 3 warns loudly when the lookahead only just fired, and stays silent once it is
    /// commonplace — the case a bare "at least one" pass cannot distinguish from the failure the
    /// gate exists to catch.
    /// </summary>
    [Fact]
    public async Task AssertLookaheadFired_WarnsWhenTheLookaheadBarelyFired_AndIsSilentWhenItIsCommon()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] withFlare = [AnswerArm.Flare];
        var retrievers = new EngineRetrievers(new EmptyRetriever());
        var flare = retrievers.For(AnswerArm.Flare)!;

        // One retrieval in fifty queries: the gate passes, because it did fire, and says so loudly.
        _ = await flare.RetrieveAsync("a lone lookahead", cancellationToken: ct);
        var barely = new CapturingTestOutputHelper();
        retrievers.AssertLookaheadFired(withFlare, queries: 50, barely);

        Assert.Contains(barely.Lines, line => line.Contains("GATE 3 PASSED", StringComparison.Ordinal));
        Assert.Contains(barely.Lines, line => line.Contains("WARNING", StringComparison.Ordinal));

        // Twenty in fifty is 0.4 per query, comfortably clear of the warning.
        for (var i = 0; i < 19; i++)
        {
            _ = await flare.RetrieveAsync("another lookahead", cancellationToken: ct);
        }

        var common = new CapturingTestOutputHelper();
        retrievers.AssertLookaheadFired(withFlare, queries: 50, common);

        Assert.Contains(common.Lines, line => line.Contains("GATE 3 PASSED", StringComparison.Ordinal));
        Assert.DoesNotContain(common.Lines, line => line.Contains("WARNING", StringComparison.Ordinal));
    }

    /// <summary>Sources whose chunk identities are exactly <paramref name="documentIds"/>, in order.</summary>
    private static IReadOnlyList<SearchResult> GateFixtureSources(params string[] documentIds)
    {
        var sources = new SearchResult[documentIds.Length];
        for (var i = 0; i < documentIds.Length; i++)
        {
            sources[i] = new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = "context " + documentIds[i],
                    DocumentId = new DocumentId(documentIds[i]),
                    ChunkIndex = 0,
                },
                Score = 1.0 - (i * 0.01),
            };
        }

        return sources;
    }

    /// <summary>Stands in for the client every arm shares, counting what reaches it.</summary>
    private sealed class SharedCallCountingChatClient : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The arms use AskAsync, not streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>A retriever that succeeds with nothing — enough for Gate 3 to have something to count.</summary>
    private sealed class EmptyRetriever : IRetriever
    {
        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));
    }

    /// <summary>
    /// An <see cref="IChatClient"/> decorator that counts its own invocations and forwards
    /// everything else — <b>one instance per (arm, query)</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This type exists to make Gate 2 possible at all.</b> The obvious implementation is to read
    /// the shared client's <c>Calls</c> before and after one engine's <c>AskAsync</c> and take the
    /// difference. Under <c>Parallel.ForEachAsync</c> at <see cref="AnswerConcurrency"/> = 8 that
    /// difference also contains every call the other seven in-flight queries made in the same
    /// window — a number that looks like a call count, is not one, and would let a mis-wired arm
    /// pass while making the gate itself the thing reporting the wrong figure.
    /// </para>
    /// <para>
    /// Serialising the run to make a global delta valid was the other option and is worse: it would
    /// change the workload being measured to make the measurement easier. A fresh counter per
    /// (arm, query), handed to exactly one engine, has neither problem — no other engine holds a
    /// reference to it, so no other engine can increment it, whatever else is running.
    /// <see cref="Interlocked"/> is still used because a single engine may itself fan out.
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> is a no-op: the client underneath is borrowed for the length of one
    /// answer and outlives this decorator by the whole run.
    /// </para>
    /// </remarks>
    private sealed class EngineCallCountingChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        private int _calls;

        public EngineCallCountingChatClient(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        /// <summary>How many requests this one engine made.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return _inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "The engine arms answer through AskAsync. CachedGraphRagClient refuses to stream " +
                "for the same reason: a streaming call would be an uncached, uncounted model call.");

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            // The client underneath belongs to EngineArmClients and lives for the whole run.
        }
    }

    /// <summary>
    /// A pass-through <see cref="IChatClient"/> whose <see cref="Dispose"/> does nothing, so a
    /// second owner can be handed the live model without ever disposing it.
    /// </summary>
    /// <remarks>
    /// <c>CachedGraphRagClient.Dispose</c> disposes the model it was constructed with. The run has
    /// one model and, from <see cref="EngineArmClients"/>, several clients over it; without this the
    /// first client disposed would take the model out from under the rest. The single owner stays
    /// the client <see cref="OpenAnsweringClient"/> returns, which the theory holds in a
    /// <c>using</c>.
    /// </remarks>
    private sealed class BorrowedChatClient : IChatClient
    {
        private readonly IChatClient _inner;

        public BorrowedChatClient(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return _inner.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            // Borrowed. The answering client returned by OpenAnsweringClient owns the model.
        }
    }

    /// <summary>
    /// One <see cref="CachedGraphRagClient"/> per selected engine arm, over the <b>shared</b> answer
    /// cache and the <b>shared</b> model — the per-arm cost meters Step 4's counters are read from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an extra client rather than a delta on the shared one.</b> The token counters are the
    /// same parallelism problem as Gate 2's call counts, and worse: usage is recorded inside
    /// <c>CachedGraphRagClient</c> on the live call, so a decorator above it cannot see the figure
    /// at all — <c>ChatResponse.Usage</c> is gone by the time the cache has answered. Giving each
    /// arm its own instance of that class makes <c>Calls</c>, <c>InputTokens</c> and
    /// <c>OutputTokens</c> exactly that arm's, using the same <see cref="Interlocked"/> counters
    /// already trusted elsewhere, with no delta arithmetic anywhere.
    /// </para>
    /// <para>
    /// <b>Nothing about the cache changes.</b> Every client here holds the <i>same</i>
    /// <c>GraphExtractionCache</c> instance the shared client holds, at the same temperature, and
    /// the key is the rendered prompt — so an engine arm's entries land in the same directory, under
    /// the same identity, as they would have through one client. Only the counters are split, and
    /// only for the five engine arms: the non-engine path keeps the shared client untouched.
    /// </para>
    /// </remarks>
    private sealed class EngineArmClients : IDisposable
    {
        private readonly Dictionary<string, CachedGraphRagClient> _byArm = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        /// <param name="arms">The run's selected arms; the non-engine ones are ignored.</param>
        /// <param name="cache">The shared answer cache — the same instance, not a second one.</param>
        /// <param name="model">The live model, or <see langword="null"/> on a replay run.</param>
        /// <param name="temperature">The identity's temperature, shared with the answering client.</param>
        public EngineArmClients(
            IReadOnlyList<string> arms, GraphExtractionCache cache, IChatClient? model, float temperature)
        {
            ArgumentNullException.ThrowIfNull(arms);
            ArgumentNullException.ThrowIfNull(cache);

            for (var i = 0; i < arms.Count; i++)
            {
                var arm = arms[i];
                if (!AnswerEngineArms.IsEngineArm(arm) || _byArm.ContainsKey(arm))
                {
                    continue;
                }

                _byArm[arm] = new CachedGraphRagClient(
                    cache, model is null ? null : new BorrowedChatClient(model), temperature);
                _order.Add(arm);
            }
        }

        /// <summary>The engine arms that have a client here, in selection order.</summary>
        public IReadOnlyList<string> Arms => _order;

        /// <summary>Calls made through every engine arm's client, summed.</summary>
        public long TotalCalls
        {
            get
            {
                long total = 0;
                for (var i = 0; i < _order.Count; i++)
                {
                    total += _byArm[_order[i]].Calls;
                }

                return total;
            }
        }

        /// <summary>Model attempts retried through every engine arm's client, summed.</summary>
        /// <remarks>
        /// <b>Read for the same reason <see cref="TotalCalls"/> is.</b> Every counter on
        /// <c>CachedGraphRagClient</c> is per instance, so the moment the engine arms got clients of
        /// their own, every whole-run figure taken from the shared client alone became the
        /// non-engine share of itself. Retries is the one that matters most in practice: the engine
        /// arms issue roughly seven times the request volume of a single-shot arm, so they are where
        /// rate limiting shows up first, and a run reporting "0 retries" while
        /// <c>mapreduce</c> is being throttled would send an operator looking in the wrong place.
        /// </remarks>
        public long TotalRetries
        {
            get
            {
                long total = 0;
                for (var i = 0; i < _order.Count; i++)
                {
                    total += _byArm[_order[i]].Retries;
                }

                return total;
            }
        }

        /// <summary>The client <paramref name="arm"/>'s engines are built over.</summary>
        public CachedGraphRagClient For(string arm) =>
            _byArm.TryGetValue(arm, out var client)
                ? client
                : throw new ArgumentOutOfRangeException(
                    nameof(arm), arm,
                    "No engine-arm client was built for this arm — it is not an engine arm, or it " +
                    "was not in the selection this instance was constructed from.");

        public void Dispose()
        {
            foreach (var client in _byArm.Values)
            {
                // Safe: each holds a BorrowedChatClient, so the run's one model is never disposed here.
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// The three <see cref="SearchResult"/> context maps and the one rendered-string map the graph
    /// arms read from — collected in one sequential pass when any of those arms is selected, and
    /// left empty when none is, exactly as the inline gate this replaces did.
    /// </summary>
    private async Task<GraphStoreContexts> CollectGraphStoreContextsIfNeededAsync(
        GraphRagRun run,
        QuerySelection selection,
        IReadOnlyList<string> arms,
        long startedAt,
        CancellationToken ct)
    {
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

        return new GraphStoreContexts(localContexts, controlContexts, filteredContexts, localSpecContexts);
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
    /// The six chunks the dense, global, RAPTOR and engine arms hand the model, in the arm's own
    /// order.
    /// </summary>
    /// <remarks>
    /// The five engine arms share <see cref="AnswerArm.Dense"/>'s case body rather than restating
    /// it. That sharing is what makes "the engine arms hold retrieval fixed at dense" true by
    /// construction: there is one retrieval, not two paths that have to agree. <c>flare</c> is the
    /// one exception, and it is an exception <i>after</i> this call — its lookahead retrieves again
    /// mid-generation through its own retriever, which is exactly what <c>flare − flarefixed</c>
    /// prices.
    /// <para/>
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
            case AnswerArm.ChatEngine:
            case AnswerArm.MapReduce:
            case AnswerArm.Refine:
            case AnswerArm.Flare:
            case AnswerArm.FlareFixed:
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
        EngineArmClients engineClients,
        TimeSpan elapsed)
    {
        // Every counter on CachedGraphRagClient is per instance, so each whole-run figure below has
        // to be summed across the engine arms' clients as well as the shared one — answering.Calls
        // and answering.Retries alone are only the non-engine share. The cache counters are the
        // exception, and genuinely whole already: every client holds the SAME cache instance, so
        // Hits and Misses have already seen every arm's requests.
        var requests = answering.Calls + engineClients.TotalCalls;
        var retries = answering.Retries + engineClients.TotalRetries;
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant($"""

            === {descriptor.Name} ACCURACY AGAINST THE GOLD ANSWERS — {elapsed.TotalSeconds:F1} s, {requests} answer requests ({engineClients.TotalCalls} of them through the engine arms), {answering.Cache.Hits} cached / {answering.Cache.Misses} generated, {retries} retries ({engineClients.TotalRetries} of them on the engine arms) ===
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

    /// <summary>
    /// <b>Step 4.</b> What the run actually cost, per engine arm — the figures that price the full
    /// sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not derived, and it supersedes the design document's dollar table.</b> That
    /// table was arithmetic over an assumed call shape; this is the shape the run performed. For
    /// <c>flare</c> in particular the calls-per-query figure is what settles a roughly tenfold cost
    /// range — the engine's loop can stop after one sentence or run to <c>MaxSentences</c>, and
    /// nothing short of running it says which.
    /// </para>
    /// <para>
    /// <b>Tokens are billed on live calls only.</b> <c>CachedGraphRagClient</c> accumulates usage
    /// where the model answers, so a replay run legitimately reports zero tokens against non-zero
    /// calls — that is the cache working, not a counter failing, and the line below says so rather
    /// than leaving a reader to wonder. A non-zero <c>CallsWithoutUsage</c> means the token figures
    /// are a floor; it is printed for the same reason.
    /// </para>
    /// <para>
    /// Only the engine arms appear per-arm: they are the arms this phase added and the only ones
    /// with a client of their own. Everything else — <c>dense</c>, <c>global</c>, the graph and
    /// RAPTOR arms, and RAPTOR summarisation — shares one client and is reported as one line, which
    /// is deliberate: splitting it would mean re-wiring the path every pinned figure was generated
    /// through.
    /// </para>
    /// </remarks>
    private static string DescribeEngineArmCosts(
        IReadOnlyList<string> arms,
        EngineArmClients engineClients,
        EngineRetrievers retrievers,
        AnswerEngineArms.FailureLog failures,
        CachedGraphRagClient answering,
        Dictionary<string, ArmTally> tallies)
    {
        // Nothing to price when no engine arm ran, and a header over an empty table is worse than
        // no header: every ordinary ten-arm run used to print one and invite the reader to wonder
        // which arm's costs had gone missing.
        var engineArms = engineClients.Arms;
        if (engineArms.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine().AppendLine(
            "=== ENGINE-ARM COST COUNTERS — what prices the full sweep ===");
        builder.AppendLine(
            "Measured per arm through that arm's own CachedGraphRagClient over the shared cache and");
        builder.AppendLine(
            "model, so no other arm's calls or tokens can land in these figures. Tokens are billed on");
        builder.AppendLine(
            "LIVE calls only: a cache replay reports zero tokens against non-zero calls, correctly.");
        builder.AppendLine(FormattableString.Invariant(
            $"{"arm",-14}{"queries",9}{"calls",9}{"calls/query",14}{"in tokens",14}{"out tokens",14}{"tokens/query",15}"));

        long engineCalls = 0;
        long engineInput = 0;
        long engineOutput = 0;
        long engineWithoutUsage = 0;
        for (var i = 0; i < engineArms.Count; i++)
        {
            var arm = engineArms[i];
            var client = engineClients.For(arm);
            engineCalls += client.Calls;
            engineInput += client.InputTokens;
            engineOutput += client.OutputTokens;
            engineWithoutUsage += client.CallsWithoutUsage;
            AppendArmCostRow(
                builder, arm, tallies.TryGetValue(arm, out var tally) ? tally.Answered : 0, client);
        }

        builder.AppendLine(FormattableString.Invariant(
            $"shared client (every non-engine arm, plus RAPTOR summarisation): {answering.Calls} calls, {answering.InputTokens:N0} in, {answering.OutputTokens:N0} out, {answering.Retries} retries"));

        // Printed explicitly rather than left for a reader to add up. Every counter on
        // CachedGraphRagClient is per instance, so the shared line above is the non-engine share of
        // the run and nothing else — reading it as the bill is the exact mistake this line removes.
        builder.AppendLine(FormattableString.Invariant(
            $"RUN TOTAL (shared + every engine arm): {answering.Calls + engineCalls} calls, {answering.InputTokens + engineInput:N0} in, {answering.OutputTokens + engineOutput:N0} out, {answering.Retries + engineClients.TotalRetries} retries"));
        builder.AppendLine(FormattableString.Invariant(
            $"calls that reported no usage, and are therefore absent from the token totals: {answering.CallsWithoutUsage} on the shared client, {engineWithoutUsage} across the engine arms — any non-zero figure here makes the tokens above a floor"));

        if (arms.Contains(AnswerArm.Flare, StringComparer.Ordinal))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"flare lookahead retrievals across the run: {retrievers.FlareLookaheads}"));
        }

        // Printed as well as asserted. The assert is what stops a degraded figure being published;
        // this line is what tells a reader which engine degraded and on what.
        builder.AppendLine(failures.Describe());

        return builder.ToString().TrimEnd();
    }

    /// <summary>One arm's row in the cost table.</summary>
    private static void AppendArmCostRow(
        StringBuilder builder, string arm, int queries, CachedGraphRagClient client)
    {
        var calls = client.Calls;
        var input = client.InputTokens;
        var output = client.OutputTokens;
        var callsPerQuery = queries == 0 ? double.NaN : calls / (double)queries;
        var tokensPerQuery = queries == 0 ? double.NaN : (input + output) / (double)queries;

        builder.AppendLine(FormattableString.Invariant(
            $"{arm,-14}{queries,9}{calls,9}{callsPerQuery,14:F2}{input,14:N0}{output,14:N0}{tokensPerQuery,15:F2}"));
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

    /// <summary>
    /// The four contexts <see cref="CollectGraphStoreContextsAsync"/>'s one sequential pass
    /// produces, keyed by query id — empty when no arm that reads them is selected.
    /// </summary>
    private sealed record GraphStoreContexts(
        Dictionary<string, IReadOnlyList<SearchResult>> Local,
        Dictionary<string, IReadOnlyList<SearchResult>> Control,
        Dictionary<string, IReadOnlyList<SearchResult>> Filtered,
        Dictionary<string, string> LocalSpec);

    /// <summary>
    /// Everything the parallel answer loop needs that does not vary per query, carried as one value
    /// so <see cref="AnswerOneQueryAsync"/> and the two arm paths can be methods rather than a
    /// lambda too long for MA0051.
    /// </summary>
    /// <param name="Arms">The run's selected arms, in the order they are answered.</param>
    /// <param name="HasEngineArm">
    /// Whether any selected arm is an engine arm — the condition under which Gate 1's dense
    /// reference is worth retrieving at all. Computed once rather than per query.
    /// </param>
    /// <param name="EngineClients">The per-arm cost meters; only engine arms have one.</param>
    /// <param name="Retrievers">The engine retrievers, and the two lookahead gates over them.</param>
    /// <param name="Failures">
    /// Where the engines' swallowed failures are counted — one instance for the run, so the whole
    /// answering phase reduces to the single number the gate asserts on.
    /// </param>
    /// <param name="Tallies">Where each arm's scored answers accumulate; internally locked.</param>
    private sealed record AnswerPass(
        IReadOnlyList<string> Arms,
        bool HasEngineArm,
        GraphRagRun Run,
        InMemoryVectorStore Articles,
        RaptorRun? CorpusRun,
        RaptorRun? PerDocumentRun,
        OnnxEmbeddingGenerator Generator,
        EmbeddingCache Embeddings,
        CachedGraphRagClient Answering,
        EngineArmClients EngineClients,
        EngineRetrievers Retrievers,
        AnswerEngineArms.FailureLog Failures,
        GraphStoreContexts GraphContexts,
        IReadOnlyDictionary<string, MultiHopRagAnswer> Gold,
        Dictionary<string, ArmTally> Tallies);

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
