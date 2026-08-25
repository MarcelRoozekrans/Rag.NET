using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What each answer-level arm last measured on MultiHop-RAG, and how far it may move before that
/// is a regression — <see cref="BeirReproduction"/>'s discipline, for a figure that is an accuracy
/// rather than an nDCG.
/// <para>
/// <b>Its own table rather than a row in <see cref="BeirReproduction"/></b>, because that table's
/// entries, messages and tolerance reasoning are all about nDCG@10 and a reader of "nDCG@10 =
/// 0.44" for an accuracy would be misled by the label. Same rule: a pair not in the table throws,
/// so an arm cannot exist without something pinning its number; an entry may record that no figure
/// exists, and the run then prints what it measured and asserts nothing.
/// </para>
/// <para>
/// <b>Tolerance ±0.005, and it is tighter than it looks.</b> Over the 2,255 judged queries one
/// answer flipping moves accuracy by 0.00044, so the window is eleven flips. Replayed from the
/// answer cache at temperature 0 the run is exact — every reply is a file — and the only way to
/// drift is retrieval handing the model different context, which misses the cache and fails before
/// any figure is computed. So a re-run either reproduces to the last digit or refuses.
/// </para>
/// </summary>
internal static class MultiHopRagAnswerReproduction
{
    /// <summary>How far a re-measurement may sit from the recorded one.</summary>
    public const double Tolerance = 0.005;

    private static readonly Reproduction[] Reproductions =
    [
        new(
            "multihop-rag",
            AnswerArm.Dense,
            [0.3499],
            "MEASURED 2026-08-15 on Windows 11, .NET 10.0.11, CPU ONNX Runtime -- Phase 5.2.2, the " +
            "full run: 2,556 queries x 4 arms, 19,674 answer requests (17,668 generated, 2,006 " +
            "cached from the 100-query pilot, 1 retry), 1 h 25 m, openai/gpt-4o-mini at " +
            "temperature 0, top-6 context, the prompt on BeirGraphRagAnswerTests. The dense arm: " +
            "the Real leg's 17,648 article chunks alone. **Paper-rule accuracy 0.3499 over the " +
            "2,255 judged queries** (raw 0.2603, strict 0.3242); inference 0.7721 (n=816, commits " +
            "on 82%, precision when committed 0.943), comparison 0.1636 (abstains on 78%), " +
            "temporal 0.0326 (abstains on 95%); abstains correctly on 48.5% of the 301 null " +
            "queries -- the best abstention of the four. The paper's Table 6 has ChatGPT at 0.44 " +
            "and GPT-4 at 0.56 with voyage-02 + bge-reranker top-6: same shape, different " +
            "embedder, different model, not comparable in number. **Read the yes/no types " +
            "against their base rates**: comparison gold is 60% yes and temporal 46% yes, so " +
            "always-yes scores 0.598 and 0.463 there, and this arm's low figures are abstention, " +
            "not error -- when it commits on comparison it is right 74% of the time."),
        new(
            "multihop-rag",
            AnswerArm.Control,
            [0.1384],
            "MEASURED 2026-08-15, same run. The candidate-set control at answer level: dense top-6 " +
            "over the graph run's 321,151-unit store, no graph behaviour. **0.1384** (raw 0.0922, " +
            "strict 0.1215); inference 0.2806, comparison 0.0876, temporal 0.0137; nulls 41.5%. " +
            "**Against the dense arm's 0.3499 this is what store pollution costs an answer: " +
            "-0.2115**, and on inference -0.4915 -- a top-6 full of entity, relationship and " +
            "report chunks (303,503 of them beside 17,648 article chunks) leaves the model almost " +
            "no article text. The same pollution cost the ranking -0.043 (#232); the answer sees " +
            "five times as much of it, because six chunks is a much smaller window than a top-10 " +
            "of max-pooled documents."),
        new(
            "multihop-rag",
            AnswerArm.Local,
            [0.2102],
            "MEASURED 2026-08-15, same run. GraphRAG local search at PageRankWeight 0.3 -- the shipped " +
            "default on that date, and NOT the default since #239 set it to 0; GraphRagRun pins 0.3 " +
            "explicitly so this figure keeps meaning what it says: " +
            "dense top-500 over the graph store, the behaviour's top-6 as context. **0.2102** (raw " +
            "0.1552, strict 0.1898); inference 0.4620, comparison 0.1005, temporal 0.0189; nulls " +
            "40.5%. **Below dense by 0.1397 and above the control by 0.0718.** The second number " +
            "is the blend #239 measured as a pure cost to the ranking doing something useful to " +
            "the context: demoting graph-connected entity chunks pushes article chunks back into " +
            "the top-6. The first number is 5.2's finding in the answer currency: local search as " +
            "shipped does not help answers on this dataset either, and store pollution is why."),
        new(
            "multihop-rag",
            AnswerArm.Global,
            [0.5951],
            "MEASURED 2026-08-15, same run. GraphRAG global search: GraphGlobalSearchBehavior's " +
            "map/reduce over the community reports (deterministic since #241), its synthesised " +
            "answer first and the next five candidates behind it as context. **0.5951** (raw " +
            "0.3242, strict 0.4523); nulls 9.3%. **Read per type, because the overall figure " +
            "mixes two different things.** Inference: **0.8444 against dense 0.7721** (n=816, " +
            "commits on 99% at precision 0.851 where dense commits on 82% at 0.943) -- a real, " +
            "honestly earned +0.0723, 59 more entity questions right, and the arm 5.2 could not " +
            "score at all. Comparison 0.4953 and temporal 0.3928: **below the always-yes " +
            "baselines of 0.598 and 0.463**; the arm answers yes 532 times and no 55 on " +
            "comparison, commits on 69-73% at precision 0.57-0.68, so those columns are " +
            "commitment on a skewed base rate, not comprehension. And it abstains on only 9.3% " +
            "of the null queries against dense's 48.5% -- it guesses on unanswerable questions. " +
            "**So: GraphRAG global helps on entity questions here and does not on yes/no ones, " +
            "and its overall lead over dense is about one third real.**"),
        new(
            "multihop-rag",
            AnswerArm.Filtered,
            [0.3494],
            "MEASURED 2026-08-17 on a SECOND machine (Windows 11, .NET 10.0.11, CPU ONNX Runtime) " +
            "from the restored cache -- the full run: 2,556 queries x 5 arms, 22,230 answer " +
            "requests, 22,121 cached / 109 generated, 0 retries, 1 h 33 m (store build 23 m 45 s). " +
            "Issue #247's option (c): the same dense top-500 the control arm sees, with every " +
            "graph-derived unit dropped before the top-6 is taken. **0.3494** (raw 0.2599, strict " +
            "0.3233). " +
            "**Against the control's 0.1384 this recovers +0.2110 of the -0.2115 that store " +
            "pollution costs -- 99.8% of it -- and lands 0.0005 below the article-only dense arm's " +
            "0.3499.** " +
            "**The residual is noise, and its direction says so.** Per type: comparison 0.1636 and " +
            "temporal 0.0326 are IDENTICAL to dense to four decimals; inference is 0.7708 against " +
            "dense's 0.7721, which is -0.0013 -- ONE answer in 816 (630.0 correct against " +
            "629.0), while four answers moved abstain->other, so the mix shifted more than the " +
            "score did; nulls are 0.4884 against 0.4850, +0.0034, which is one answer in 301 in " +
            "filtered's FAVOUR. A systematic loss would move one way on every type. " +
            "**The cache is the second, independent statement.** 109 generations out of 22,230 " +
            "requests: for 99.5% of queries the filtered top-6 was byte-identical to a context " +
            "some other arm had already answered, so the answer cache -- keyed on the prompt, which " +
            "embeds the context -- simply hit. Filtering the graph store's candidates does not " +
            "merely score like the article-only store; for almost every query it RECONSTRUCTS it. " +
            "The synthetic units are pure displacement: they evict article chunks from a six-slot " +
            "window without changing which article chunks would otherwise win it. " +
            "**Cost: 109 gpt-4o-mini completions, and no extra retrieval at all** -- the " +
            "over-fetch option (c) needs already exists, because local search returns its top-500 " +
            "and six are taken from it either way. " +
            "Measured in the harness; whether the filter becomes shipped library behaviour, and " +
            "whether on by default, is open on #247. What is no longer open is which of the " +
            "issue's three options works."),
        new(
            "multihop-rag",
            AnswerArm.LocalSpec,
            [0.3459],
            "MEASURED 2026-08-20 on Windows 11, .NET 10.0.11, CPU ONNX Runtime -- Phase 6.x.7, the " +
            "run this whole exercise was for. Microsoft's local search as specified: IGraphRagSearch, " +
            "the context builder from #317/#320/#321 plus conversation history, at the upstream " +
            "defaults (12,000 tokens, 0.15 community / 0.5 text-unit, top-10 entities oversampled to " +
            "20, covariates off). Its rendered context replaces the top-6 rendering in the SAME " +
            "PromptTemplate every other arm uses, so the only variable against dense/control/filtered " +
            "is the context. 2,556 queries, openai/gpt-4o-mini at temperature 0. Generation pass " +
            "50 m 21 s; the reproduction pass replayed all 2,556 from cache, generated ZERO, and " +
            "returned the SAME 2,556 PREDICTIONS -- checked as a multiset of (query, prediction, " +
            "paper, raw, strict) tuples rather than as an aggregate, so this is stronger than the " +
            "accuracies matching. On-disk row order differs between the two files because answering " +
            "is parallel; compare the tuples, not the lines. " +
            "**Paper-rule accuracy 0.3459 over the 2,255 judged queries** (raw 0.3140, strict " +
            "0.3255); inference 0.8603 (n=816), comparison 0.0736 (n=856), temporal 0.0257 (n=583); " +
            "abstains correctly on 34.6% of the 301 null queries. " +
            "**This is what Milestone 5.2 should have measured, and it changes 5.2's verdict.** That " +
            "phase concluded GraphRAG does not help on this corpus from the `local` arm's 0.2102 -- " +
            "a PageRank blend over dense candidates, which is not in Microsoft's local search at all. " +
            "Against it this arm is +0.1357 overall and **+0.3983 on inference**. " +
            "**On entity questions it is the best arm ever measured here: 0.8603, above global's " +
            "0.8444 and dense's 0.7721.** It commits on 91.4% of inference queries at precision " +
            "0.941, where dense commits on 82% at 0.943 -- the same accuracy, far more willing. " +
            "**It is nonetheless level with dense overall (-0.0040), and the shortfall is all " +
            "unwillingness elsewhere.** Read the yes/no types against their base rates: comparison " +
            "gold is 60% yes and temporal 46% yes, so always-yes scores 0.598 and 0.463, and this " +
            "arm's 0.0736 and 0.0257 are far below them because it ABSTAINS -- committing on 8.8% of " +
            "comparison (precision 0.827) and 4.3% of temporal (precision 0.600). It also abstains " +
            "on only 34.6% of the nulls against dense's 48.5%. So it declines the answerable " +
            "yes/no questions and commits on the unanswerable ones, both at once: the graph context " +
            "makes the model confident about entities and unwilling about comparisons. " +
            "**Cost, measured not estimated:** the caches this replays are 35,112 extraction calls " +
            "(22,309,528 tokens) plus 3,573 community reports (2,026,478 tokens), generated once by " +
            "`--stage extraction` and `--stage reports` over the full 609-article corpus; ~$9 at " +
            "gpt-4o-mini rates, and a re-run pays none of it."),
        new(
            "multihop-rag",
            AnswerArm.RaptorCorpus,
            [0.3588],
            "MEASURED 2026-08-25 on Windows 11, .NET 10.0.11 -- Phase 6.2.1 Task 5, the full sweep: " +
            "2,556 queries x 4 RAPTOR arms, 10,224 scored answers, 58 m after a 28 m I/O-bound " +
            "load, openai/gpt-4o-mini at temperature 0, top-6 context. Both trees were already " +
            "cached, so this run paid for answers only. Accuracy is over the **2,255 judged " +
            "queries**, the denominator every other pin here uses -- the 301 null queries are " +
            "scored separately as abstention. " +
            "**The validation gate held exactly at full scale**: raptorfiltered reproduced the dense " +
            "arm to four decimals on all three rules (0.3499 / 0.2603 / 0.3242), the figures pinned " +
            "2026-08-15. The corpora did not diverge, so these numbers measure RAPTOR. " +
            "**Paper-rule 0.3588** (raw 0.2656, strict 0.3322); inference 0.7831, comparison 0.1729, " +
            "temporal 0.0377; abstains correctly on 48.2% of the 301 nulls. " +
            "**raptorcorpus - raptor = -0.0146 paper, -0.0204 raw, -0.0027 strict.** Corpus-level " +
            "clustering -- 6.2.3's breaking change, and the shipped default -- is *worse* than the " +
            "per-document tree it replaced. McNemar over the paired judged queries: paper p=0.0247 " +
            "(85 corpus wins against 118 per-document), raw p=0.0006 (62 against 108), strict p=0.7372 " +
            "(a wash). Two of three rules significant, all three signed the same way. " +
            "**The 50-query pilot put this at +0.0000 and was simply underpowered** -- which is what " +
            "Task 5 existed to find out. " +
            "**The gap is inference queries**: 0.7831 against the control's 0.8309, while comparison " +
            "and temporal are flat. That is the opposite of the rationale for #331 -- corpus-spanning " +
            "summaries were meant to help exactly the multi-hop case they measurably hurt here. " +
            "**Read the yes/no types against their base rates**: comparison gold is 60% yes, so " +
            "always-yes scores 0.598; this arm commits on 19.9% of comparisons and is right 82.4% of " +
            "the time when it does. What to do about the default is a design decision, not a " +
            "measurement one, and is deliberately left open here."),
        new(
            "multihop-rag",
            AnswerArm.Raptor,
            [0.3734],
            "MEASURED 2026-08-25 on Windows 11, .NET 10.0.11 -- Phase 6.2.1 Task 5, the full sweep: " +
            "2,556 queries x 4 RAPTOR arms, 10,224 scored answers, 58 m after a 28 m I/O-bound " +
            "load, openai/gpt-4o-mini at temperature 0, top-6 context. Both trees were already " +
            "cached, so this run paid for answers only. Accuracy is over the **2,255 judged " +
            "queries**, the denominator every other pin here uses -- the 301 null queries are " +
            "scored separately as abstention. " +
            "**Paper-rule 0.3734** (raw 0.2860, strict 0.3348); inference 0.8309, comparison 0.1694, " +
            "temporal 0.0326; abstains correctly on 47.5% of the nulls. " +
            "This is the per-document control, and it is **the best of the four arms on every rule**. " +
            "See the RaptorCorpus entry for the comparison and its significance: the breaking change " +
            "that made corpus scope the default did not buy accuracy, it cost some."),
        new(
            "multihop-rag",
            AnswerArm.RaptorFiltered,
            [0.3499],
            "MEASURED 2026-08-25 on Windows 11, .NET 10.0.11 -- Phase 6.2.1 Task 5, the full sweep: " +
            "2,556 queries x 4 RAPTOR arms, 10,224 scored answers, 58 m after a 28 m I/O-bound " +
            "load, openai/gpt-4o-mini at temperature 0, top-6 context. Both trees were already " +
            "cached, so this run paid for answers only. Accuracy is over the **2,255 judged " +
            "queries**, the denominator every other pin here uses -- the 301 null queries are " +
            "scored separately as abstention. " +
            "**Paper-rule 0.3499** (raw 0.2603, strict 0.3242) -- the dense arm's pinned figures to " +
            "four decimals, which is what makes this the validation gate rather than a result. " +
            "Summaries filtered out, so only the 17,648 leaf chunks are reachable; reproducing dense " +
            "exactly is the evidence the RAPTOR corpus and the dense corpus are the same corpus. " +
            "**raptorcorpus - raptorfiltered = +0.0089 paper** (McNemar p=0.0293), +0.0053 raw " +
            "(p=0.1416), +0.0080 strict (p=0.0795): what the summaries add, significant on one rule " +
            "of three and small on all of them."),
        new(
            "multihop-rag",
            AnswerArm.RaptorBoost,
            [0.3450],
            "MEASURED 2026-08-25 on Windows 11, .NET 10.0.11 -- Phase 6.2.1 Task 5, the full sweep: " +
            "2,556 queries x 4 RAPTOR arms, 10,224 scored answers, 58 m after a 28 m I/O-bound " +
            "load, openai/gpt-4o-mini at temperature 0, top-6 context. Both trees were already " +
            "cached, so this run paid for answers only. Accuracy is over the **2,255 judged " +
            "queries**, the denominator every other pin here uses -- the 301 null queries are " +
            "scored separately as abstention. " +
            "**Paper-rule 0.3450** (raw 0.2634, strict 0.3086); inference 0.7757, comparison 0.1472, " +
            "temporal 0.0326; abstains correctly on 51.8% of the nulls, the best abstention of the four. " +
            "**raptorboost - raptorcorpus = -0.0137 paper** (McNemar p=0.0073), -0.0022 raw (p=0.7016), " +
            "-0.0235 strict (p=0.0000). Promoting summaries into the result set makes the answer worse, " +
            "significantly so on two rules. Phase 6.2.4 fixed Boost so it could promote at all (#344); " +
            "this is the first measurement of what it does once it works, and the answer is that it " +
            "trades accuracy for abstention."),
    ];

    /// <summary>Asserts one arm's paper-rule accuracy reproduced what was last recorded, or records what it measured.</summary>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="arm">The arm, one of <see cref="AnswerArm.All"/>.</param>
    /// <param name="measuredAccuracy">The paper-rule accuracy over the judged queries.</param>
    /// <param name="output">Where a "nothing recorded yet" note goes.</param>
    public static void AssertReproduces(string datasetName, string arm, double measuredAccuracy, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var recorded = Find(datasetName, arm);
        if (recorded.Accuracy.Count == 0)
        {
            output.WriteLine(FormattableString.Invariant($"""
                 NO ANSWER REPRODUCTION RECORDED for {datasetName} / {arm}, so nothing was checked.
                 This run measured paper-rule accuracy = {measuredAccuracy:F4}.
                 Recorded instead: {recorded.Provenance}
                 If this run was the full one, it is the figure -- put it in {nameof(MultiHopRagAnswerReproduction)}
                 with the machine and the date, and the next run will be checked against it.
                """));
            return;
        }

        var reproduces = recorded.Accuracy.Any(a => Math.Abs(measuredAccuracy - a) <= Tolerance);
        Assert.True(
            reproduces,
            FormattableString.Invariant($"""
                {datasetName} / {arm} measured paper-rule accuracy {measuredAccuracy:F4}, outside ±{Tolerance} of
                every recorded figure ({string.Join(", ", recorded.Accuracy.Select(a => a.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)))}).
                Replayed from the answer cache this run is exact, so a difference is retrieval handing the model
                different context on this machine -- or the recorded figure is from another. Recorded: {recorded.Provenance}
                """));
    }

    /// <summary>Provokes the lookup for one pair and compares nothing.</summary>
    public static void RequireRecordedCase(string datasetName, string arm) => _ = Find(datasetName, arm);

    /// <summary>
    /// Whether an arm has at least one recorded figure for a dataset, as opposed to an entry that
    /// exists only so <see cref="RequireRecordedCase"/> and the unpinned-arm guard pass while the
    /// arm is wired up and unmeasured (an empty <see cref="Reproduction.Accuracy"/> array, like the
    /// four RAPTOR arms carry until Phase 6.2.1's sweep fills them in).
    /// </summary>
    /// <remarks>
    /// The default arm selection in <c>BeirGraphRagAnswerTests.SelectArms</c> calls this to skip an
    /// unmeasured arm from the canonical full run — an operator who names the arm explicitly
    /// through <see cref="BeirGraphRagAnswerTests.ArmsVariable"/> still gets it, empty pin or not.
    /// </remarks>
    /// <param name="datasetName">The dataset name.</param>
    /// <param name="arm">The arm, one of <see cref="AnswerArm.All"/>.</param>
    /// <returns>Whether the recorded entry carries at least one figure.</returns>
    public static bool HasRecordedFigure(string datasetName, string arm) => Find(datasetName, arm).Accuracy.Count > 0;

    private static Reproduction Find(string datasetName, string arm)
    {
        foreach (var reproduction in Reproductions)
        {
            if (string.Equals(reproduction.Dataset, datasetName, StringComparison.Ordinal)
                && string.Equals(reproduction.Arm, arm, StringComparison.Ordinal))
            {
                return reproduction;
            }
        }

        throw new InvalidOperationException(
            $"No answer reproduction is recorded for dataset '{datasetName}' under the {arm} arm. " +
            "An arm was added without anything pinning its figure; add an entry, empty if it has " +
            "never run, and the first full run fills it.");
    }

    private sealed record Reproduction(string Dataset, string Arm, IReadOnlyList<double> Accuracy, string Provenance);
}
