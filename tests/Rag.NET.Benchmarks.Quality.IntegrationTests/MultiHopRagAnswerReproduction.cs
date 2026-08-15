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
            "MEASURED 2026-08-15, same run. GraphRAG local search as shipped (PageRankWeight 0.3): " +
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
