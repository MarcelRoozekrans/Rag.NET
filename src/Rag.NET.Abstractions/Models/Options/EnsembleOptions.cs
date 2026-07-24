namespace Rag.NET.Models.Options;

public sealed class EnsembleOptions
{
    /// <summary>
    /// Weight applied to dense (vector) retrieval scores when combining results.
    /// Must be in the range [0, 1]. Together with <see cref="Bm25Weight"/>, the two
    /// weights control the relative contribution of each retrieval strategy.
    /// Defaults to <c>0.5</c> (equal weighting).
    /// </summary>
    public float DenseWeight { get; init; } = 0.5f;

    /// <summary>
    /// Weight applied to BM25 (sparse/keyword) retrieval scores when combining results.
    /// Must be in the range [0, 1]. Together with <see cref="DenseWeight"/>, the two
    /// weights control the relative contribution of each retrieval strategy.
    /// Defaults to <c>0.5</c> (equal weighting).
    /// </summary>
    public float Bm25Weight  { get; init; } = 0.5f;

    /// <summary>
    /// Relative weight applied to learned sparse (SPLADE) retrieval scores when combining
    /// results — use the same scale as <see cref="DenseWeight"/> and <see cref="Bm25Weight"/>
    /// (weights are not validated; only their ratios matter for the RRF fusion).
    /// Only used when the sparse ensemble arm runs — see
    /// <see cref="RetrievalOptions.UseSparseSearch"/>.
    /// Defaults to <c>0.5</c>.
    /// </summary>
    public float SparseWeight { get; init; } = 0.5f;

    /// <summary>
    /// The rank constant used in the Reciprocal Rank Fusion denominator formula:
    /// <c>weight / (k + rank + 1)</c>. A higher value reduces the impact of rank differences
    /// between candidate lists. The value <c>60</c> is the canonical default recommended
    /// by Cormack et al. (2009) and has been shown to perform well across diverse corpora.
    /// Defaults to <c>60</c>.
    /// </summary>
    public int   K           { get; init; } = 60;
}
