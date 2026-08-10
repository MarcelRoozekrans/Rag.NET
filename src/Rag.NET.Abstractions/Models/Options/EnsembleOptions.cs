using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>
/// Per-arm weights and the rank constant for Reciprocal Rank Fusion, used to combine dense,
/// BM25, and (when active) sparse retrieval results when
/// <see cref="RetrievalOptions.UseHybridSearch"/> is <see langword="true"/>.
/// Validated by the generated <c>EnsembleOptionsValidator</c>, which
/// <c>RetrievalOptionsValidator</c> runs automatically (nested validation) whenever
/// <see cref="RetrievalOptions.EnsembleOptions"/> is set.
/// </summary>
[Validate]
public sealed class EnsembleOptions
{
    /// <summary>
    /// Weight applied to dense (vector) retrieval scores when combining results.
    /// Must be in the range [0, 1] — enforced by the validation attribute;
    /// <c>PipelineRetriever.RetrieveAsync</c> rejects a value outside it with a
    /// validation failure. Together with <see cref="Bm25Weight"/>, the two
    /// weights control the relative contribution of each retrieval strategy.
    /// Defaults to <c>0.5</c> (equal weighting).
    /// </summary>
    [InclusiveBetween(0.0, 1.0)]
    public float DenseWeight { get; init; } = 0.5f;

    /// <summary>
    /// Weight applied to BM25 (sparse/keyword) retrieval scores when combining results.
    /// Must be in the range [0, 1] — enforced by the validation attribute;
    /// <c>PipelineRetriever.RetrieveAsync</c> rejects a value outside it with a
    /// validation failure. Together with <see cref="DenseWeight"/>, the two
    /// weights control the relative contribution of each retrieval strategy.
    /// Defaults to <c>0.5</c> (equal weighting).
    /// </summary>
    [InclusiveBetween(0.0, 1.0)]
    public float Bm25Weight  { get; init; } = 0.5f;

    /// <summary>
    /// Relative weight applied to learned sparse (SPLADE) retrieval scores when combining
    /// results — the same scale as <see cref="DenseWeight"/> and <see cref="Bm25Weight"/>,
    /// so it carries the same bound: must be in the range [0, 1], enforced by the validation
    /// attribute. Only the ratios between the weights matter for RRF fusion, but a weight
    /// outside the shared scale is a configuration error — and a negative weight would push
    /// every sparse hit <em>down</em> the fused ranking.
    /// Only used when the sparse ensemble arm runs — see
    /// <see cref="RetrievalOptions.UseSparseSearch"/>.
    /// Defaults to <c>0.5</c>.
    /// </summary>
    [InclusiveBetween(0.0, 1.0)]
    public float SparseWeight { get; init; } = 0.5f;

    /// <summary>
    /// The rank constant used in the Reciprocal Rank Fusion denominator formula:
    /// <c>weight / (k + rank + 1)</c>. A higher value reduces the impact of rank differences
    /// between candidate lists. The value <c>60</c> is the canonical default recommended
    /// by Cormack et al. (2009) and has been shown to perform well across diverse corpora.
    /// Defaults to <c>60</c>. Must be greater than 0 — enforced by the validation attribute.
    /// A negative constant can zero (or flip the sign of) the denominator for early ranks,
    /// and before this bound existed a non-positive value was silently clamped to 1 by the
    /// merger — the configured value and the effective value disagreed with no signal, the
    /// exact failure mode of issue #94.
    /// </summary>
    [GreaterThan(0)]
    public int   K           { get; init; } = 60;
}
