using Rag.NET.Models;
using ZeroAlloc.Specification;
using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>
/// Per-call overrides for <c>IRetriever.RetrieveAsync</c>. Unset properties fall back to whatever
/// the pipeline was configured with at startup.
/// </summary>
[Validate]
public sealed record RetrievalOptions
{
    /// <summary>
    /// Chunks to return after all pipeline stages. Defaults to 5. Must be greater than 0 —
    /// enforced by the validation attribute, which <c>PipelineRetriever.RetrieveAsync</c> runs
    /// through the generated <c>RetrievalOptionsValidator</c>, rejecting the call with a
    /// validation failure rather than clamping the value.
    /// </summary>
    [GreaterThan(0)]
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Minimum similarity score a retrieved chunk must meet — a similarity score, not a
    /// percentage. Defaults to 0.0 (no filtering). Passed straight through to the vector store's
    /// own score threshold; filtering happens inside the store, not here.
    /// <para>
    /// Deliberately unbounded apart from rejecting non-finite values (NaN and infinities):
    /// inner-product stores return unbounded scores, and stores implementing
    /// <see cref="Rag.NET.Abstractions.IScoreScaleAware"/> with
    /// <see cref="Rag.NET.Abstractions.ScoreScale.OpaqueRanking"/> return RRF values on an
    /// entirely different scale, so a zero-to-one bound would reject configurations that are
    /// legitimate for those stores. Only the declared score scale could justify a tighter
    /// bound, and this record does not know the store.
    /// </para>
    /// </summary>
    [Must(nameof(MinScoreIsFinite), Message = "MinScore must be a finite number (not NaN or infinity).")]
    public double MinScore { get; init; } = 0.0;

    /// <summary>Reports whether <see cref="MinScore"/> is a finite number.</summary>
    /// <param name="value">The <see cref="MinScore"/> value under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool MinScoreIsFinite(double value) => double.IsFinite(value);

    /// <summary>
    /// Restricts retrieval to chunks whose metadata matches every key/value pair exactly
    /// (typed equality — a number filter value matches a number, not its string form — with
    /// ordinal comparison for strings and AND semantics across pairs). <see langword="null"/> or
    /// an empty dictionary means no filtering.
    /// </summary>
    public IDictionary<string, MetadataValue>? MetadataFilter { get; init; }

    /// <summary>
    /// Combines dense vector search with sparse/keyword search for this call. Defaults to
    /// <see langword="false"/> (dense search only). Served by one of two mechanisms:
    /// when the registered store implements <see cref="Rag.NET.Abstractions.IHybridSearchable"/>
    /// and this call configures nothing native fusion cannot express — no sparse (SPLADE) arm
    /// would run, <see cref="EnsembleOptions"/> is not supplied, and <see cref="MinScore"/> is
    /// <c>0.0</c> — the store's own server-side hybrid query runs in a single backend call and
    /// returns scores on the backend's fusion scale. Otherwise dense and BM25 (and, when
    /// active, sparse) searches run client-side and are merged by reciprocal rank fusion with
    /// <see cref="EnsembleOptions"/> weights, returning RRF scores. Either way the scores are
    /// not similarities — treat them as ordinal.
    /// </summary>
    public bool UseHybridSearch { get; init; }

    /// <summary>
    /// Per-retriever weights and k for RRF hybrid search.
    /// Null applies defaults (0.5 / 0.5 / 60). Only used when <see cref="UseHybridSearch"/> is true.
    /// When set, its own validation attributes run as part of the generated
    /// <c>RetrievalOptionsValidator</c> (nested validation), reporting failures under
    /// <c>EnsembleOptions.*</c> property names.
    /// </summary>
    public EnsembleOptions? EnsembleOptions { get; init; }

    /// <summary>
    /// Controls the learned sparse (SPLADE) arm of hybrid search.
    /// <see langword="null"/> (the default) follows <see cref="UseHybridSearch"/>: the sparse
    /// arm joins the ensemble whenever hybrid search runs, an
    /// <see cref="Rag.NET.Abstractions.ISparseEmbeddingGenerator"/> is registered, and the
    /// vector store implements <see cref="Rag.NET.Abstractions.ISparseSearchable"/>.
    /// Set to <see langword="false"/> to exclude the sparse arm from hybrid search for this
    /// call. Setting <see langword="true"/> without <see cref="UseHybridSearch"/> has no
    /// effect — sparse search only participates in the ensemble.
    /// </summary>
    public bool? UseSparseSearch { get; init; }

    /// <summary>
    /// Reorders retrieved chunks so the most relevant sit at the start and end of the context
    /// rather than the middle, countering LLMs' tendency to attend less to mid-context text.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool UseLostInTheMiddleReordering { get; init; }

    /// <summary>
    /// Drops near-duplicate chunks by embedding cosine similarity before results are returned.
    /// Defaults to <see langword="false"/>; when enabled, <see cref="RedundancyThreshold"/> is the
    /// similarity cutoff at which a later chunk is considered a duplicate and dropped.
    /// </summary>
    public bool UseRedundancyFilter { get; init; }

    /// <summary>
    /// Cosine similarity at or above which two chunks are treated as redundant, so the later one
    /// is dropped. Range 0.0–1.0 — enforced by the validation attribute;
    /// <c>PipelineRetriever.RetrieveAsync</c> rejects a value outside that range with a
    /// validation failure. Default 0.95. Ignored unless <see cref="UseRedundancyFilter"/>
    /// is <see langword="true"/>.
    /// </summary>
    [InclusiveBetween(0.0, 1.0)]
    public float RedundancyThreshold { get; init; } = 0.95f;

    /// <summary>
    /// Caps the combined length of the retrieved chunks, in cl100k_base tokens.
    /// <see langword="null"/> — the default — applies no length bound, which is the behaviour
    /// this repository had before the setting existed.
    /// <para>
    /// <see cref="TopK"/> bounds how <i>many</i> chunks come back and <see cref="MinScore"/> how
    /// relevant they are; neither bounds how <i>long</i> they are. A corpus rechunked from 500 to
    /// 4,000 characters silently multiplies the prompt at the same TopK, with no error until the
    /// model rejects the request (issue #85). Chunk size is an ingestion decision; the context
    /// limit is a model constraint known at query time, and this is how the second gets said.
    /// </para>
    /// <para>
    /// Chunks are dropped whole and lowest-ranked first, never truncated, and the drop is logged
    /// — see <c>ContextBudgetBehavior</c> for why each of those is the way round it is.
    /// </para>
    /// </summary>
    [GreaterThan(0, When = nameof(MaxContextTokensIsSet))]
    public int? MaxContextTokens { get; init; }

    /// <summary>Reports whether <see cref="MaxContextTokens"/> was set at all.</summary>
    /// <returns>Whether a budget is configured.</returns>
    internal bool MaxContextTokensIsSet() => MaxContextTokens.HasValue;

    /// <summary>
    /// Set to <see langword="true"/> to enable Maximal Marginal Relevance selection for this call.
    /// Requires <c>RagBuilder.UseMmr()</c>. Unlike most retrieval features, MMR is opt-in per call.
    /// Has no effect when <c>UseMmr()</c> is not registered.
    /// </summary>
    public bool UseMmr { get; init; } = false;

    /// <summary>
    /// Lambda parameter for MMR: weight between relevance and diversity.
    /// <c>1.0</c> = pure relevance (no diversity), <c>0.0</c> = pure diversity (ignores relevance).
    /// Default <c>0.5</c> balances both. Range 0.0–1.0 — enforced by the validation attribute;
    /// <c>PipelineRetriever.RetrieveAsync</c> rejects a value outside it with a validation
    /// failure.
    /// </summary>
    [InclusiveBetween(0.0, 1.0)]
    public float MmrLambda { get; init; } = 0.5f;

    /// <summary>
    /// Number of candidates to fetch before MMR selection.
    /// Defaults to <see cref="TopK"/> * 3 when <see langword="null"/>.
    /// Ignored when <see cref="UseMmr"/> is <see langword="false"/>.
    /// <para>
    /// When set, must be greater than 0 — enforced by the validation attribute
    /// (<see langword="null"/> passes). <c>MmrBehavior</c> overwrites the downstream
    /// <see cref="TopK"/> with this value, and vector stores return no results for a
    /// non-positive limit — so an unvalidated 0 here would silently replace the validated
    /// <see cref="TopK"/> and empty every retrieval (issue #94).
    /// </para>
    /// </summary>
    [GreaterThan(0, When = nameof(MmrCandidateCountIsSet))]
    public int? MmrCandidateCount { get; init; }

    /// <summary>Reports whether <see cref="MmrCandidateCount"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="MmrCandidateCount"/> has a value.</returns>
    internal bool MmrCandidateCountIsSet() => MmrCandidateCount is not null;

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
    /// <para>
    /// When set, must be greater than 0 — enforced by the validation attribute
    /// (<see langword="null"/> passes). <c>RerankingBehavior</c> overwrites the downstream
    /// <see cref="TopK"/> with this value, and vector stores return no results for a
    /// non-positive limit — so an unvalidated 0 here would silently replace the validated
    /// <see cref="TopK"/> and empty every retrieval (issue #94).
    /// </para>
    /// </summary>
    [GreaterThan(0, When = nameof(CandidateCountIsSet))]
    public int? CandidateCount { get; init; }

    /// <summary>Reports whether <see cref="CandidateCount"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="CandidateCount"/> has a value.</returns>
    internal bool CandidateCountIsSet() => CandidateCount is not null;

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
    /// Range: 0.0–1.0 — enforced by the validation attribute;
    /// <c>PipelineRetriever.RetrieveAsync</c> rejects a value outside that range with a
    /// validation failure. Default <c>0.5</c>.
    /// </summary>
    [InclusiveBetween(0.0, 1.0)]
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
