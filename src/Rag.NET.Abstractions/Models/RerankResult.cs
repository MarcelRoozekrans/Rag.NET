namespace Rag.NET.Models;

/// <summary>
/// A search result re-scored by an <c>IReranker</c> (typically a cross-encoder). Produced and
/// consumed internally by the reranking stage — callers of the retrieval pipeline see the
/// resulting <see cref="Models.SearchResult"/>s, not this wrapper.
/// </summary>
public sealed class RerankResult
{
    /// <summary>The original search result being re-scored.</summary>
    public required SearchResult SearchResult { get; init; }

    /// <summary>
    /// The reranker's relevance score. On its own scale, unrelated to and not comparable with
    /// <see cref="Models.SearchResult.Score"/> — reranking replaces the ordering the vector
    /// store's score produced, it does not refine it.
    /// </summary>
    public required double RelevanceScore { get; init; }
}
