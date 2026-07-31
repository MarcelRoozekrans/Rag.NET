namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The published nDCG@10 one dataset's parity run is measured against, the band around it, and where
/// the figure came from.
/// </summary>
/// <remarks>
/// <para>
/// This lives on the dataset rather than in a test because the figure <b>belongs</b> to the dataset:
/// a target hard-coded in a test file forces one test file per dataset, and the second dataset is
/// then a copy of the first with three constants edited — which is how a band ends up widened to fit
/// a number instead of a number investigated for leaving the band.
/// </para>
/// <para>
/// The figure is per model. Everything recorded here is for <c>all-MiniLM-L6-v2</c> at
/// <c>max_seq_length = 256</c>; swapping the embedder invalidates every target in the harness.
/// </para>
/// </remarks>
/// <param name="PublishedNdcgAt10">The published nDCG@10 for the dataset.</param>
/// <param name="Source">
/// Where <paramref name="PublishedNdcgAt10"/> was read from, recorded rather than assumed. MTEB's
/// leaderboard and the BEIR paper do not always agree, so "which number" is only meaningful
/// alongside "from where".
/// </param>
/// <param name="Tolerance">
/// The half-width of the band, defaulting to <see cref="DefaultTolerance"/>.
/// </param>
public sealed record BeirParityTarget(
    double PublishedNdcgAt10,
    string Source,
    double Tolerance = BeirParityTarget.DefaultTolerance)
{
    /// <summary>
    /// The default half-width of the parity band: ±0.02, two-sided.
    /// </summary>
    /// <remarks>
    /// Two-sided on purpose. Scoring materially <b>above</b> a model's own published figure is not
    /// good news — nothing in this harness should be able to beat it, so a high score indicates a
    /// leak, most likely qrels reaching the ranker.
    /// </remarks>
    public const double DefaultTolerance = 0.02;

    /// <summary>Gets the lowest nDCG@10 the parity run may report and still pass.</summary>
    public double LowerBound => PublishedNdcgAt10 - Tolerance;

    /// <summary>Gets the highest nDCG@10 the parity run may report and still pass.</summary>
    public double UpperBound => PublishedNdcgAt10 + Tolerance;

    /// <summary>Reports whether <paramref name="ndcgAt10"/> lands inside the band.</summary>
    /// <param name="ndcgAt10">A measured nDCG@10.</param>
    /// <returns><see langword="true"/> when the measurement is within tolerance of published.</returns>
    public bool Contains(double ndcgAt10) => ndcgAt10 >= LowerBound && ndcgAt10 <= UpperBound;
}
