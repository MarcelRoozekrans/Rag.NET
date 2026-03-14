namespace Rag.NET.Models.Options;

public sealed record RetrievalOptions
{
    public int TopK { get; init; } = 5;
    public double MinScore { get; init; } = 0.0;
    public IDictionary<string, string>? MetadataFilter { get; init; }
    public bool UseHybridSearch { get; init; }
    public bool UseLostInTheMiddleReordering { get; init; }
    public bool UseRedundancyFilter { get; init; }
    public float RedundancyThreshold { get; init; } = 0.95f;

    /// <summary>
    /// Set to <see langword="false"/> to skip multi-query expansion for this call,
    /// even when <see cref="Rag.NET.Abstractions.IQueryExpander"/> is registered in DI.
    /// Has no effect when no expander is registered.
    /// </summary>
    public bool UseMultiQuery { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip cross-encoder reranking for this call,
    /// even when <see cref="Rag.NET.Abstractions.IReranker"/> is registered in DI.
    /// Has no effect when no reranker is registered.
    /// </summary>
    public bool UseReranking { get; init; } = true;

    /// <summary>
    /// Number of candidates to fetch from vector search before reranking.
    /// When an <see cref="Rag.NET.Abstractions.IReranker"/> is registered and this is
    /// <see langword="null"/>, defaults to <see cref="TopK"/> * 3.
    /// Ignored when no reranker is registered or <see cref="UseReranking"/> is <see langword="false"/>.
    /// </summary>
    public int? CandidateCount { get; init; }
}
