namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The result of one <see cref="IrMetrics.Evaluate"/> run: the means, the cutoff they were computed
/// at, and the query counts on both sides of the qrels exclusion rule.
/// </summary>
/// <param name="NormalizedDiscountedCumulativeGain">
/// Mean nDCG@<paramref name="Cutoff"/> over the judged queries. This is the number compared against
/// a published reference.
/// </param>
/// <param name="Recall">Mean Recall@<paramref name="Cutoff"/> over the judged queries.</param>
/// <param name="MeanReciprocalRank">
/// Mean reciprocal rank of the first relevant document within <paramref name="Cutoff"/>.
/// </param>
/// <param name="Cutoff">
/// The rank cutoff. Carried on the result so a reported figure cannot be mistaken for nDCG@10 when
/// it was computed at some other <c>k</c>.
/// </param>
/// <param name="EvaluatedQueryCount">
/// How many queries entered the means. Worth asserting against the dataset's expected split size:
/// SciFact's test qrels judge 300 queries, and a run reporting far fewer is scoring a subset while
/// looking healthy.
/// </param>
/// <param name="ExcludedQueryCount">
/// How many of the supplied runs were skipped for having no positive relevance judgement. Reported
/// rather than swallowed because a surprisingly large number here is the visible symptom of loading
/// the wrong qrels split — SciFact ships 1,109 queries against 300 judged ones, so a run supplying
/// every query against the test split would show 809. The BEIR harness no longer produces such
/// runs: it retrieves only for judged queries, so its figure here is 0 unless a judged query
/// carries nothing but zero-grade judgements.
/// </param>
public sealed record IrEvaluation(
    double NormalizedDiscountedCumulativeGain,
    double Recall,
    double MeanReciprocalRank,
    double Precision,
    double MeanAveragePrecision,
    int Cutoff,
    int EvaluatedQueryCount,
    int ExcludedQueryCount);
