namespace Rag.NET.Abstractions;

// Placement is deliberate: this enum lives in Abstractions/ next to IScoreScaleAware, whose
// contract it is, rather than in Models/ with the standalone enums. It is part of the
// capability interface's signature, and relocating it later would change its namespace —
// a source-breaking change for consumers that implement or probe the interface.

/// <summary>
/// The scale on which an <see cref="IVectorStore"/> reports
/// <see cref="Models.SearchResult.Score"/>, declared through
/// <see cref="IScoreScaleAware"/>.
/// </summary>
public enum ScoreScale
{
    /// <summary>
    /// Scores are similarities: comparable across queries, roughly bounded to <c>[0, 1]</c>,
    /// and meaningful to threshold against a fixed cut-off (e.g. cosine similarity). This is
    /// the assumed scale for stores that do not implement <see cref="IScoreScaleAware"/>.
    /// </summary>
    Similarity = 0,

    /// <summary>
    /// Scores are an opaque ranking signal: ordinal only. Their magnitude carries no
    /// cross-store or cross-query meaning, so only the relative order is usable and fixed
    /// thresholds must not be applied. Reciprocal Rank Fusion (whose scores peak near
    /// <c>1 / (k + 1)</c> per contributing store) and unbounded backend hybrid scores are
    /// both on this scale.
    /// </summary>
    OpaqueRanking = 1,
}
