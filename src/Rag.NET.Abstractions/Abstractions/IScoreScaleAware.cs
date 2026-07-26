namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability interface for <see cref="IVectorStore"/> implementations that declare
/// which <see cref="ScoreScale"/> their <see cref="Models.SearchResult.Score"/> values are on —
/// the same probe pattern as <see cref="IHybridSearchable"/> and <see cref="ISparseSearchable"/>.
/// Callers discover the capability with an <c>is IScoreScaleAware</c> check on the registered
/// store and treat its absence as <see cref="ScoreScale.Similarity"/>, which is what every
/// score threshold in the library already assumes.
/// </summary>
/// <remarks>
/// Implement this only when the store's scores are <em>not</em> similarities — a store whose
/// scores are cosine-like needs no declaration. Consumers that apply a fixed threshold (for
/// example persistent conversation memory's <c>MinScore</c>) skip it on
/// <see cref="ScoreScale.OpaqueRanking"/> and fall back to taking results in rank order.
/// </remarks>
public interface IScoreScaleAware
{
    /// <summary>
    /// The scale of the scores this store returns from
    /// <see cref="IVectorStore.SearchAsync"/>. Must be a constant for the lifetime of the
    /// instance: callers probe it once and may cache the answer.
    /// </summary>
    ScoreScale ScoreScale { get; }
}
