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

    /// <summary>
    /// Set to <see langword="false"/> to skip HyDE (Hypothetical Document Embeddings) for this call,
    /// even when <see cref="Rag.NET.Abstractions.IHypotheticalDocumentGenerator"/> is registered in DI.
    /// Has no effect when no generator is registered.
    /// </summary>
    public bool UseHyde { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip embedding caching for this call,
    /// even when caching is registered via <c>RagBuilder.UseCaching()</c>.
    /// Has no effect when caching is not registered.
    /// </summary>
    public bool UseCacheEmbedding { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip result caching for this call,
    /// even when caching is registered via <c>RagBuilder.UseCaching()</c>.
    /// Has no effect when caching is not registered.
    /// </summary>
    public bool UseCacheResult { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip parent-document text replacement for this call,
    /// even when parent-document retrieval is registered via <c>RagBuilder.UseParentDocumentRetrieval()</c>.
    /// Has no effect when parent-document retrieval is not registered.
    /// </summary>
    public bool UseParentDocument { get; init; } = true;

    /// <summary>
    /// Internal override for the text to embed instead of the query.
    /// Set by <see cref="Rag.NET.Retrieval.HydeRetriever"/> to pass the hypothetical document
    /// to <see cref="Rag.NET.Retrieval.VectorStoreRetriever"/> while preserving the original query for BM25.
    /// </summary>
    internal string? EmbeddingTextOverride { get; init; }
}
