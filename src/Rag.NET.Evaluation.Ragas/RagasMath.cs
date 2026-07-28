using Rag.NET.Evaluation.Ragas.Judging;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// The RAGAS score formulas as pure functions over judgement arrays.
/// </summary>
/// <remarks>
/// Separated from the evaluators so metric fidelity can be pinned by table tests with no LLM
/// involved. The arithmetic is the part that was definitionally wrong before Phase 3.1; keeping
/// it callable without mock choreography is what makes that verifiable.
/// </remarks>
internal static class RagasMath
{
    /// <summary>
    /// Rank-aware average precision: <c>Σ(P@k × rel_k) / total_relevant</c>.
    /// </summary>
    /// <remarks>
    /// Rank-aware on purpose. Plain <c>relevant / total</c> scores a retriever that returns the
    /// gold chunk first identically to one that returns it last, which is exactly the
    /// discrimination this metric exists to provide.
    /// </remarks>
    /// <param name="relevanceByRank">Relevance judgements in retrieved order.</param>
    public static double AveragePrecision(ReadOnlySpan<bool> relevanceByRank)
    {
        var relevantSoFar = 0;
        var precisionSum = 0.0;

        for (var i = 0; i < relevanceByRank.Length; i++)
        {
            if (!relevanceByRank[i])
                continue;

            relevantSoFar++;
            precisionSum += relevantSoFar / (double)(i + 1);
        }

        return relevantSoFar == 0 ? 0.0 : precisionSum / relevantSoFar;
    }

    /// <summary>Fraction of items judged supported.</summary>
    /// <param name="supported">Items judged supported.</param>
    /// <param name="total">Items judged at all.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="total"/> is zero. The caller decides what "nothing to judge" means —
    /// returning 1.0 here is the defect this phase removes.
    /// </exception>
    public static double SupportedFraction(int supported, int total)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(total, 0, nameof(total));
        return supported / (double)total;
    }

    /// <summary>
    /// Fraction of <see cref="Verdict.Yes"/> among the verdicts that could be read at all, or
    /// <c>null</c> when none could.
    /// </summary>
    /// <remarks>
    /// Shared by Faithfulness and Context Recall, which are the same arithmetic over different
    /// material. They each carried their own copy before Phase 3.1, which is how the same
    /// parse-failure defect came to exist twice.
    /// <para>
    /// An unparseable verdict is excluded from the denominator rather than counted as a "no":
    /// counting it would state that the model denied something it never answered.
    /// </para>
    /// </remarks>
    /// <param name="verdicts">The judgements obtained, in any order.</param>
    public static double? ScoreFromVerdicts(IReadOnlyList<Verdict> verdicts)
    {
        var supported = 0;
        var readable = 0;
        for (var i = 0; i < verdicts.Count; i++)
        {
            if (verdicts[i] == Verdict.Unparseable)
                continue;

            readable++;
            if (verdicts[i] == Verdict.Yes)
                supported++;
        }

        return readable == 0 ? null : SupportedFraction(supported, readable);
    }

    /// <summary>
    /// <see cref="AveragePrecision"/> over the verdicts that could be read at all, in retrieved
    /// order, or <c>null</c> when none could.
    /// </summary>
    /// <remarks>
    /// An unparseable verdict is dropped from the ranking rather than treated as irrelevant: the
    /// model returned no judgement for that chunk, so the chunk is excluded from the ranking
    /// entirely — scored as though it was never retrieved — rather than counted as a miss.
    /// <para>
    /// Dropping it does promote every later chunk: for <c>[unparseable, yes]</c> the gold chunk
    /// becomes rank 1 and the sample scores <c>1.0</c>, where counting the unparseable one as
    /// irrelevant would leave the gold chunk at rank 2 and score <c>0.5</c>. That promotion is
    /// the intended reading. The alternative charges the retriever for the model's failure to
    /// answer, which is the same fabrication the tri-state verdicts exist to prevent — see
    /// <see cref="ScoreFromVerdicts"/>, which excludes such a verdict from its denominator for
    /// exactly the same reason.
    /// </para>
    /// </remarks>
    /// <param name="verdicts">The relevance judgements, in the order the chunks were retrieved.</param>
    public static double? PrecisionFromVerdicts(IReadOnlyList<Verdict> verdicts)
    {
        var relevanceByRank = new bool[verdicts.Count];
        var readable = 0;
        for (var i = 0; i < verdicts.Count; i++)
        {
            if (verdicts[i] == Verdict.Unparseable)
                continue;

            relevanceByRank[readable++] = verdicts[i] == Verdict.Yes;
        }

        return readable == 0 ? null : AveragePrecision(relevanceByRank.AsSpan(0, readable));
    }

    /// <summary>Clamps a raw similarity to the documented [0, 1] score range.</summary>
    /// <param name="raw">The unconstrained value, such as a cosine similarity over [-1, 1].</param>
    public static double ClampScore(double raw) => Math.Clamp(raw, 0.0, 1.0);
}
