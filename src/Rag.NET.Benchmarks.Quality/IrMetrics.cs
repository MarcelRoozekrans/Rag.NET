namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Native information-retrieval metrics — nDCG@k, Recall@k and reciprocal rank — computed over
/// <b>document</b> rankings and TREC-style relevance judgements.
/// <para>
/// Native rather than delegated to <c>pytrec_eval</c> or a NuGet equivalent, and pinned in
/// <c>IrMetricsTests</c> against values a reader can redo with a calculator, because a subtly wrong
/// nDCG does not announce itself: it still lands inside the ±0.02 band around SciFact's published
/// ≈ 0.645, the headline number then reads as evidence, and every dataset added afterwards inherits
/// the defect.
/// </para>
/// <para>
/// Three conventions carry all the risk, and each is the subject of its own test:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>IDCG is summed over <c>min(|relevant|, k)</c> ideal gains, never over <c>k</c>.</b> 277 of
///     SciFact's 300 judged test queries have exactly one relevant document, so for 92% of that
///     dataset IDCG must equal exactly 1 no matter what <c>k</c> is. Summing ten assumed gains
///     instead gives 4.5436 and scales almost every query down by the same factor — a uniform,
///     plausible-looking collapse rather than a visible failure.
///   </item>
///   <item>
///     <b>Only queries with a positive relevance judgement are averaged.</b> SciFact ships 1,109
///     queries and judges 300 of them in the test split. Scoring the remaining 809 as zero divides
///     the mean by roughly 3.7 — about 0.17 instead of 0.645 — which reads as catastrophic retrieval
///     failure rather than as a harness bug.
///   </item>
///   <item>
///     <b>Gain is <c>2^rel - 1</c>.</b> SciFact is binary, where that reduces to 1, but FiQA and
///     TREC-COVID are graded and a boolean gain would silently misrank them instead of failing.
///   </item>
/// </list>
/// <para>
/// Recall@k's denominator is <c>|relevant|</c> — the exact opposite of IDCG's cap. Four relevant
/// documents at <c>k = 2</c> means Recall@2 can be at most 0.5, while nDCG@2 can still be 1.0. The
/// two rules look interchangeable and are not, which is why they are tested separately.
/// </para>
/// </summary>
public static class IrMetrics
{
    /// <summary>
    /// Normalised discounted cumulative gain at <paramref name="k"/> for one query.
    /// </summary>
    /// <param name="rankedDocumentIds">
    /// The retrieved document ids, best first, already aggregated to document level and deduplicated
    /// by <c>DocumentRanking</c>. Ranking chunks here computes a different quantity that merely
    /// resembles nDCG@k.
    /// </param>
    /// <param name="relevance">
    /// This query's judgements: document id to relevance grade. Grades of zero and documents absent
    /// from the map are equally irrelevant — a BEIR qrels row of 0 is a judgement that the document
    /// does not answer the query, so it must neither earn gain nor enlarge the ideal.
    /// </param>
    /// <param name="k">The rank cutoff. Must be positive.</param>
    /// <returns>
    /// A value in <c>[0, 1]</c>, or 0 when nothing relevant was judged — there is no ideal to divide
    /// by, and <see cref="Evaluate"/> excludes such a query from the mean rather than letting this
    /// zero into it.
    /// </returns>
    public static double NormalizedDiscountedCumulativeGain(
        IReadOnlyList<string> rankedDocumentIds,
        IReadOnlyDictionary<string, int> relevance,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedDocumentIds);
        ArgumentNullException.ThrowIfNull(relevance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var ideal = IdealDiscountedCumulativeGain(relevance, k);
        if (ideal <= 0)
        {
            return 0;
        }

        var limit = Math.Min(k, rankedDocumentIds.Count);
        double discounted = 0;
        for (var rank = 0; rank < limit; rank++)
        {
            var grade = GradeOf(rankedDocumentIds[rank], relevance);
            if (grade > 0)
            {
                discounted += Gain(grade) / Discount(rank);
            }
        }

        return discounted / ideal;
    }

    /// <summary>
    /// The fraction of this query's relevant documents that appear in the first <paramref name="k"/>
    /// ranks.
    /// </summary>
    /// <param name="rankedDocumentIds">The retrieved document ids, best first.</param>
    /// <param name="relevance">This query's judgements: document id to relevance grade.</param>
    /// <param name="k">The rank cutoff. Must be positive.</param>
    /// <returns>A value in <c>[0, 1]</c>, or 0 when nothing relevant was judged.</returns>
    /// <remarks>
    /// The denominator is every relevant document, <b>not</b> <c>min(|relevant|, k)</c>. Capping it
    /// the way IDCG is capped would report perfect recall for a run that retrieved half the answer
    /// because the other half could not fit inside the cutoff.
    /// </remarks>
    public static double Recall(
        IReadOnlyList<string> rankedDocumentIds,
        IReadOnlyDictionary<string, int> relevance,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedDocumentIds);
        ArgumentNullException.ThrowIfNull(relevance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var relevantCount = CountRelevant(relevance);
        if (relevantCount == 0)
        {
            return 0;
        }

        var limit = Math.Min(k, rankedDocumentIds.Count);
        var found = 0;
        for (var rank = 0; rank < limit; rank++)
        {
            if (GradeOf(rankedDocumentIds[rank], relevance) > 0)
            {
                found++;
            }
        }

        return (double)found / relevantCount;
    }

    /// <summary>
    /// One over the rank of the first relevant document, or 0 when none appears within
    /// <paramref name="k"/>. Averaged over queries this is MRR@k.
    /// </summary>
    /// <param name="rankedDocumentIds">The retrieved document ids, best first.</param>
    /// <param name="relevance">This query's judgements: document id to relevance grade.</param>
    /// <param name="k">The rank cutoff. Must be positive.</param>
    /// <returns>A value in <c>[0, 1]</c>.</returns>
    /// <remarks>
    /// Ranks are one-based here, as in every published MRR: a relevant document first is 1.0,
    /// second is 0.5, third is 1/3.
    /// </remarks>
    public static double ReciprocalRank(
        IReadOnlyList<string> rankedDocumentIds,
        IReadOnlyDictionary<string, int> relevance,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedDocumentIds);
        ArgumentNullException.ThrowIfNull(relevance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var limit = Math.Min(k, rankedDocumentIds.Count);
        for (var rank = 0; rank < limit; rank++)
        {
            if (GradeOf(rankedDocumentIds[rank], relevance) > 0)
            {
                return 1.0 / (rank + 1);
            }
        }

        return 0;
    }

    /// <summary>
    /// Averages every metric over the queries <paramref name="qrels"/> actually judges.
    /// </summary>
    /// <param name="runs">Document rankings by query id, best first.</param>
    /// <param name="qrels">Relevance judgements by query id, then by document id.</param>
    /// <param name="k">The rank cutoff. Must be positive.</param>
    /// <returns>The means, the cutoff, and the query counts on both sides of the exclusion rule.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="qrels"/> judges nothing, so every mean would be <c>0/0</c>. Returning zero
    /// would look like total retrieval failure; this says the qrels split was never loaded.
    /// </exception>
    /// <remarks>
    /// The loop runs over the <b>judged</b> queries, not over <paramref name="runs"/>, and that
    /// direction is load-bearing in both ways. A query in <paramref name="runs"/> with no positive
    /// judgement is excluded — the 809-of-1,109 case, where scoring zero would read as a retrieval
    /// collapse. A judged query missing from <paramref name="runs"/> scores zero and stays in the
    /// divisor: retrieval returned nothing for a query that has an answer, and dropping it would let
    /// a harness that lost half its queries still report a healthy mean.
    /// </remarks>
    public static IrEvaluation Evaluate(
        IReadOnlyDictionary<string, IReadOnlyList<string>> runs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> qrels,
        int k)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(qrels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        double ndcg = 0;
        double recall = 0;
        double reciprocalRank = 0;
        var evaluated = 0;
        var judgedWithARun = 0;

        foreach (var (queryId, relevance) in qrels)
        {
            if (CountRelevant(relevance) == 0)
            {
                continue;
            }

            evaluated++;
            if (runs.TryGetValue(queryId, out var ranked))
            {
                judgedWithARun++;
            }
            else
            {
                // Judged, retrieved nothing: a zero that belongs in the mean.
                ranked = [];
            }

            ndcg += NormalizedDiscountedCumulativeGain(ranked, relevance, k);
            recall += Recall(ranked, relevance, k);
            reciprocalRank += ReciprocalRank(ranked, relevance, k);
        }

        if (evaluated == 0)
        {
            throw new InvalidOperationException(
                $"None of the {qrels.Count} qrels entries carries a positive relevance judgement, so " +
                "every mean would be 0/0. This almost always means the qrels split was not loaded, or " +
                "was loaded from the wrong file — not that retrieval failed.");
        }

        return new IrEvaluation(
            ndcg / evaluated,
            recall / evaluated,
            reciprocalRank / evaluated,
            k,
            evaluated,
            runs.Count - judgedWithARun);
    }

    /// <summary>
    /// The best DCG@k any ranking of these judgements could achieve: the highest
    /// <c>min(|relevant|, k)</c> gains, in descending order, each at the best rank still available.
    /// </summary>
    /// <remarks>
    /// The binary path is separated out because it is SciFact's shape — 277 of 300 judged queries
    /// have one relevant document, and none has more than five — so the overwhelmingly common case
    /// needs no array and no sort. It is also the case the cap matters most for: one relevant
    /// document must give an ideal of exactly 1.
    /// </remarks>
    private static double IdealDiscountedCumulativeGain(IReadOnlyDictionary<string, int> relevance, int k)
    {
        var relevantCount = 0;
        var anyGradeAboveOne = false;
        foreach (var grade in relevance.Values)
        {
            if (grade <= 0)
            {
                continue;
            }

            relevantCount++;
            anyGradeAboveOne |= grade > 1;
        }

        var limit = Math.Min(relevantCount, k);
        if (!anyGradeAboveOne)
        {
            double binary = 0;
            for (var rank = 0; rank < limit; rank++)
            {
                binary += 1 / Discount(rank);
            }

            return binary;
        }

        return GradedIdealDiscountedCumulativeGain(relevance, relevantCount, limit);
    }

    /// <summary>
    /// The graded ideal: positive grades sorted descending, the best <paramref name="limit"/> of
    /// them placed at ranks 1..limit.
    /// </summary>
    /// <remarks>
    /// A separate method rather than a branch inside the loop of
    /// <see cref="IdealDiscountedCumulativeGain"/>: sorting is what this case needs and what the
    /// binary case must not pay for, and ZA0601 rejects an ordering operation written inside a loop
    /// body regardless.
    /// </remarks>
    private static double GradedIdealDiscountedCumulativeGain(
        IReadOnlyDictionary<string, int> relevance, int relevantCount, int limit)
    {
        var grades = new int[relevantCount];
        var next = 0;
        foreach (var grade in relevance.Values)
        {
            if (grade > 0)
            {
                grades[next++] = grade;
            }
        }

        // Ascending, then walked from the end: descending order without allocating a comparer.
        Array.Sort(grades);

        double ideal = 0;
        for (var rank = 0; rank < limit; rank++)
        {
            ideal += Gain(grades[relevantCount - 1 - rank]) / Discount(rank);
        }

        return ideal;
    }

    private static int CountRelevant(IReadOnlyDictionary<string, int> relevance)
    {
        var count = 0;
        foreach (var grade in relevance.Values)
        {
            if (grade > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static int GradeOf(string documentId, IReadOnlyDictionary<string, int> relevance) =>
        relevance.TryGetValue(documentId, out var grade) ? grade : 0;

    /// <summary>
    /// TREC's exponential gain, <c>2^rel - 1</c>. Binary judgements reduce to 1, so SciFact is
    /// unaffected; graded ones are not linear in the grade, which is the whole point.
    /// </summary>
    private static double Gain(int relevance) => Math.Pow(2, relevance) - 1;

    /// <summary>
    /// The rank discount <c>log2(rank + 2)</c> for a zero-based rank — that is, <c>log2(1 + rank)</c>
    /// over one-based ranks, so rank 1 divides by 1 and rank 2 by log2(3).
    /// </summary>
    private static double Discount(int zeroBasedRank) => Math.Log2(zeroBasedRank + 2);
}
