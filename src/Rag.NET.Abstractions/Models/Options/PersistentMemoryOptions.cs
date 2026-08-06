namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for persistent conversation memory: past exchanges are stored in a vector store and
/// recalled by similarity to the current query, rather than kept only for the lifetime of one
/// conversation.
/// </summary>
public sealed class PersistentMemoryOptions
{
    /// <summary>Maximum number of past exchange pairs to retrieve from the vector store per query.</summary>
    public int TopK { get; init; } = 3;

    /// <summary>
    /// Minimum similarity score threshold. Matches below this value are not injected — but
    /// only when the resolved <c>IVectorStore</c> is on a similarity scale, which is every
    /// store that does not implement <c>IScoreScaleAware</c>.
    /// <para>
    /// A store declaring <c>ScoreScale.OpaqueRanking</c> (currently
    /// <c>FederatedVectorStore</c>, whose scores are Reciprocal Rank Fusion sums) has no
    /// thresholdable scale, so this value is <b>ignored</b>: recall takes the top
    /// <see cref="TopK"/> matches in rank order and logs one warning naming the store. Use
    /// <see cref="TopK"/> to bound how much past context is injected in that case.
    /// </para>
    /// </summary>
    public double MinScore { get; init; } = 0.7;
}
