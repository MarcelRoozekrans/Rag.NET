namespace Rag.NET.Models.Options;

/// <summary>
/// The shape a vector store actually receives: <see cref="Rag.NET.Abstractions.IVectorStore.SearchAsync"/>
/// and <see cref="Rag.NET.Abstractions.IHybridSearchable.HybridSearchAsync"/> take this rather than
/// <c>RetrievalOptions</c>. The retrieval pipeline translates the richer <c>RetrievalOptions</c>
/// down to this before reaching the store, so a store implementation never sees the pipeline-only
/// knobs (MMR, reranking, HyDE, and so on).
/// </summary>
public sealed class SearchOptions
{
    /// <summary>Chunks the store should return. Defaults to 5.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Minimum similarity score a returned chunk must meet — a similarity score, not a
    /// percentage. Defaults to 0.0 (no filtering). Each store applies this as its own native
    /// score threshold; there is no filtering layered on top by the pipeline.
    /// </summary>
    public double MinScore { get; set; } = 0.0;

    /// <summary>
    /// Restricts results to chunks whose metadata matches every key/value pair exactly. Exact
    /// matching semantics (case sensitivity, AND vs. OR) are up to each store's implementation.
    /// <see langword="null"/> or an empty dictionary means no filtering. Matching is typed:
    /// a filter value of <c>3</c> (a <see cref="MetadataValue"/> number) matches metadata
    /// written as a number, not the string <c>"3"</c>.
    /// </summary>
    public IDictionary<string, MetadataValue>? MetadataFilter { get; set; }
}
