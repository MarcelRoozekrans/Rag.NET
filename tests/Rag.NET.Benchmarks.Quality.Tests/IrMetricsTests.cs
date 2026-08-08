using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="IrMetrics"/> against arithmetic a reader can redo with a calculator.
/// <para>
/// This is the suite the whole phase rests on, and the reason is that a wrong nDCG does not look
/// wrong. A subtly incorrect implementation still lands inside the ±0.02 parity band around the
/// published SciFact figure of ≈ 0.645, the headline number reads as evidence, and every dataset
/// added afterwards inherits the defect. So every expected value below is written as the
/// expression that produces it rather than as a decimal literal someone copied out of a failing
/// run.
/// </para>
/// </summary>
public sealed class IrMetricsTests
{
    /// <summary>
    /// The four cases from the design, pinned to five decimal places. Loose enough for float
    /// arithmetic, tight enough that any of the classic mistakes moves the third digit.
    /// </summary>
    private const int Places = 5;

    [Fact]
    public void Ndcg_OneRelevantDocumentAtRankOne_IsExactlyOne()
    {
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["d1", "d2", "d3"], Binary("d1"), k: 10);

        Assert.Equal(1.0, score, Places);
    }

    [Fact]
    public void Ndcg_OneRelevantDocumentAtRankTwo_IsOneOverLogTwoOfThree()
    {
        // 1 / log2(3) ≈ 0.63093. A round number here — 0.5, or something near 0.28 — means IDCG was
        // summed over k = 10 assumed gains instead of over the one relevant document that exists.
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["d9", "d1", "d3"], Binary("d1"), k: 10);

        Assert.Equal(1.0 / Math.Log2(3), score, Places);
        Assert.Equal(0.63093, score, Places);
    }

    [Fact]
    public void Ndcg_TwoRelevantDocumentsAtRanksOneAndTwo_IsExactlyOne()
    {
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["d1", "d2", "d3"], Binary("d1", "d2"), k: 10);

        Assert.Equal(1.0, score, Places);
    }

    [Fact]
    public void Ndcg_TwoRelevantDocumentsAtRanksOneAndThree_IsTheRatioOfTwoGainSums()
    {
        // (1 + 1/log2(4)) / (1 + 1/log2(3)) ≈ 0.91972. IDCG has two gains because two documents are
        // relevant; DCG's second one sits a rank lower and is discounted further.
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["d1", "x", "d2"], Binary("d1", "d2"), k: 10);

        var expected = (1 + (1 / Math.Log2(4))) / (1 + (1 / Math.Log2(3)));

        Assert.Equal(expected, score, Places);
        Assert.Equal(0.91972, score, Places);
    }

    [Fact]
    public void Ndcg_IdealGainIsSummedOverTheRelevantDocumentsNotOverK()
    {
        // The trap, named with the number that makes it the common case rather than an edge case:
        // 277 of SciFact's 300 test queries have exactly ONE relevant document, so for 92% of the
        // dataset IDCG must equal exactly 1 whatever k is. Summing IDCG over k = 10 assumed gains
        // gives 4.5436, which would crush every one of those queries to roughly a fifth of its true
        // score — a uniform, plausible-looking collapse rather than a visible failure.
        var relevance = Binary("only");

        var atOne = IrMetrics.NormalizedDiscountedCumulativeGain(["only"], relevance, k: 1);
        var atTen = IrMetrics.NormalizedDiscountedCumulativeGain(["only"], relevance, k: 10);
        var atHundred = IrMetrics.NormalizedDiscountedCumulativeGain(["only"], relevance, k: 100);

        Assert.Equal(1.0, atOne, Places);
        Assert.Equal(1.0, atTen, Places);
        Assert.Equal(1.0, atHundred, Places);
    }

    [Fact]
    public void Ndcg_MoreRelevantDocumentsThanK_CapsTheIdealAtK()
    {
        // The other side of min(|relevant|, k): with three relevant documents and k = 2 the ideal is
        // the best two, so retrieving two of them at the top two ranks is a perfect score. Dividing
        // by an ideal over all three would report 0.63 for a run that could not have done better.
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["d1", "d2", "zzz"], Binary("d1", "d2", "d3"), k: 2);

        Assert.Equal(1.0, score, Places);
    }

    [Fact]
    public void Ndcg_GradedRelevance_UsesTwoToThePowerOfTheGradeMinusOne()
    {
        // SciFact is binary, but FiQA and TREC-COVID are not, and a binary-only gain function would
        // silently misrank them rather than fail. Gain is 2^rel - 1: grade 2 is worth 3, grade 1 is
        // worth 1. Putting the grade-1 document first is therefore NOT a perfect run.
        //
        //   DCG  = 1/log2(2) + 3/log2(3) = 1 + 1.89279 = 2.89279
        //   IDCG = 3/log2(2) + 1/log2(3) = 3 + 0.63093 = 3.63093
        var relevance = Graded(("high", 2), ("low", 1));

        var wrongOrder = IrMetrics.NormalizedDiscountedCumulativeGain(["low", "high"], relevance, k: 10);
        var idealOrder = IrMetrics.NormalizedDiscountedCumulativeGain(["high", "low"], relevance, k: 10);

        var expected = (1 + (3 / Math.Log2(3))) / (3 + (1 / Math.Log2(3)));

        Assert.Equal(expected, wrongOrder, Places);
        Assert.Equal(0.79671, wrongOrder, Places);
        Assert.Equal(1.0, idealOrder, Places);
    }

    [Fact]
    public void Ndcg_TreatingGradedRelevanceAsBinaryWouldScoreBothOrdersTheSame()
    {
        // Guards the guard above. If gain were `rel > 0 ? 1 : 0`, both orders would score 1.0 and
        // the graded test would still pass on its ideal-order assertion alone.
        var relevance = Graded(("high", 3), ("low", 1));

        var wrongOrder = IrMetrics.NormalizedDiscountedCumulativeGain(["low", "high"], relevance, k: 10);

        Assert.True(
            wrongOrder < 1.0,
            $"Ranking a grade-1 document above a grade-3 one scored {wrongOrder}. A gain function " +
            "that collapses grades to a boolean cannot tell these two orders apart.");
    }

    [Fact]
    public void Ndcg_NothingRelevantRetrieved_IsZero()
    {
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["a", "b", "c"], Binary("d1"), k: 10);

        Assert.Equal(0.0, score, Places);
    }

    [Fact]
    public void Ndcg_RelevantDocumentBelowTheCutoff_IsZero()
    {
        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["a", "b", "d1"], Binary("d1"), k: 2);

        Assert.Equal(0.0, score, Places);
    }

    [Fact]
    public void Ndcg_ZeroGradedJudgementsCountAsIrrelevant()
    {
        // BEIR qrels files carry explicit 0 rows. A 0 is a judgement that the document is NOT
        // relevant, so it must neither earn gain nor enlarge the ideal.
        var relevance = Graded(("judged-irrelevant", 0), ("relevant", 1));

        var score = IrMetrics.NormalizedDiscountedCumulativeGain(
            ["judged-irrelevant", "relevant"], relevance, k: 10);

        Assert.Equal(1.0 / Math.Log2(3), score, Places);
    }

    [Fact]
    public void Recall_DenominatorIsEveryRelevantDocumentNotJustTheOnesThatCouldFit()
    {
        // The exact opposite of IDCG's min(|relevant|, k), which is why these get separate tests.
        // Four relevant documents and k = 2: retrieving both of the two that fit is Recall@2 = 0.5,
        // not 1.0. Reusing IDCG's capped denominator here would report perfect recall for a run
        // that found half the answer.
        var recall = IrMetrics.Recall(["d1", "d2", "d3"], Binary("d1", "d2", "d3", "d4"), k: 2);

        Assert.Equal(0.5, recall, Places);
    }

    [Fact]
    public void Recall_AllRelevantDocumentsInsideTheCutoff_IsOne()
    {
        var recall = IrMetrics.Recall(["x", "d1", "d2", "y"], Binary("d1", "d2"), k: 10);

        Assert.Equal(1.0, recall, Places);
    }

    [Fact]
    public void Recall_IsIndifferentToTheOrderInsideTheCutoff()
    {
        var first = IrMetrics.Recall(["d1", "d2", "x"], Binary("d1", "d2"), k: 3);
        var second = IrMetrics.Recall(["x", "d2", "d1"], Binary("d1", "d2"), k: 3);

        Assert.Equal(first, second, Places);
        Assert.Equal(1.0, first, Places);
    }

    [Fact]
    public void Recall_CountsGradedJudgementsAboveZeroAsRelevant()
    {
        var recall = IrMetrics.Recall(["a", "b"], Graded(("a", 3), ("b", 1), ("c", 0)), k: 10);

        Assert.Equal(1.0, recall, Places);
    }

    [Fact]
    public void Recall_NothingRetrieved_IsZero()
    {
        Assert.Equal(0.0, IrMetrics.Recall([], Binary("d1"), k: 10), Places);
    }

    [Fact]
    public void ReciprocalRank_IsOneOverTheRankOfTheFirstRelevantDocument()
    {
        Assert.Equal(1.0, IrMetrics.ReciprocalRank(["d1", "x"], Binary("d1"), k: 10), Places);
        Assert.Equal(0.5, IrMetrics.ReciprocalRank(["x", "d1"], Binary("d1"), k: 10), Places);
        Assert.Equal(1.0 / 3, IrMetrics.ReciprocalRank(["x", "y", "d1"], Binary("d1"), k: 10), Places);
    }

    [Fact]
    public void ReciprocalRank_IgnoresRelevantDocumentsAfterTheFirst()
    {
        var rank = IrMetrics.ReciprocalRank(["x", "d1", "d2"], Binary("d1", "d2"), k: 10);

        Assert.Equal(0.5, rank, Places);
    }

    [Fact]
    public void ReciprocalRank_FirstRelevantDocumentBelowTheCutoff_IsZero()
    {
        Assert.Equal(0.0, IrMetrics.ReciprocalRank(["x", "y", "d1"], Binary("d1"), k: 2), Places);
    }

    [Fact]
    public void Evaluate_QueriesAbsentFromQrelsAreExcludedRatherThanScoredZero()
    {
        // The trap, named with the numbers that make it decisive rather than cosmetic. SciFact ships
        // 1,109 queries and its test qrels judge 300 of them. Scoring the other 809 as zero divides
        // the mean by roughly 3.7 — about 0.17 instead of 0.645 — which reads as catastrophic
        // retrieval failure, not as a harness bug, and would send whoever sees it into the retrieval
        // path looking for a fault that is not there.
        //
        // Two perfect judged queries and two unjudged ones: the mean must be 1.0, not 0.5.
        var runs = Runs(
            ("judged-1", new[] { "d1" }),
            ("judged-2", new[] { "d2" }),
            ("unjudged-1", new[] { "whatever" }),
            ("unjudged-2", new[] { "whatever" }));

        var qrels = Qrels(("judged-1", Binary("d1")), ("judged-2", Binary("d2")));

        var evaluation = IrMetrics.Evaluate(runs, qrels, k: 10);

        Assert.Equal(1.0, evaluation.NormalizedDiscountedCumulativeGain, Places);
        Assert.Equal(1.0, evaluation.MeanReciprocalRank, Places);
        Assert.Equal(2, evaluation.EvaluatedQueryCount);
        Assert.Equal(2, evaluation.ExcludedQueryCount);
    }

    [Fact]
    public void Evaluate_AveragesOverJudgedQueriesOnly()
    {
        // One perfect judged query, one that puts its relevant document at rank 2, and an unjudged
        // one. The mean is (1 + 1/log2(3)) / 2, over two queries — the third never enters the sum
        // or the divisor.
        var runs = Runs(
            ("q1", new[] { "d1" }),
            ("q2", new[] { "x", "d2" }),
            ("q3", new[] { "x" }));

        var qrels = Qrels(("q1", Binary("d1")), ("q2", Binary("d2")));

        var evaluation = IrMetrics.Evaluate(runs, qrels, k: 10);

        Assert.Equal((1 + (1 / Math.Log2(3))) / 2, evaluation.NormalizedDiscountedCumulativeGain, Places);
        Assert.Equal(2, evaluation.EvaluatedQueryCount);
        Assert.Equal(1, evaluation.ExcludedQueryCount);
    }

    [Fact]
    public void Evaluate_AJudgedQueryWithNoRunScoresZeroRatherThanBeingExcluded()
    {
        // The mirror image of the exclusion rule, and the one that would inflate the number instead
        // of deflating it. A query that IS judged but that retrieval returned nothing for is a
        // retrieval failure worth 0, not an unjudged query worth skipping. Dropping it would let a
        // harness that lost half its queries still report a healthy mean.
        var runs = Runs(("q1", new[] { "d1" }));
        var qrels = Qrels(("q1", Binary("d1")), ("q2", Binary("d2")));

        var evaluation = IrMetrics.Evaluate(runs, qrels, k: 10);

        Assert.Equal(0.5, evaluation.NormalizedDiscountedCumulativeGain, Places);
        Assert.Equal(2, evaluation.EvaluatedQueryCount);
        Assert.Equal(0, evaluation.ExcludedQueryCount);
    }

    [Fact]
    public void Evaluate_AQrelsEntryWithNoPositiveJudgementIsNotAJudgedQuery()
    {
        // A qrels row of 0 is present but says nothing is relevant, so nDCG has no ideal to divide
        // by. Counting such a query as judged would score it 0 and dilute the mean exactly the way
        // the 809 unjudged queries would.
        var runs = Runs(("q1", new[] { "d1" }), ("q2", new[] { "x" }));
        var qrels = Qrels(("q1", Binary("d1")), ("q2", Graded(("d2", 0))));

        var evaluation = IrMetrics.Evaluate(runs, qrels, k: 10);

        Assert.Equal(1.0, evaluation.NormalizedDiscountedCumulativeGain, Places);
        Assert.Equal(1, evaluation.EvaluatedQueryCount);
        Assert.Equal(1, evaluation.ExcludedQueryCount);
    }

    [Fact]
    public void Evaluate_ReportsTheCutoffItUsed()
    {
        var evaluation = IrMetrics.Evaluate(
            Runs(("q1", new[] { "d1" })), Qrels(("q1", Binary("d1"))), k: 10);

        Assert.Equal(10, evaluation.Cutoff);
    }

    [Fact]
    public void Evaluate_NoJudgedQueryAtAll_ThrowsRatherThanReportingZero()
    {
        // A mean over nothing is 0/0. Returning 0 would look like total retrieval failure; throwing
        // says the qrels split was never loaded.
        var runs = Runs(("q1", new[] { "d1" }));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IrMetrics.Evaluate(runs, Qrels(), k: 10));

        Assert.Contains("qrels", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_MeanRecallAndMrrAreAveragedOverTheSameJudgedQueries()
    {
        var runs = Runs(
            ("q1", new[] { "d1", "x", "d2" }),
            ("q2", new[] { "x", "y", "z" }));

        var qrels = Qrels(("q1", Binary("d1", "d2")), ("q2", Binary("d3")));

        var evaluation = IrMetrics.Evaluate(runs, qrels, k: 10);

        // q1 finds both of two relevant documents, q2 finds none of one: mean recall = 0.5.
        Assert.Equal(0.5, evaluation.Recall, Places);
        // q1's first relevant document is at rank 1, q2 has none: mean reciprocal rank = 0.5.
        Assert.Equal(0.5, evaluation.MeanReciprocalRank, Places);
    }

    [Fact]
    public void Metrics_RejectANonPositiveCutoff()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IrMetrics.NormalizedDiscountedCumulativeGain(["d1"], Binary("d1"), k: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IrMetrics.Recall(["d1"], Binary("d1"), k: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IrMetrics.ReciprocalRank(["d1"], Binary("d1"), k: -1));
    }

    // ---------------------------------------------------------------------------
    // Precision@k and average precision — Phase 5.4. Each of the three denominator
    // decisions gets a case that fails under the alternative, per the design.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Precision_DividesByK_NotByWhatWasRetrieved()
    {
        // The whole run is 3 documents, all relevant, evaluated at k = 10.
        // /k gives 3/10 = 0.3. Dividing by min(k, retrieved) would give 3/3 = 1.0 —
        // perfect precision from a system that returned almost nothing.
        var score = IrMetrics.Precision(["d1", "d2", "d3"], Binary("d1", "d2", "d3"), k: 10);

        Assert.Equal(0.3, score, Places);
    }

    [Fact]
    public void Precision_CountsOnlyWithinTheCutoff()
    {
        // Relevant at ranks 1 and 5; k = 3 sees only the first. 1/3 = 0.33333.
        var score = IrMetrics.Precision(["d1", "x", "y", "z", "d2"], Binary("d1", "d2"), k: 3);

        Assert.Equal(1.0 / 3.0, score, Places);
    }

    [Fact]
    public void Precision_TreatsAGradeOfZeroAsIrrelevant()
    {
        // d2 is judged 0 — a statement that it does not answer the query, not an absence
        // of judgement. Only d1 counts: 1/2 = 0.5.
        var score = IrMetrics.Precision(["d1", "d2"], Graded(("d1", 3), ("d2", 0)), k: 2);

        Assert.Equal(0.5, score, Places);
    }

    [Fact]
    public void AveragePrecision_DividesByMinOfKAndRelevant_SoAPerfectRankingReachesOne()
    {
        // 20 relevant documents, k = 10, and the top 10 are all relevant.
        // Precision is 1.0 at every one of those ranks, so the sum is 10.
        // min(k, |relevant|) = 10 gives 10/10 = 1.0.
        // Dividing by |relevant| = 20 would give 0.5 — unreachable perfection.
        var relevant = Enumerable.Range(1, 20).Select(i => $"d{i}").ToArray();
        var ranked = relevant.Take(10).ToArray();

        var score = IrMetrics.AveragePrecision(ranked, Binary(relevant), k: 10);

        Assert.Equal(1.0, score, Places);
    }

    [Fact]
    public void AveragePrecision_AveragesPrecisionAtEachRelevantRank()
    {
        // Relevant at ranks 1 and 3 of ["d1", "x", "d2"], two relevant judged.
        //   rank 1: 1st relevant found, precision 1/1 = 1.0
        //   rank 3: 2nd relevant found, precision 2/3 = 0.66667
        // sum = 1.66667, divided by min(10, 2) = 2 -> 0.83333
        var score = IrMetrics.AveragePrecision(["d1", "x", "d2"], Binary("d1", "d2"), k: 10);

        Assert.Equal((1.0 + 2.0 / 3.0) / 2.0, score, Places);
    }

    [Fact]
    public void AveragePrecision_NothingRelevantJudged_IsZero()
    {
        // No positive judgement: there is nothing to average. Evaluate excludes such a
        // query from the mean rather than letting this zero into it.
        var score = IrMetrics.AveragePrecision(["d1"], Graded(("d1", 0)), k: 10);

        Assert.Equal(0, score, Places);
    }

    [Fact]
    public void Evaluate_PrecisionAndMap_UseTheSameJudgedQueryExclusionAsTheOthers()
    {
        // q1 is judged and retrieved perfectly. q2 is judged but retrieved nothing —
        // a real zero that belongs in the mean. q3 has only a zero grade, so it is
        // excluded entirely and must not divide anything.
        var runs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["q1"] = ["d1"],
        };
        var qrels = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
        {
            ["q1"] = Binary("d1"),
            ["q2"] = Binary("d9"),
            ["q3"] = Graded(("d5", 0)),
        };

        var evaluation = IrMetrics.Evaluate(runs, qrels, k: 10);

        // Two evaluated queries, not three: q3 carries no positive judgement.
        Assert.Equal(2, evaluation.EvaluatedQueryCount);
        // q1 precision = 1/10 = 0.1 (one relevant document, denominator k), q2 = 0.
        Assert.Equal(0.05, evaluation.Precision, Places);
        // q1 AP = 1.0 (perfect at min(10, 1) = 1), q2 = 0. Mean = 0.5.
        Assert.Equal(0.5, evaluation.MeanAveragePrecision, Places);
    }

    private static IReadOnlyDictionary<string, int> Binary(params string[] relevantDocumentIds)
    {
        var relevance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ref readonly var id in relevantDocumentIds.AsSpan())
        {
            relevance[id] = 1;
        }

        return relevance;
    }

    private static IReadOnlyDictionary<string, int> Graded(params (string DocumentId, int Relevance)[] judgements)
    {
        var relevance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ref readonly var judgement in judgements.AsSpan())
        {
            relevance[judgement.DocumentId] = judgement.Relevance;
        }

        return relevance;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Runs(
        params (string QueryId, string[] RankedDocumentIds)[] runs)
    {
        var byQuery = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (ref readonly var run in runs.AsSpan())
        {
            byQuery[run.QueryId] = run.RankedDocumentIds;
        }

        return byQuery;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Qrels(
        params (string QueryId, IReadOnlyDictionary<string, int> Relevance)[] judgements)
    {
        var byQuery = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (ref readonly var judgement in judgements.AsSpan())
        {
            byQuery[judgement.QueryId] = judgement.Relevance;
        }

        return byQuery;
    }
}
