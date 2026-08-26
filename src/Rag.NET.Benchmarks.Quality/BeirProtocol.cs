namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Which measurement a run makes over a dataset: the two chunking protocols, plus the ablation
/// table's three cells — which all index the parity corpus but cost very differently.
/// </summary>
/// <remarks>
/// Exists so <c>BeirRunBudget</c> can key a cost on the pair that actually determines it. A
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
    /// only one comparable to a published figure. Measured by <c>BeirParityTests</c>.
    /// </summary>
    Parity,

    /// <summary>
    /// Rag.NET's own chunking, max-pooled back to documents, measured against the parity run rather
    /// than against anything published. Measured by <c>BeirRealChunkingTests</c>, which runs
    /// <b>both</b> legs — so a real case costs its own embedding work plus whatever the parity leg
    /// costs when the cache cannot supply it.
    /// </summary>
    Real,

    /// <summary>
    /// The ablation table's +bm25 hybrid cell: the parity corpus, dense fused with
    /// <c>InMemoryBm25Index</c> via RRF, comparable to the dense anchor and to no published BM25
    /// figure. Measured by
    /// <c>BeirAblationTests.NdcgAt10_UnderBm25HybridRrf_MeasuresWithBm25ProvablyContributing</c>.
    /// </summary>
    HybridBm25,

    /// <summary>
    /// The ablation table's +hyde cell: the parity corpus searched with the mean of the cached
    /// hypotheticals' vectors instead of the query vector — no LLM call; the run reads the frozen
    /// generation run and refuses on a miss. Measured by
    /// <c>BeirAblationTests.NdcgAt10_UnderCachedHyde_MeasuresWithHydeProvablyDiverging</c>.
    /// </summary>
    Hyde,

    /// <summary>
    /// The ablation table's +reranker cell: the parity corpus's dense top-k rescored by the
    /// cross-encoder. Measured by
    /// <c>BeirAblationTests.NdcgAt10_UnderCrossEncoderRerank_MeasuresWithRerankerProvablyReordering</c>.
    /// </summary>
    Reranked,

    /// <summary>
    /// The library comparison's control row (Phase 3.14): the parity corpus retrieved exactly as
    /// <see cref="Parity"/> retrieves it, but scored from a TREC run file written to disk and read
    /// back — the boundary every comparison entrant crosses — rather than from the rankings in
    /// memory. Same retrieval work as <see cref="Parity"/>, so the same cold cost; what it
    /// measures is the boundary itself, on a row whose answer is already published. Measured by
    /// <c>BeirComparisonControlTests</c>.
    /// </summary>
    Comparison,

    /// <summary>
    /// The library comparison's Semantic Kernel row (Phase 3.14 Task 4): the corpus indexed
    /// unchunked — one InMemory-connector record per document, which is SK's actual default since
    /// it ships no ingestion pipeline — embedded and searched through Semantic Kernel's own paths
    /// with the pinned embedder, and scored from a TREC run file like every entrant. The embedding
    /// work is the parity corpus's exactly (same texts, same model), so its cold cost is the
    /// parity leg's. Measured by <c>BeirSemanticKernelDefaultsTests</c>.
    /// </summary>
    SemanticKernel,

    /// <summary>
    /// The library comparison's LangChain row (Phase 3.14 Stage 2): the corpus chunked by
    /// <c>RecursiveCharacterTextSplitter</c> at its defaults (4000 characters, 200 overlap),
    /// indexed in langchain-core's <c>InMemoryVectorStore</c> (cosine), retrieved through
    /// LangChain's own search path with the pinned embedder behind its <c>Embeddings</c>
    /// interface, max-pooled to documents writer-side, and scored from a TREC run file the
    /// pinned Python harness (<c>benchmarks/library-comparison-python</c>) emitted. Scored by
    /// <c>BeirPythonEntrantsTests</c>; no Python code computes a metric.
    /// </summary>
    LangChain,

    /// <summary>
    /// The library comparison's LlamaIndex row (Phase 3.14 Stage 2): the corpus chunked by
    /// <c>SentenceSplitter</c> at its defaults (1024 cl100k tokens, 200 overlap), indexed in
    /// <c>SimpleVectorStore</c> (cosine), retrieved through LlamaIndex's own path with the pinned
    /// embedder behind <c>Settings.embed_model</c>, max-pooled to documents writer-side, and
    /// scored from a TREC run file the pinned Python harness emitted. Scored by
    /// <c>BeirPythonEntrantsTests</c>; no Python code computes a metric.
    /// </summary>
    LlamaIndex,

    /// <summary>
    /// The library comparison's Haystack row (Phase 3.14 Stage 2): the corpus chunked by
    /// <c>DocumentSplitter</c> at its defaults (200 words, 0 overlap), indexed in
    /// <c>InMemoryDocumentStore</c> under its default <c>dot_product</c> similarity (the pinned
    /// vectors are unit-length, so dot product and cosine coincide), retrieved through Haystack's
    /// own <c>InMemoryEmbeddingRetriever</c>, max-pooled to documents writer-side, and scored
    /// from a TREC run file the pinned Python harness emitted. Scored by
    /// <c>BeirPythonEntrantsTests</c>; no Python code computes a metric.
    /// </summary>
    Haystack,

    /// <summary>
    /// The ablation table's corpus, split by <c>SemanticChunkingStrategy</c> instead of indexed one
    /// chunk per document — the embedding-based boundary detector measured against the chunking it
    /// would replace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A protocol rather than a variant inside <see cref="Real"/> because it changes what a unit
    /// <i>is</i>: a document becomes several indexed units, and
    /// <see cref="DocumentRanking.TopDocuments(System.Collections.Generic.IReadOnlyList{ScoredDocument}, int)"/>
    /// max-pools them back to one document before the cut. That pooling is why the figure is
    /// comparable to a one-chunk-per-document run at all.
    /// </para>
    /// <para>
    /// <b>Its control is <see cref="Parity"/> on the same dataset, not <see cref="Real"/>.</b> Like
    /// every other cell in the ablation table it runs under the parity protocol — one chunk per
    /// document, truncated at 256 — because that is where the table's dense anchor is. Against the
    /// <see cref="Real"/> figure instead, SciFact's 0.64551 reads as a 0.032 regression; against the
    /// parity anchor it is 0.00042, a wash. The same number, two controls, opposite conclusions,
    /// which is the whole reason the control is named here rather than left to the reader.
    /// </para>
    /// <para>
    /// Reported without that difference the number says nothing at all: chunking cannot be better or
    /// worse in the abstract, only against the chunking it replaces.
    /// </para>
    /// </remarks>
    SemanticChunking,

    /// <summary>
    /// The graph path: entities and relations extracted from the corpus into a graph, that graph
    /// partitioned into communities, and retrieval running over the result — local search out from
    /// the entities a query names, global search over the community summaries. <b>Applies to
    /// MultiHop-RAG and to nothing else here.</b>
    /// <para>
    /// The other ten protocols all index a flat corpus and differ only in how they cut, fuse or
    /// rescore it, so their costs are variations on one embedding bill. This one builds a second
    /// structure before it retrieves anything, which is a construction cost no chunking figure
    /// predicts — the reason it is a protocol rather than another ablation cell.
    /// </para>
    /// <para>
    /// It is restricted to MultiHop-RAG because a graph can only be rewarded where the judgements
    /// need more than one document. The four BEIR datasets here judge a query against documents
    /// that answer it individually, so a graph built over them would be measured by qrels that
    /// cannot tell whether it helped; MultiHop-RAG's queries cite 2 to 4 articles each and are
    /// written to be unanswerable from any one of them.
    /// </para>
    /// </summary>
    GraphRag,

    /// <summary>
    /// The depth-matched dense control for <see cref="GraphRag"/>: the article chunks alone —
    /// exactly what <see cref="Real"/> indexes, cut by the same chunker — retrieved at the graph
    /// path's candidate depth rather than at the Real protocol's, and max-pooled to documents the
    /// same way. <b>Applies to MultiHop-RAG and to nothing else here.</b>
    /// <para>
    /// It exists to separate two things the graph run changed at once. Against the Real leg the
    /// graph path's store held 321,151 units instead of 17,648 and its candidate set was 500 deep
    /// instead of 2,010, and the gap between the graph run's own candidate-set control (0.59658)
    /// and the Real leg (0.63967) could be either. This protocol moves only the depth: same store as
    /// Real, same chunks, same pooling, top-500. Its difference from the Real leg prices the depth,
    /// and its difference from the graph run's candidate-set control prices what the extra
    /// 303,503 graph-derived units cost the judged documents by competing with them for rank.
    /// </para>
    /// <para>
    /// A protocol rather than a second figure inside <see cref="GraphRag"/>'s cell because it has
    /// its own cost — no graph is built, no cache is replayed, it needs the article vectors and
    /// nothing else — and its own figure to pin, and both tables are keyed on the pair.
    /// </para>
    /// </summary>
    GraphRagDepthControl,
}
