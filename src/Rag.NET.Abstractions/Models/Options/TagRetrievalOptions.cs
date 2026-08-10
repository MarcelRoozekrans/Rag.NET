using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>Options for <see cref="Rag.NET.Retrieval.TagRetriever"/>.</summary>
[Validate]
public sealed class TagRetrievalOptions
{
    /// <summary>
    /// Maximum number of distinct tag keys to inject as metadata filters.
    /// For each matched key the highest-scoring value is used.
    /// Default: 1.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>RagBuilder.UseTagRetrieval</c> runs through the generated
    /// <c>TagRetrievalOptionsValidator</c> at registration. With a zero (or negative) cap,
    /// <c>TagRetriever</c> still embeds the query and scans the tag index but can never
    /// inject a filter — tag retrieval silently fails open on every call.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int TopK { get; init; } = 1;

    /// <summary>
    /// Minimum cosine similarity for a tag to be injected.
    /// Default: 0.82.
    /// <para>
    /// Must be between -1.0 and 1.0, the range cosine similarity can take — enforced by the
    /// validation attribute, which <c>RagBuilder.UseTagRetrieval</c> runs through the generated
    /// <c>TagRetrievalOptionsValidator</c> at registration. A threshold above 1 can never be
    /// met (<c>InMemoryTagIndex.Search</c> compares <c>score &gt;= minScore</c> against cosine
    /// similarity), so tag retrieval would silently inject no filters — failing open — on
    /// every call. The explicit finiteness rule exists because every comparison against NaN
    /// is false, so NaN is never "outside" the range — it slips through the bounds check and
    /// then fails every <c>score &gt;= minScore</c> comparison, the same silent fail-open.
    /// </para>
    /// </summary>
    [InclusiveBetween(-1.0, 1.0)]
    [Must(nameof(MinScoreIsFinite), Message = "MinScore must be a finite number (not NaN or infinity).")]
    public double MinScore { get; init; } = 0.82;

    /// <summary>Reports whether <see cref="MinScore"/> is a finite number.</summary>
    /// <param name="value">The <see cref="MinScore"/> value under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool MinScoreIsFinite(double value) => double.IsFinite(value);
}
