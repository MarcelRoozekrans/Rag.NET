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
            [],
            "NEVER RUN in full -- Phase 5.2.2 (design: docs/plans/2026-08-15-graphrag-answer-level-" +
            "evaluation.md), entry written before the first measurement. The dense arm: the Real " +
            "leg's 17,648 article chunks alone, top-6, one prompt, openai/gpt-4o-mini at " +
            "temperature 0, scored by the paper's any-shared-word rule over the 2,255 judged " +
            "queries. The full run replaces this text with the figure and the pilot that preceded it."),
        new(
            "multihop-rag",
            AnswerArm.Local,
            [],
            "NEVER RUN in full -- Phase 5.2.2. The GraphRAG local arm as shipped (PageRankWeight " +
            "0.3): the graph run's 321,151-unit store, dense top-500 through " +
            "GraphLocalSearchBehavior, top-6 of what it returns as context."),
        new(
            "multihop-rag",
            AnswerArm.Global,
            [],
            "NEVER RUN in full -- Phase 5.2.2. The GraphRAG global arm: GraphGlobalSearchBehavior's " +
            "map/reduce over the community reports (replayable since #241), its synthesised answer " +
            "first and the next five candidates behind it as context."),
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
