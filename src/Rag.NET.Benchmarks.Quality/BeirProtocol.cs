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
    /// HyDE measured over <see cref="Real"/>'s units: Rag.NET's own chunking, max-pooled back to
    /// documents, with the query replaced by its hypothetical documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its control is <see cref="Real"/> on the same dataset.</b> Units are held fixed — the same
    /// chunking, the same pooling — and only the ranking row varies, so the difference is HyDE and
    /// nothing else. That is stated here rather than left to the reader because
    /// <see cref="SemanticChunking"/> demonstrated what the alternative costs: the same SciFact
    /// figure reads as a 0.032 regression against one control and a 0.00042 wash against the other.
    /// A cell without a named control is not a measurement.
    /// </para>
    /// <para>
    /// <b>Distinct from <see cref="Hyde"/>, which measures the same technique over parity units</b> —
    /// one chunk per document, truncated at 256. This one exists because that is not the corpus
    /// Rag.NET produces: the library ships chunking, so a HyDE figure over whole documents describes
    /// a configuration no user runs.
    /// </para>
    /// <para>
    /// Costs no model calls. The hypothetical cache is keyed on the model identity, the prompt
    /// template, the query and the hypothesis index — <b>not the corpus</b> — so the entries the
    /// parity cell generated replay unchanged here.
    /// </para>
    /// </remarks>
    RealHyde,

    /// <summary>
    /// Cross-encoder reranking measured over <see cref="Real"/>'s units: Rag.NET's own chunking,
    /// with the dense candidates rescored by <c>OnnxReranker</c> before the cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its control is <see cref="Real"/> on the same dataset</b>, for the reason given on
    /// <see cref="RealHyde"/>: units fixed, row varied, so the difference is the reranker alone.
    /// </para>
    /// <para>
    /// <b>Distinct from <see cref="Reranked"/></b>, which rescores parity units. The distinction is
    /// sharper here than for HyDE: a cross-encoder scores a query against a <i>passage</i>, and a
    /// chunk is a different passage from a whole document truncated at 256 tokens. Reranking is the
    /// technique most likely to behave differently on the corpus the library actually produces.
    /// </para>
    /// <para>Costs no model calls — the reranker is a local ONNX model.</para>
    /// </remarks>
    RealReranked,

    /// <summary>
    /// Dense retrieval fused with BM25 by reciprocal rank fusion, measured over <see cref="Real"/>'s
    /// units: Rag.NET's own chunking, with the lexical index built over the same chunks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its control is <see cref="Real"/> on the same dataset</b>, for the reason given on
    /// <see cref="RealHyde"/>: units fixed, row varied, so the difference is the BM25 arm alone.
    /// </para>
    /// <para>
    /// <b>Distinct from <see cref="HybridBm25"/></b>, which fuses over parity units. The distinction
    /// has a specific shape here that it does not have for the other two techniques: <b>BM25 is a
    /// term-frequency model, and chunking changes the document length it normalises against.</b> A
    /// whole document truncated at 256 tokens and a 512-character chunk have different term
    /// statistics, different IDF denominators and different lengths, so the lexical arm is not the
    /// same ranker over the two corpora even though the code is identical. Whether that helps or
    /// hurts is what this cell measures.
    /// </para>
    /// <para>
    /// Costs no model calls and needs no model file beyond the dense embedder: the index is
    /// <c>InMemoryBm25Index</c>, built in process over the units the harness already holds. It is
    /// the cheapest of the Real-protocol technique cells for that reason.
    /// </para>
    /// </remarks>
    RealHybridBm25,

    /// <summary>
    /// Late chunking measured end to end: the document is embedded at
    /// token level first, and each chunk's vector is pooled from token vectors that saw the whole
    /// document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It varies BOTH the boundaries and the embedding, and that was mis-stated when this cell
    /// was written.</b> The first version of this remark said the cell "keeps the Real protocol's
    /// boundaries and changes only how their vectors are computed". It does not:
    /// <c>LateChunkingStrategy</c> windows at its own <c>WindowSizeTokens</c> (256) rather than
    /// reusing <c>RecursiveChunkingStrategy</c>'s, and SciFact's first run made that plain — 9,507
    /// units against the Real cell's 20,155 over the same 5,183 documents. The comparison against
    /// <see cref="Real"/> is still the right one, because the question is "does late chunking beat
    /// the default chunking end to end", but no part of this cell isolates the embedding step.
    /// </para>
    /// <para>
    /// <b>Its control is <see cref="Real"/> on the same dataset</b> — but the asymmetry is worth
    /// stating, because it is not the same kind of comparison the other Real cells make.
    /// <see cref="RealHyde"/>, <see cref="RealReranked"/> and <see cref="RealHybridBm25"/> hold the
    /// units fixed and vary the ranking row. <b>This one varies how the units are embedded.</b> It
    /// answers "does late chunking beat Rag.NET's default chunking end to end", which is the
    /// question a user has, rather than isolating a ranking step.
    /// </para>
    /// <para>
    /// <b>Never measured before.</b> The allowlist entry that owed this cell said late chunking was
    /// "measured once in Phase 3.7 and never pinned"; 3.7 built the harness and measured SciFact's
    /// parity dense figure, and no nDCG figure for late chunking has ever existed. Phase 3.13
    /// verified it functionally after fixing a normalisation defect — that is a different claim.
    /// </para>
    /// <para>
    /// <b>Its units carry their own embeddings and must not be re-embedded.</b> The harness's normal
    /// path embeds a unit's text through the sentence embedder; doing that here would measure late
    /// chunking's BOUNDARIES with ordinary embeddings and report it under this name. The precomputed
    /// index path exists for this cell and refuses rather than falling back.
    /// </para>
    /// <para>
    /// <b>Pays no model calls and no cache.</b> The token embedder is local, and
    /// <c>EmbeddingCache</c> cannot help: it is keyed on text, and these vectors are not a function
    /// of chunk text alone — the same text in a different document embeds differently, which is the
    /// property under test.
    /// </para>
    /// </remarks>
    RealLateChunking,

    /// <summary>
    /// Learned sparse retrieval over <see cref="Real"/>'s units: every unit and every query encoded
    /// by <c>OnnxSpladeEncoder</c>, scored by sparse dot product with no dense arm at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its control is <see cref="Real"/> on the same dataset</b>, and the comparison is blunter
    /// than the other Real cells'. <see cref="RealHyde"/> changes the query vector,
    /// <see cref="RealReranked"/> rescores dense candidates, <see cref="RealHybridBm25"/> fuses a
    /// lexical arm beside the dense one — all three keep dense retrieval in the path. <b>This
    /// replaces the ranker entirely.</b> Read its figure as "what a learned sparse retriever scores
    /// on this corpus", not as "what SPLADE adds to the pipeline".
    /// </para>
    /// <para>
    /// <b>It needs a model the other cells do not, and that is why it went unmeasured for so
    /// long.</b> Until 2026-09-04 there was no SPLADE model anywhere in this repository: no export
    /// in the cache, no <c>RAGNET_ONNX_SPLADE_*</c> convention beside the embed and rerank ones, no
    /// download procedure, and <c>OnnxSpladeEncoderTests</c> driving an injected window runner
    /// rather than a real session. The encoder had never run against a real model here. The
    /// canonical <c>naver/splade-cocondenser-ensembledistil</c> publishes no ONNX export at all, so
    /// the pinned artefact is <c>Qdrant/Splade_PP_en_v1</c> — 508 MB, against the reranker's 88 —
    /// provisioned by the fenced procedure in <c>docs/reference/ci.md</c> and deliberately not by
    /// the nightly, for the reason Phase 4.1 removed the reranker from it: an input no unattended
    /// job consumes is provisioning nobody reads.
    /// </para>
    /// <para>Costs no model calls; the encoder is a local ONNX session.</para>
    /// </remarks>
    RealSplade,

    /// <summary>
    /// Retrieval over a store holding <b>two</b> corpora, restricted back to one of them by a
    /// metadata tag naming the corpus each chunk came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is the only protocol here that changes the STORE rather than the ranker.</b> Every
    /// other one indexes a single corpus and varies how it is cut, fused or rescored. This one
    /// leaves dense retrieval exactly as <see cref="Real"/> runs it and indexes SciFact together
    /// with FiQA, tagging every unit with its corpus, so the question is not how well the ranker
    /// scores but whether the filter restores what a single-corpus store would have returned.
    /// </para>
    /// <para>
    /// <b>Which is why it has a target rather than a figure.</b> The filtered run must reproduce
    /// SciFact's standalone <see cref="Real"/> number to five decimals. A tag-filtering cell built
    /// the obvious way — invent tags, filter on them, report the score — would have measured
    /// whichever vocabulary its author invented, since no BEIR corpus carries tags. The corpus a
    /// document came from is a fact about the data rather than an invention, and it turns the cell
    /// into a check that can fail.
    /// </para>
    /// <para>
    /// Costs no model calls, but it is the most expensive cell here to SET UP: it embeds FiQA's
    /// 57,638 documents alongside SciFact's 5,183 to build one store, against every other cell's
    /// single corpus.
    /// </para>
    /// </remarks>
    RealTagFiltered,

    /// <summary>
    /// Retrieval whose metadata filter is written by a real model rather than by the harness,
    /// over the same two-corpus store <see cref="RealTagFiltered"/> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pair is the point.</b> <see cref="RealTagFiltered"/> applies the corpus filter by
    /// hand through <c>MetadataFilter</c>, which the store applies while scoring, and reproduces
    /// the single-corpus figure exactly. This one asks a model to write that same filter and lets
    /// the pipeline apply it where the pipeline actually does — <c>RetrievalOptions.Filter</c>,
    /// which <c>FilterBehavior</c> runs as <c>results.Where(...)</c> AFTER the search. Two things
    /// therefore separate the figures: whether the model picked the right corpus, and what the
    /// post-retrieval wiring costs even when it did.
    /// </para>
    /// <para>
    /// Costs one model call per query, cached on disk, so a re-run replays free.
    /// </para>
    /// </remarks>
    RealSelfQuery,

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
