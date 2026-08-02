namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Which measurement a run makes over a dataset: the two chunking protocols, plus the ablation
/// table's three cells — which all index the parity corpus but cost very differently.
/// </summary>
/// <remarks>
/// Exists so <see cref="BeirRunBudget"/> can key a cost on the pair that actually determines it. A
/// dataset does not have "a cost": SciFact costs ~5 minutes under
/// <see cref="Parity"/> and roughly twice that under <see cref="Real"/>, because the real
/// protocol embeds 20,155 chunks where parity embeds 5,183 documents. Keying the budget on the
/// dataset alone would have to pick one of those two numbers and be wrong about the other. The
/// ablation cells are the same rule again: Phase 3.15 measured FiQA's +hyde cell at ~1.5 minutes
/// and its +bm25 cell at ~58, and a budget keyed on anything coarser than the cell would have to
/// answer for both with one figure.
/// </remarks>
public enum BeirProtocol
{
    /// <summary>
    /// One chunk per document, truncated at the model's 256 tokens — BEIR's own protocol, and the
    /// only one comparable to a published figure. Measured by <see cref="BeirParityTests"/>.
    /// </summary>
    Parity,

    /// <summary>
    /// Rag.NET's own chunking, max-pooled back to documents, measured against the parity run rather
    /// than against anything published. Measured by <see cref="BeirRealChunkingTests"/>, which runs
    /// <b>both</b> legs — so a real case costs its own embedding work plus whatever the parity leg
    /// costs when the cache cannot supply it.
    /// </summary>
    Real,

    /// <summary>
    /// The ablation table's +bm25 hybrid cell: the parity corpus, dense fused with
    /// <c>InMemoryBm25Index</c> via RRF, comparable to the dense anchor and to no published BM25
    /// figure. Measured by
    /// <see cref="BeirAblationTests.NdcgAt10_UnderBm25HybridRrf_MeasuresWithBm25ProvablyContributing"/>.
    /// </summary>
    HybridBm25,

    /// <summary>
    /// The ablation table's +hyde cell: the parity corpus searched with the mean of the cached
    /// hypotheticals' vectors instead of the query vector — no LLM call; the run reads the frozen
    /// generation run and refuses on a miss. Measured by
    /// <see cref="BeirAblationTests.NdcgAt10_UnderCachedHyde_MeasuresWithHydeProvablyDiverging"/>.
    /// </summary>
    Hyde,

    /// <summary>
    /// The ablation table's +reranker cell: the parity corpus's dense top-k rescored by the
    /// cross-encoder. Measured by
    /// <see cref="BeirAblationTests.NdcgAt10_UnderCrossEncoderRerank_MeasuresWithRerankerProvablyReordering"/>.
    /// </summary>
    Reranked,

    /// <summary>
    /// The library comparison's control row (Phase 3.14): the parity corpus retrieved exactly as
    /// <see cref="Parity"/> retrieves it, but scored from a TREC run file written to disk and read
    /// back — the boundary every comparison entrant crosses — rather than from the rankings in
    /// memory. Same retrieval work as <see cref="Parity"/>, so the same cold cost; what it
    /// measures is the boundary itself, on a row whose answer is already published. Measured by
    /// <see cref="BeirComparisonControlTests"/>.
    /// </summary>
    Comparison,

    /// <summary>
    /// The library comparison's Semantic Kernel row (Phase 3.14 Task 4): the corpus indexed
    /// unchunked — one InMemory-connector record per document, which is SK's actual default since
    /// it ships no ingestion pipeline — embedded and searched through Semantic Kernel's own paths
    /// with the pinned embedder, and scored from a TREC run file like every entrant. The embedding
    /// work is the parity corpus's exactly (same texts, same model), so its cold cost is the
    /// parity leg's. Measured by <see cref="BeirSemanticKernelDefaultsTests"/>.
    /// </summary>
    SemanticKernel,

    /// <summary>
    /// The library comparison's LangChain row (Phase 3.14 Stage 2): the corpus chunked by
    /// <c>RecursiveCharacterTextSplitter</c> at its defaults (4000 characters, 200 overlap),
    /// indexed in langchain-core's <c>InMemoryVectorStore</c> (cosine), retrieved through
    /// LangChain's own search path with the pinned embedder behind its <c>Embeddings</c>
    /// interface, max-pooled to documents writer-side, and scored from a TREC run file the
    /// pinned Python harness (<c>benchmarks/library-comparison-python</c>) emitted. Scored by
    /// <see cref="BeirPythonEntrantsTests"/>; no Python code computes a metric.
    /// </summary>
    LangChain,

    /// <summary>
    /// The library comparison's LlamaIndex row (Phase 3.14 Stage 2): the corpus chunked by
    /// <c>SentenceSplitter</c> at its defaults (1024 cl100k tokens, 200 overlap), indexed in
    /// <c>SimpleVectorStore</c> (cosine), retrieved through LlamaIndex's own path with the pinned
    /// embedder behind <c>Settings.embed_model</c>, max-pooled to documents writer-side, and
    /// scored from a TREC run file the pinned Python harness emitted. Scored by
    /// <see cref="BeirPythonEntrantsTests"/>; no Python code computes a metric.
    /// </summary>
    LlamaIndex,

    /// <summary>
    /// The library comparison's Haystack row (Phase 3.14 Stage 2): the corpus chunked by
    /// <c>DocumentSplitter</c> at its defaults (200 words, 0 overlap), indexed in
    /// <c>InMemoryDocumentStore</c> under its default <c>dot_product</c> similarity (the pinned
    /// vectors are unit-length, so dot product and cosine coincide), retrieved through Haystack's
    /// own <c>InMemoryEmbeddingRetriever</c>, max-pooled to documents writer-side, and scored
    /// from a TREC run file the pinned Python harness emitted. Scored by
    /// <see cref="BeirPythonEntrantsTests"/>; no Python code computes a metric.
    /// </summary>
    Haystack,
}
