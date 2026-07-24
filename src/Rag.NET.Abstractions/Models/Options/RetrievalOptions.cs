using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Models.Options;

// Note: [Validate] is intentionally absent. The ZeroAlloc.Validation source generator
// does not support record types. Validation for TopK, RedundancyThreshold, and MmrLambda
// is applied manually in PipelineRetriever.RetrieveAsync.
public sealed record RetrievalOptions
{
    public int TopK { get; init; } = 5;
    public double MinScore { get; init; } = 0.0;
    public IDictionary<string, string>? MetadataFilter { get; init; }
    public bool UseHybridSearch { get; init; }

    /// <summary>
    /// Per-retriever weights and k for RRF hybrid search.
    /// Null applies defaults (0.5 / 0.5 / 60). Only used when <see cref="UseHybridSearch"/> is true.
    /// </summary>
    public EnsembleOptions? EnsembleOptions { get; init; }

    public bool UseLostInTheMiddleReordering { get; init; }
    public bool UseRedundancyFilter { get; init; }
    public float RedundancyThreshold { get; init; } = 0.95f;

    /// <summary>
    /// Set to <see langword="true"/> to enable Maximal Marginal Relevance selection for this call.
    /// Requires <c>RagBuilder.UseMmr()</c>. Unlike most retrieval features, MMR is opt-in per call.
    /// Has no effect when <c>UseMmr()</c> is not registered.
    /// </summary>
    public bool UseMmr { get; init; } = false;

    /// <summary>
    /// Lambda parameter for MMR: weight between relevance and diversity.
    /// <c>1.0</c> = pure relevance (no diversity), <c>0.0</c> = pure diversity (ignores relevance).
    /// Default <c>0.5</c> balances both.
    /// </summary>
    public float MmrLambda { get; init; } = 0.5f;

    /// <summary>
    /// Number of candidates to fetch before MMR selection.
    /// Defaults to <see cref="TopK"/> * 3 when <see langword="null"/>.
    /// Ignored when <see cref="UseMmr"/> is <see langword="false"/>.
    /// </summary>
    public int? MmrCandidateCount { get; init; }

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
    /// Optional post-search filter. Only results satisfying this specification are returned.
    /// Build complex filters with <c>spec.And(other)</c>, <c>spec.Or(other)</c>, <c>spec.Not()</c>.
    /// </summary>
    public ISpecification<SearchResult>? Filter { get; init; }

    /// <summary>
    /// Set to <see langword="false"/> to skip HyDE (Hypothetical Document Embeddings) for this call,
    /// even when <see cref="Rag.NET.Abstractions.IHypotheticalDocumentGenerator"/> is registered in DI.
    /// Has no effect when no generator is registered.
    /// </summary>
    public bool UseHyde { get; init; } = true;

    /// <summary>
    /// Set to <see langword="true"/> to enable Adaptive Retrieval complexity-based routing.
    /// Automatically adjusts <see cref="TopK"/>, <see cref="UseMultiQuery"/>, and <see cref="UseHyde"/>
    /// based on detected query complexity (simple / complex / multi_hop).
    /// Uses heuristic classification first; falls back to <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// when available and the query is ambiguous.
    /// </summary>
    public bool UseAdaptiveRetrieval { get; init; } = false;

    /// <summary>
    /// Set to <see langword="true"/> to enable Corrective RAG (CRAG) post-retrieval relevance checking.
    /// Requires <see cref="Rag.NET.Abstractions.IWebSearch"/> to be registered in DI.
    /// When the relevance score is below <see cref="CragScoreThreshold"/>, web results replace or
    /// supplement vector results according to <see cref="CragFallbackMode"/>.
    /// </summary>
    public bool UseCrag { get; init; } = false;

    /// <summary>
    /// Minimum fraction of results classified as relevant before CRAG triggers web fallback.
    /// Range: 0.0–1.0. Default <c>0.5</c>.
    /// </summary>
    public float CragScoreThreshold { get; init; } = 0.5f;

    /// <summary>
    /// Controls how web search results are merged when CRAG triggers.
    /// <see cref="CragFallbackMode.Replace"/> discards vector results (default);
    /// <see cref="CragFallbackMode.Append"/> concatenates web results after vector results.
    /// </summary>
    public CragFallbackMode CragFallbackMode { get; init; } = CragFallbackMode.Replace;

    /// <summary>
    /// Set to <see langword="false"/> to skip self-query rewriting and filter generation for this call,
    /// even when <see cref="SelfQueryOptions"/> is registered in DI.
    /// Has no effect when <c>UseSelfQuery()</c> is not registered.
    /// </summary>
    public bool UseSelfQuery { get; init; } = true;

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
    /// Set to <see langword="false"/> to skip automatic tag filter injection for this call,
    /// even when <c>RagBuilder.UseTagRetrieval()</c> is registered.
    /// Has no effect when tag retrieval is not registered.
    /// </summary>
    public bool UseTagRetrieval { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip time-weighted re-scoring for this call,
    /// even when <c>RagBuilder.UseTimeWeighting()</c> is registered.
    /// Has no effect when time-weighting is not registered.
    /// </summary>
    public bool UseTimeWeighting { get; init; } = true;

    /// <summary>
    /// Internal override for the text to embed instead of the query.
    /// Set by <see cref="Rag.NET.Retrieval.HydeRetriever"/> to pass the hypothetical document
    /// to <see cref="Rag.NET.Retrieval.VectorStoreRetriever"/> while preserving the original query for BM25.
    /// </summary>
    internal string? EmbeddingTextOverride { get; init; }

    /// <summary>
    /// Internal override for the query embedding itself.
    /// Set by HyDE v2 multi-hypothesis averaging; consumed by the vector-store and ensemble
    /// (dense arm) behaviors in preference to embedding any text
    /// (takes precedence over <see cref="EmbeddingTextOverride"/>).
    /// An empty vector means absent — same contract as <c>TextChunk.Embedding</c>.
    /// The embedding cache behavior passes through when this is set (no text key to cache under).
    /// </summary>
    internal ReadOnlyMemory<float>? EmbeddingOverride { get; init; }
}
