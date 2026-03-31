namespace Rag.NET.Models.Options;

/// <summary>Options for <see cref="Rag.NET.Retrieval.TagRetriever"/>.</summary>
public sealed class TagRetrievalOptions
{
    /// <summary>
    /// Maximum number of distinct tag keys to inject as metadata filters.
    /// For each matched key the highest-scoring value is used.
    /// Default: 1.
    /// </summary>
    public int TopK { get; init; } = 1;

    /// <summary>
    /// Minimum cosine similarity for a tag to be injected.
    /// Default: 0.82.
    /// </summary>
    public double MinScore { get; init; } = 0.82;
}
