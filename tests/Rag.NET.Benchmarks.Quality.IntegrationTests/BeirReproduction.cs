using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What each measurement actually reported, and how far it may move before that is a regression
/// rather than a different computer.
/// <para>
/// <b>This is not a parity band and it is not a second opinion on anybody's published figure.</b>
/// <see cref="BeirParityTarget"/> holds MTEB's number and a ±0.02 band around it, and that band is
/// correct as what it is: a tolerance for agreeing with a figure produced by other software on other
/// hardware. It is the wrong instrument for the other question — <i>did this repository's own number
/// move</i> — because ±0.02 is wider than most defects. The reviewer of Phase 3.12 demonstrated
/// exactly that: under a cut-then-pool mutation of <see cref="DocumentRanking"/>, SciFact's real run
/// fell 0.65589 → 0.64008 and ArguAna's 0.42594 → 0.40612, and <b>both tests still passed</b>, one
/// on the ±0.02 published band and one on <see cref="BeirRealChunkingTests"/>' 0.5×–1.5× collapse
/// envelope.
/// </para>
/// <para>
/// <b>So this is a reproduction check, kept deliberately separate from the parity check.</b> Every
/// figure below is a number this repository produced, on the machine named in its entry, on the date
/// named in its entry. Nothing here claims agreement with the literature; the entries for the real
/// protocol could not, since no published figure exists for it at all.
/// </para>
/// <para>
/// <b>Why it is not an exact match.</b> On one machine, a run is deterministic wherever the
/// ranking's tie order is pinned: the same model over the same corpus produces the same vectors,
/// in-memory storage is pinned for that reason, <see cref="DocumentRanking"/> breaks score ties by
/// ordinal document id, and the Python entrants' pooling applies the same ordinal rule
/// (<c>doc_ranking.top_documents</c>) over libraries whose own orderings are deterministic
/// functions of insertion order. The exception is <see cref="BeirProtocol.SemanticKernel"/>, by
/// design: its entrant keeps SK's returned order untouched — a tie-break of ours would measure our
/// re-sort instead of Semantic Kernel — and SK's InMemory connector does not order exact ties
/// repeatably across processes. On ArguAna, where 1,298 of 1,406 queries are byte-identical to
/// their own corpus document and exactly-equal cosine scores abound, three runs on the measuring
/// machine gave nDCG@10 0.50306, 0.50321 and 0.50399 with Recall@10 identical at 0.79161 — the
/// same documents every run, only exact-tie order moving — so that entry records the observed
/// spread rather than a single figure presented as exact. Across machines nothing is exact for any
/// protocol: ONNX Runtime dispatches its kernels on the available instruction set, so a vector can
/// differ in its last bits and a near-tie can order the other way. A test that demanded the fifth
/// decimal on every runner would eventually go red for a reason nobody could act on, and the usual
/// response to that is to delete it.
/// </para>
/// </summary>
public static class BeirReproduction
{
    /// <summary>
    /// How far a re-measurement may sit from the recorded one: ±0.005.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chosen from both ends rather than picked. <b>The lower end</b> is what the check has to
    /// catch: the cut-then-pool mutation moves SciFact's real run by 0.0158 and ArguAna's by 0.0198,
    /// so anything below about 0.015 catches it — 0.005 does, three times over.
    /// </para>
    /// <para>
    /// <b>The upper end</b> is what it must not trip on. SciFact scores 300 judged queries, and one
    /// query whose relevant document slips from rank 1 to rank 2 costs 1 − 1/log₂3 ≈ 0.37 of that
    /// query's ideal gain, so a single rank flip moves the mean by roughly 0.37 ÷ 300 ≈ 0.0012.
    /// SciFact is the coarsest dataset here for that reason — ArguAna's 1,406 judged queries make
    /// its quantum ≈ 0.00026 — so a window narrower than about 0.005 is one that a handful of
    /// near-ties resolving the other way on another CPU can breach. 0.005 is four of SciFact's
    /// quanta.
    /// </para>
    /// <para>
    /// For the parity runs this is four times tighter than the published band. For the <i>real</i>
    /// runs it replaces an envelope of ±50%, which is where the whole gap was.
    /// </para>
    /// </remarks>
    public const double Tolerance = 0.005;

    /// <summary>
    /// Every dataset under every protocol, with what it measured — or with the fact that it never
    /// has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table with the same shape and the same rule as <see cref="BeirRunBudget"/>'s: a pair that
    /// is not in it throws rather than passing quietly, so a fourth dataset cannot join the suite
    /// with nothing pinning its number. Adding the descriptor fails here until somebody has run it.
    /// </para>
    /// <para>
    /// <b>An entry may record that no figure exists</b>, which is a different state from being
    /// absent — it is the state FiQA's real leg sat in from Phase 3.12 until Phase 3.15 measured it
    /// on 2026-08-02. <see cref="AssertReproduces"/> prints what such a run measured and asserts
    /// nothing about it, because an hours-long measurement whose only outcome is a failure telling
    /// the runner to write down what they just watched is a measurement people learn to skip. No
    /// case is in that state today; the next dataset's legs will start there.
    /// </para>
    /// <para>
    /// <b>More than one figure per pair is allowed, and is the answer to a different machine —
    /// or to a protocol that is not deterministic even on one.</b> If a hosted runner reproduces
    /// something legitimately outside <see cref="Tolerance"/> of every figure here, the fix is
    /// another entry naming that machine — not a wider window. Widening costs every dataset its
    /// resolution to settle one runner; a second figure costs nothing and says plainly that two
    /// machines disagree. The SemanticKernel ArguAna entry uses the same mechanism for a different
    /// reason: three figures from <i>one</i> machine, because that protocol's tie ordering does not
    /// repeat across processes and the spread is the honest record.
    /// </para>
    /// </remarks>
    private static readonly Reproduction[] Reproductions =
    [
        new(
            "scifact",
            BeirProtocol.Parity,
            [0.64593],
            "Both separators, and they agree exactly — Phase 3.13 fixed the tokenizer defect that " +
            "made the newline leg read 0.64907. 300 of 1,109 queries judged. Windows 11, .NET 10, " +
            "CPU ONNX Runtime; re-measured 2026-07-31."),
        new(
            "scifact",
            BeirProtocol.Real,
            [0.67742],
            "20,155 chunks over 5,183 of 5,183 documents, max 25 from one, pooled on all 1,109 " +
            "queries — a count over every query in queries.jsonl; since Phase 3.15 cut retrieval " +
            "to the judged set, a re-run pools on at most the 300 judged and the nDCG is unchanged " +
            "by construction. Re-measured 2026-07-31 on the same machine, 643.1 s, after Phase 3.16 taught " +
            "RecursiveChunkingStrategy to pack split parts towards MaxChunkSize — the packing " +
            "lifted this leg from 3.12's 0.65589 (56,707 chunks, max 221) to 0.67742, +0.03148 " +
            "against a parity leg the fix did not move."),
        new(
            "fiqa",
            BeirProtocol.Parity,
            [0.37086],
            "One separator — FiQA titles none of its 57,638 documents. Measured 2026-07-31, 1 h " +
            "11 m for 64,247 embeddings, retrieving for all 6,648 queries; since Phase 3.15 cut " +
            "retrieval to the 648 judged, a re-run embeds ~6,000 fewer query texts and the nDCG " +
            "is unchanged by construction. GATED: BeirRunBudget keeps this case behind " +
            "RAGNET_BEIR_LONG_RUNS, so nothing re-checks this figure unless somebody asks for it."),
        new(
            "fiqa",
            BeirProtocol.Real,
            [0.35569],
            "121,236 units over 57,600 of 57,638 documents, max 41 from one — 38 corpus entries " +
            "have an empty title and an empty text and so yield no chunks, one of them (117276) " +
            "judged relevant, so this leg indexes 38 fewer documents than parity. Pooled two or " +
            "more units on all 648 judged queries. First measured 2026-08-02 (Phase 3.15), same " +
            "machine: real leg 3,587.5 s (59.8 min), whole test 1 h 4 m — the derived ~1.5-2 h " +
            "estimate it replaces was conservative. Recall@10 0.42235, MRR@10 0.42596; the " +
            "parity leg in the same run reproduced 0.37086 exactly, so the delta is -0.01517."),
        new(
            "arguana",
            BeirProtocol.Parity,
            [0.50432],
            "Both separators, which agree. All 1,406 queries judged. Measured 2026-07-31; ~50 s " +
            "warm, and one of the two parity legs the nightly still runs unasked."),
        new(
            "arguana",
            BeirProtocol.Real,
            [0.47559],
            "24,003 chunks over 8,674 documents, max 16 from one, pooled on all 1,406 queries. " +
            "Re-measured 2026-07-31, 667.1 s, under Phase 3.16's packing chunker. Its delta of " +
            "-0.02873 against the unmoved parity leg is still the headline number and is pinned by " +
            "pinning both legs — packing recovered about 63% of 3.12's -0.07839 (0.42594, 82,618 " +
            "chunks, max 285), which is what design §6 predicted if fragmentation was the cause."),
        new(
            "scifact",
            BeirProtocol.HybridBm25,
            [0.69913],
            "Parity corpus, dense fused with InMemoryBm25Index via RRF — this repository's own " +
            "reproduction, comparable to the dense anchor and to no published BM25 or hybrid " +
            "figure. Recall@10 0.83933, MRR@10 0.65676. Measured in Phase 3.15 (2026-08-01/02), " +
            "Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.Hyde,
            [0.70001],
            "Parity corpus searched with the mean of 3 cached gpt-4o-mini@t0.8 hypotheticals — " +
            "this repository's own reproduction, comparable to nothing published. Recall@10 " +
            "0.85033, MRR@10 0.65563. Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET " +
            "10, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.Reranked,
            [0.68442],
            "Dense top-k rescored by cross-encoder/ms-marco-MiniLM-L6-v2 — this repository's own " +
            "reproduction, comparable to nothing published. Recall@10 0.78667, MRR@10 0.65789. " +
            "Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime, " +
            "after commit a912187 replaced the whitespace word-lookup tokenizer with WordPiece. " +
            "The pre-fix run measured 0.56693 — history, not a figure to reproduce: a run " +
            "landing there again means the tokenizer regressed."),
        new(
            "fiqa",
            BeirProtocol.HybridBm25,
            [0.35665],
            "Parity corpus, dense fused with InMemoryBm25Index via RRF — this repository's own " +
            "reproduction, comparable to the dense anchor and to no published BM25 or hybrid " +
            "figure. Recall@10 0.43951, MRR@10 0.42914. Measured in Phase 3.15 (2026-08-01/02), " +
            "Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.Hyde,
            [0.36543],
            "Parity corpus searched with the mean of 3 cached gpt-4o-mini@t0.8 hypotheticals — " +
            "this repository's own reproduction, comparable to nothing published. Recall@10 " +
            "0.44738, MRR@10 0.43124. Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET " +
            "10, CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.Reranked,
            [0.38458],
            "Dense top-k rescored by cross-encoder/ms-marco-MiniLM-L6-v2 — this repository's own " +
            "reproduction, comparable to nothing published, and the ablation table's only " +
            "reranker lift. Recall@10 0.44295, MRR@10 0.46744. Measured in Phase 3.15 " +
            "(2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime, after commit a912187's " +
            "WordPiece fix. The pre-fix run measured 0.34085 — history, not a figure to " +
            "reproduce."),
        new(
            "arguana",
            BeirProtocol.HybridBm25,
            [0.51173],
            "Parity corpus, dense fused with InMemoryBm25Index via RRF — this repository's own " +
            "reproduction, comparable to the dense anchor and to no published BM25 or hybrid " +
            "figure. Recall@10 0.80228, MRR@10 0.42141. Measured in Phase 3.15 (2026-08-01/02), " +
            "Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "arguana",
            BeirProtocol.Hyde,
            [0.50293],
            "Parity corpus searched with the mean of 3 cached gpt-4o-mini@t0.8 hypotheticals — " +
            "this repository's own reproduction, comparable to nothing published; the design's " +
            "negative control, and it held. Recall@10 0.79516, MRR@10 0.41258. Measured in " +
            "Phase 3.15 (2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "arguana",
            BeirProtocol.Reranked,
            [0.47917],
            "Dense top-k rescored by cross-encoder/ms-marco-MiniLM-L6-v2 — this repository's own " +
            "reproduction, comparable to nothing published. Recall@10 0.79374, MRR@10 0.38188. " +
            "Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime, " +
            "after commit a912187's WordPiece fix. The pre-fix run measured 0.41806 — history, " +
            "not a figure to reproduce."),
        new(
            "scifact",
            BeirProtocol.Comparison,
            [0.64593],
            "The library-comparison control row: the parity rankings written to a TREC run file, " +
            "read back, and scored from the read-back — and it reproduced the parity figure " +
            "EXACTLY, as determinism on one machine says it must: same rankings in, same " +
            "rankings off the disk, same metric. Recall@10 0.78667, MRR@10 0.60483, 300 queries " +
            "evaluated. Measured 2026-08-02 (Phase 3.14 Task 2), Windows 11, .NET 10, CPU ONNX " +
            "Runtime. The control also asserts against the parity entry, which is the acceptance " +
            "gate; this entry is the row's own pin."),
        new(
            "fiqa",
            BeirProtocol.Comparison,
            [],
            "The library-comparison control row: the parity rankings written to a TREC run file, " +
            "read back, and scored from the read-back. NEVER RUN — gated with FiQA's parity leg " +
            "behind RAGNET_BEIR_LONG_RUNS, whose cold price is 1 h 11 m. The acceptance gate — " +
            "that it reproduce parity's 0.37086 — is asserted by BeirComparisonControlTests " +
            "against the parity entry the day somebody pays for the run, so this empty entry " +
            "does not weaken the gate; when that run completes, its figure belongs here."),
        new(
            "arguana",
            BeirProtocol.Comparison,
            [0.50432],
            "The library-comparison control row: the parity rankings written to a TREC run file, " +
            "read back, and scored from the read-back — and it reproduced the parity figure " +
            "EXACTLY, with the self-exclusion applied on the writer's side of the boundary, " +
            "before the file: the run file already holds the post-exclusion top 10, which is " +
            "what makes it scoreable by an outsider's trec_eval with no knowledge of query ids. " +
            "Recall@10 0.79161, MRR@10 0.41515, 1,406 queries evaluated. Measured 2026-08-02 " +
            "(Phase 3.14 Task 2), Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.SemanticKernel,
            [0.64593],
            "The library-comparison Semantic Kernel row (SK 1.78.0, InMemory connector " +
            "1.74.0-preview, MEVD 10.1.0): unchunked documents embedded and searched through " +
            "SK's own paths with the pinned embedder, scored from a read-back TREC run file — " +
            "and it equals the control row's 0.64593 EXACTLY. Not an accident: SK ships no " +
            "chunker, so its default is one record per document, which is the parity protocol " +
            "over the same texts, the same vectors and the same cosine — the two rows can " +
            "differ only in tie-ordering, and SciFact's rankings held no ties that mattered. " +
            "Recall@10 0.78667, MRR@10 0.60483, 300 queries evaluated. Measured 2026-08-02 " +
            "(Phase 3.14 Task 4), Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.SemanticKernel,
            [],
            "The library-comparison Semantic Kernel row (SK 1.78.0, InMemory connector " +
            "1.74.0-preview, MEVD 10.1.0): unchunked documents embedded and searched through " +
            "SK's own paths with the pinned embedder, scored from a read-back TREC run file. " +
            "NEVER RUN — gated with FiQA's parity leg behind RAGNET_BEIR_LONG_RUNS, whose cold " +
            "price is 1 h 11 m of embedding SK's row would pay identically (same texts, same " +
            "model); when somebody pays for the run, its figure belongs here."),
        new(
            "arguana",
            BeirProtocol.SemanticKernel,
            [0.50306, 0.50321, 0.50399],
            "The library-comparison Semantic Kernel row (SK 1.78.0, InMemory connector " +
            "1.74.0-preview, MEVD 10.1.0): unchunked documents embedded and searched through " +
            "SK's own paths with the pinned embedder, self-exclusion applied on the writer's " +
            "side of the boundary (the written file held zero query-id = document-id lines " +
            "over all 14,060), scored from a read-back TREC run file. THREE FIGURES FROM ONE " +
            "MACHINE, not three machines: this case is not deterministic run to run, because " +
            "the entrant deliberately keeps SK's returned order (re-sorting would measure our " +
            "tie-break, not SK), the connector does not order exactly-equal cosine scores " +
            "repeatably across processes, and ArguAna is exact-tie-heavy — 1,298 of 1,406 " +
            "queries are byte-identical to their own corpus document. Three runs measured " +
            "2026-08-02, Windows 11, .NET 10, CPU ONNX Runtime (the Phase 3.14 Task 4 run, " +
            "the run file that session left in the cache, and the whole-phase reviewer's " +
            "fresh run): nDCG@10 0.50306 / 0.50321 / 0.50399, MRR@10 0.41339 / 0.41361 / " +
            "0.41471 — and Recall@10 0.79161 in ALL THREE, IDENTICAL to the control's: the " +
            "same documents reach the top 10 every run and only exact-tie order moves, " +
            "0.00033-0.00126 of nDCG below the control's 0.50432, not a retrieval " +
            "difference. 1,406 queries evaluated."),
        new(
            "scifact",
            BeirProtocol.LangChain,
            [0.64613],
            "The library-comparison LangChain row (langchain-core 1.5.3, " +
            "langchain-text-splitters 1.1.2, Python 3.14.5, onnxruntime 1.28.0): " +
            "RecursiveCharacterTextSplitter at its defaults (4000 characters, 200 overlap), " +
            "InMemoryVectorStore cosine, pinned all-MiniLM-L6-v2 behind LangChain's Embeddings " +
            "interface, max-pooled to documents writer-side and scored from the read-back TREC " +
            "run file. Measured 2026-08-02 (Phase 3.14 Stage 2), Windows 11, CPU ONNX Runtime; " +
            "the Python-side embedder was verified against OnnxEmbeddingGenerator on a " +
            "six-string battery, all bitwise-equal (identity_check.py), before any Python " +
            "figure was trusted."),
        new(
            "fiqa",
            BeirProtocol.LangChain,
            [],
            "The library-comparison LangChain row. NEVER RUN — producing the run file embeds " +
            "FiQA's chunked corpus through the Python-side pinned embedder, derived to cost " +
            "roughly an hour (FiQA's .NET parity leg measured 1 h 11 m for comparable " +
            "embedding work); Phase 3.14 Stage 2 ran SciFact and ArguAna and deliberately not " +
            "FiQA. When somebody pays for the run, its figure belongs here."),
        new(
            "arguana",
            BeirProtocol.LangChain,
            [0.50450],
            "The library-comparison LangChain row (langchain-core 1.5.3, " +
            "langchain-text-splitters 1.1.2, Python 3.14.5, onnxruntime 1.28.0), self-exclusion " +
            "applied on the writer's side of the boundary (the written file held zero " +
            "query-id = document-id lines). Measured 2026-08-02 (Phase 3.14 Stage 2), Windows " +
            "11, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.LlamaIndex,
            [0.64508],
            "The library-comparison LlamaIndex row (llama-index-core 0.14.23, Python 3.14.5, " +
            "onnxruntime 1.28.0): SentenceSplitter at its defaults (1024 cl100k tokens, 200 " +
            "overlap), SimpleVectorStore cosine, pinned all-MiniLM-L6-v2 behind " +
            "Settings.embed_model, max-pooled to documents writer-side and scored from the " +
            "read-back TREC run file. Measured 2026-08-02 (Phase 3.14 Stage 2), Windows 11, " +
            "CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.LlamaIndex,
            [],
            "The library-comparison LlamaIndex row. NEVER RUN — FiQA's Python-side embedding " +
            "cost is derived at roughly an hour per entrant; Phase 3.14 Stage 2 ran SciFact " +
            "and ArguAna and deliberately not FiQA. When somebody pays for the run, its " +
            "figure belongs here."),
        new(
            "arguana",
            BeirProtocol.LlamaIndex,
            [0.50450],
            "The library-comparison LlamaIndex row (llama-index-core 0.14.23, Python 3.14.5, " +
            "onnxruntime 1.28.0), self-exclusion applied on the writer's side of the boundary " +
            "(the written file held zero query-id = document-id lines). Measured 2026-08-02 " +
            "(Phase 3.14 Stage 2), Windows 11, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.Haystack,
            [0.62757],
            "The library-comparison Haystack row (haystack-ai 3.0.0, Python 3.14.5, " +
            "onnxruntime 1.28.0): DocumentSplitter at its defaults (200 words, 0 overlap), " +
            "InMemoryDocumentStore under its default dot_product similarity (the pinned " +
            "vectors are unit-length, so dot product and cosine coincide), pinned " +
            "all-MiniLM-L6-v2 filling Document.embedding, max-pooled to documents writer-side " +
            "and scored from the read-back TREC run file. Measured 2026-08-02 (Phase 3.14 " +
            "Stage 2), Windows 11, CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.Haystack,
            [],
            "The library-comparison Haystack row. NEVER RUN — FiQA's Python-side embedding " +
            "cost is derived at roughly an hour per entrant, likely more for Haystack, whose " +
            "200-word default produces the most chunks of the three; Phase 3.14 Stage 2 ran " +
            "SciFact and ArguAna and deliberately not FiQA. When somebody pays for the run, " +
            "its figure belongs here."),
        new(
            "arguana",
            BeirProtocol.Haystack,
            [0.49715],
            "The library-comparison Haystack row (haystack-ai 3.0.0, Python 3.14.5, " +
            "onnxruntime 1.28.0), self-exclusion applied on the writer's side of the boundary " +
            "(the written file held zero query-id = document-id lines). Measured 2026-08-02 " +
            "(Phase 3.14 Stage 2), Windows 11, CPU ONNX Runtime."),
        new(
            "trec-covid",
            BeirProtocol.Parity,
            [0.45427],
            "Both separators, and they agree to five decimals. Recall@10 0.01292, MRR@10 0.72438, " +
            "50 judged queries, 171,332 units over 171,332 documents. Measured 2026-08-12, " +
            "Windows 11, .NET 10, CPU ONNX Runtime; 3,765.3 s cold then 2,847.6 s warm, 1 h 50 m " +
            "for the pair. Phase 5.3's dataset, and the one that makes IrMetrics' graded gain " +
            "mean something: its qrels carry 14,217 rows at grade 2 where FiQA's 17,110 " +
            "judgements are every one of them exactly 1. " +
            "**This is the worst agreement with published of the four datasets, by roughly 7x.** " +
            "MTEB records 0.47232 at revision bb9466ba; this measured 0.45427, a delta of " +
            "-0.01805 against +0.00085, +0.00219 and +0.00265 for SciFact, FiQA and ArguAna. It " +
            "passes the +/-0.02 band with 0.0023 to spare, which is a pass and not a comfortable " +
            "one, and it is the only negative delta of the four. One run, so the spread is " +
            "unknown. Treat a future drift here as more likely to be real than the same drift on " +
            "the other three, and treat the band as having nearly failed rather than as having " +
            "confirmed the harness on this corpus."),
        new(
            "trec-covid",
            BeirProtocol.Real,
            [],
            "NEVER RUN. The chunking leg costs the parity leg's embedding again over 171,332 " +
            "documents, and Phase 5.3 bought the parity leg only. When somebody pays for the " +
            "run, its figure belongs here."),
        new(
            "trec-covid",
            BeirProtocol.HybridBm25,
            [],
            "NEVER RUN. Phase 5.3 lands the parity leg, which is the one with a published figure " +
            "to be checked against; a hybrid figure here would be this repository's own number " +
            "comparable to nothing outside it."),
        new(
            "trec-covid",
            BeirProtocol.Hyde,
            [],
            "NEVER RUN, and not runnable on a fresh machine at any budget: the leg needs the " +
            "hypothetical cache, which only the generation tool writes and which is never " +
            "committed. The same is true of every other dataset's Hyde cell."),
        new(
            "trec-covid",
            BeirProtocol.Reranked,
            [],
            "NEVER RUN. It would be the suite's most expensive reranker cell -- the " +
            "cross-encoder scores every retrieved candidate for each of 50 judged queries " +
            "against a corpus judged far more densely than the other three, averaging 493.5 " +
            "relevant documents per query."),
        new(
            "trec-covid",
            BeirProtocol.Comparison,
            [],
            "NEVER RUN, and deliberately so. The library comparison's corpora were fixed by " +
            "Phase 5.1's published matrix at SciFact, ArguAna and FiQA. This entry exists " +
            "because a descriptor added to BeirDatasetDescriptor.All joins the control theory " +
            "whether or not anyone intended it to -- which is not a hypothetical: it happened " +
            "on 2026-08-11, seven minutes into a cost sweep, when the new case tried to prefetch " +
            "171,382 vectors from a cold cache."),
        new(
            "trec-covid",
            BeirProtocol.SemanticKernel,
            [],
            "NEVER RUN, for the Comparison entry's reason: the Semantic Kernel entrant exists to " +
            "sit beside the control in Phase 5.1's matrix, over that matrix's three corpora."),
        new(
            "trec-covid",
            BeirProtocol.LangChain,
            [],
            "NEVER RUN, for the Comparison entry's reason."),
        new(
            "trec-covid",
            BeirProtocol.LlamaIndex,
            [],
            "NEVER RUN, for the Comparison entry's reason."),
        new(
            "trec-covid",
            BeirProtocol.Haystack,
            [],
            "NEVER RUN, for the Comparison entry's reason."),
        new(
            "multihop-rag",
            BeirProtocol.Real,
            [0.63967],
            "MEASURED 2026-08-12 on Windows 11, .NET 10, CPU ONNX Runtime. Rag.NET's own " +
            "chunking over the 609 converted articles, max-pooled back to documents: 17,648 " +
            "units over 609 of 609 documents, up to 201 from a single article, none of them " +
            "contributing nothing. All 2,255 judged queries evaluated, none excluded for want of " +
            "a positive judgement, and every one of them retrieved two or more units of one " +
            "document -- so max-pooling is exercised on the whole query set here rather than on " +
            "a fraction of it, which is not true of any other corpus in this table. " +
            "Recall@10 = 0.78684, MRR@10 = 0.70150. " +
            "**Measured twice, and the two agree to five decimals.** The first run took 392.6 s " +
            "off a partly warm cache, the second 600.2 s cold on a machine under other load; the " +
            "cache state and the load moved the wall clock by 1.6x and moved nDCG@10, Recall@10 " +
            "and MRR@10 by nothing at all. That is the expected result -- the protocol is " +
            "deterministic given the corpus, the pinned model and the judged set -- but it is " +
            "worth recording, because it separates the figure pinned here from the timing in " +
            "BeirRunBudget, which is load-sensitive and was flagged there as an upper bound until " +
            "#174 re-took it idle and cold, twice, on 2026-08-15 -- 343.5 s and 388.5 s, and a " +
            "third and fourth reproduction of every figure here to five decimals. " +
            "**This figure is pinned on its own authority, and that phrase is doing real work.** " +
            "No published figure exists for all-MiniLM-L6-v2 on this dataset under any metric " +
            "this repository reports. The paper's Table 5 gives MAP@K, MRR@K and Hit@K for " +
            "ada-002, llm-embedder, bge-large-en-v1.5, jina-v2, e5-base-v2, voyage-02 and " +
            "instructor-large -- there is no MiniLM row -- and MTEB does not carry the dataset " +
            "as a retrieval task at all, so the source every other published figure here comes " +
            "from holds nothing for it either. Both the model and the metric differ from " +
            "anything in the literature, so nothing outside this repository checked 0.63967 and " +
            "nothing outside it can. What the number is good for is drift: the next run is " +
            "compared against this one, and a later reader must not mistake a self-pinned figure " +
            "for a reproduced one. " +
            "The companion parity leg read 0.55724 (Recall@10 = 0.68906, MRR@10 = 0.63748, 609 " +
            "units, 41.1 s), giving a chunking delta of +0.08243 -- the largest in the suite, and " +
            "expected on a corpus whose articles average 10,340 characters, because the parity " +
            "protocol truncates each at 256 tokens and simply cannot see most of the document " +
            "the chunked leg indexes. That leg is deliberately NOT pinned and carries no entry: " +
            "the descriptor declares Parity inapplicable for exactly the reason the delta is " +
            "large, and it is measured here only as the control this delta is subtracted from."),
        new(
            "multihop-rag",
            BeirProtocol.GraphRag,
            [0.56897],
            "MEASURED 2026-08-15 on Windows 11, .NET 10.0.11, CPU ONNX Runtime -- the same " +
            "machine as every other figure in this file. BeirGraphRagCorpusTests over the WHOLE " +
            "609-article corpus under the graph path, local search max-pooled to documents, all " +
            "2,255 judged queries scored and none excluded for lacking a positive judgement. " +
            "Recall@10 = 0.71696, MRR@10 = 0.63302. 43 m 29 s wall clock (01:16-01:59): 1,338.1 s " +
            "to build the graph, 2,564.3 s for the scored local-search pass, 1,226.2 s for the " +
            "candidate-set control pass. 35,296 extraction requests and 3,587 report requests, " +
            "every one replayed from the cache -- no model was called. The graph: 62,392 " +
            "entities, 147,021 relationships, 3,587 communities. Indexed: 321,151 units over 609 " +
            "of 609 documents (17,648 article chunks + 299,916 entity/relationship chunks + " +
            "3,587 community reports), max 201 article chunks from one document, none " +
            "contributing nothing; largest community report prompt 49,938 characters; embedding " +
            "cache 145,840 hits against 177,566 misses. " +
            "**THIS FIGURE IS REPRODUCIBLE ONLY WITH #231 (fix/230-local-search-dedup-key) " +
            "APPLIED, and that fix must land before or with this entry.** It was measured in a " +
            "worktree carrying main + the #173 harness (#229) + #231. Until #231, " +
            "GraphLocalSearchBehavior keyed its deduplication on ChunkIndex alone rather than on " +
            "(DocumentId, ChunkIndex): entity chunks use per-document negative indices and " +
            "article chunks 0..n, so across 609 documents candidates from DIFFERENT documents " +
            "collided and roughly a third of every candidate set was discarded -- arbitrarily by " +
            "document rather than by relevance. This run shows the fix in place and says so in " +
            "its own counters: 500.0 chunks in, 500.0 out, 0.0 dropped by the deduplication, " +
            "85.8 distinct documents in and 85.8 out. Run without #231, this case is measuring a " +
            "different behaviour and 0.56897 will not come back. " +
            "**THE FINDING: GRAPHRAG DOES NOT HELP ON THIS DATASET. IT HURTS.** " +
            "**And the honest comparison is against the CANDIDATE-SET CONTROL, not against the " +
            "Real leg's 0.63967.** The control scores the SAME dense top-500 the behaviour was " +
            "handed, over the SAME 321,151-chunk store, so store size and candidate depth are " +
            "held constant and the graph behaviour is the only variable: nDCG@10 = 0.59658, " +
            "Recall@10 = 0.74254, MRR@10 = 0.66785. Against it the graph path is **-0.02761**, " +
            "and Recall@10 and MRR@10 move the same way (-0.02558 and -0.03483). That is the " +
            "comparison that answers 'does the graph path help', because it is the only one in " +
            "which the graph behaviour is the sole difference. " +
            "**The delta against the Real leg's 0.63967 is -0.07070, it is DEPTH-CONFOUNDED, and " +
            "it must never be quoted without that.** The Real leg searched a 17,648-chunk store " +
            "at top-2,010 per query; this one searched 321,151 at top-500. Both numbers belong " +
            "in any account of this run -- the confounded one because it is the difference a " +
            "reader coming from the Real entry will compute anyway -- and the control is the one " +
            "that carries the claim. " +
            "**One visible mechanism, and it is honestly part artefact and part real.** A " +
            "community report reached the top 10 on 891 of the 2,255 queries, 39.5% of them. " +
            "Community reports are indexed under a synthetic document id that no qrels row " +
            "judges, so each of those is a rank slot scoring zero. The artefact half: a user " +
            "asking a synthesis question might genuinely WANT the report, and this protocol has " +
            "no way to credit it. The real half: the slot is spent either way, so a judged " +
            "document is displaced down the ranking for a reader who wanted a document. Both " +
            "halves are true and this entry does not resolve the tension by asserting one of " +
            "them. The reports are left in rather than filtered out because a pipeline that " +
            "returns them is a pipeline whose caller sees them, and hiding them here would " +
            "flatter the run. " +
            "**What this does NOT say.** One dataset, one embedder (all-MiniLM-L6-v2), one " +
            "implementation. It says that GraphRAG's local search AS IMPLEMENTED HERE does not " +
            "beat plain dense scoring of the same candidates on MultiHop-RAG. It does not say " +
            "GraphRAG is worthless, and nobody may generalise it that far from a single corpus. " +
            "**Pinned on its own authority**, like the Real entry and for the same reason: no " +
            "published figure exists for all-MiniLM-L6-v2 on this dataset under any metric this " +
            "repository reports, so nothing outside this repository checked 0.56897 and nothing " +
            "outside it can. What it is good for is drift. " +
            "**Global search was exercised and deliberately NOT scored**, which is a decision " +
            "rather than a gap: it map-reduces community reports into a synthesised answer while " +
            "these qrels judge documents, so an nDCG for it would be a category error wearing a " +
            "comparison's clothes. Described from this run: query mhr-0000 returned 491 results " +
            "through 3 map/reduce calls in 44.4 s, it reached its reports in 1 retrieval, and " +
            "the best community report ranks 50 in an unfiltered scan of the whole store. " +
            "**The chunking confound that would have made the delta meaningless was checked " +
            "before the harness was built, and it is clear.** The extraction cache was filled " +
            "through the generation tool's chunker and the Real entry's 0.63967 through the Real " +
            "protocol's; measured 2026-08-13, both cut the 609 articles into the identical " +
            "17,648 units, text for text, document for document, index for index -- asserted " +
            "every run by Chunking_UnderTheGraphPath_IsIdenticalToTheRealProtocols, which needs " +
            "no model and takes 165 ms. **What is NOT matched is candidate depth**, which is why " +
            "the control above exists and why the -0.07070 is the confounded number. " +
            "**The graph this figure was measured over is the post-#209 one, and the difference " +
            "is not cosmetic.** The model returned two relationship weights as acquisition " +
            "prices -- Microsoft/Mojang at 2.5e9, Microsoft/Rare at 3.75e8 -- and those two " +
            "edges of 147,021 carried 99.99% of the graph's total weight, putting 57,484 of the " +
            "62,392 entities (92.13%) in ONE community at modularity 0.0001. Bounding the weight " +
            "at extraction (GraphRagOptions.MaxRelationshipWeight, default 10) alters 20 of the " +
            "147,021 and brings that to 5,629 entities, 9.02%, at modularity 0.7496. A run " +
            "against an unbounded graph would measure the degenerate clustering instead and is " +
            "not comparable to this. The pinned 60-article slice could never have shown it: its " +
            "heaviest weight is 6.0 and the bound alters none of its edges. " +
            "**GraphRagFunctionsTests still publishes no nDCG and this figure does not come from " +
            "it.** That guard runs the graph path over 60 of 609 documents and 27 of 2,255 " +
            "queries and asserts that the pipeline FUNCTIONS; a number computed from it would " +
            "not be comparable to anything, which is why the comparative run had to be the whole " +
            "corpus."),
        new(
            "multihop-rag",
            BeirProtocol.GraphRagDepthControl,
            [0.63967],
            "MEASURED 2026-08-15 on Windows 11, .NET 10.0.11, CPU ONNX Runtime -- the same " +
            "machine as every other figure in this file. Phase 5.2.1 (#232): the depth-matched " +
            "dense control, BeirRealChunkingTests' 17,648 article chunks indexed alone and " +
            "retrieved through BeirHarness.MeasureAsync at the graph path's candidate depth of " +
            "500 rather than the Real protocol's derived 2,010, max-pooled to documents over all " +
            "2,255 judged queries, none excluded. Three runs: 120.0 s, 8 s, 7.0 s -- identical figures, " +
            "an OS page-cache spread on the clock, BeirRunBudget has both ends -- off a warm embedding cache (19,903 " +
            "hits, 0 misses -- 17,648 units and 2,255 queries, every vector the Real leg had " +
            "already written). " +
            "**nDCG@10 = 0.63967, Recall@10 = 0.78684, MRR@10 = 0.70150 -- the Real leg's three " +
            "figures to five decimals.** Not close: identical. Cutting the candidate set from " +
            "2,010 to 500 changed the top-10 document ranking of not one of the 2,255 queries, " +
            "which says the ten best documents' best chunks always sit inside the dense top-500 " +
            "on this corpus. **DEPTH COSTS NOTHING HERE, and that settles what #232 asked.** " +
            "The -0.04309 between the graph run's candidate-set control (0.59658) and the Real " +
            "leg is store pollution, entire and alone: 303,503 entity, relationship and " +
            "community-report units competing with 17,648 judged article chunks for rank at " +
            "the same depth over the same article text. " +
            "**So the GraphRag entry's framing above is corrected, not merely refined.** It " +
            "calls the -0.07070 against the Real leg 'depth-confounded' and says it must never " +
            "be quoted without that. Measured, the depth contributes 0.00000 of it; the -0.07070 " +
            "decomposes exactly into -0.04309 of store pollution and -0.02761 of graph " +
            "behaviour, and both halves are attributable. The word 'depth-confounded' was a " +
            "plausible reading of two protocols that differed in two things at once, written " +
            "before anybody had moved one of them alone. It is left standing in that entry, " +
            "with this one beside it, because a corrected record is more useful than a clean " +
            "one. " +
            "**What follows from it, and what does not.** The pollution is a ranking-policy " +
            "problem, not a graph-quality one: the graph-derived units are competing at the " +
            "same depth over the same article text, so a filter or a rank policy for synthetic " +
            "chunks is the lever, and it applies to RAPTOR too, which also indexes synthetic " +
            "chunks beside real ones. That is a design question and it is not decided here. " +
            "What this figure does NOT say is that top-500 is a safe depth in general -- it " +
            "says the top-10 documents survived the cut on this corpus with this chunker, where " +
            "the deepest document runs to 201 chunks; a corpus whose top documents each " +
            "contribute more chunks above the tenth document's best could lose it. " +
            "**Pinned on its own authority**, like the Real and GraphRag entries: no published " +
            "figure exists for this configuration, and its job is drift. A re-run that lands " +
            "away from the Real leg's figure has moved either the chunker or the pooling, " +
            "because this run holds everything else still."),
            new(
            "scifact",
            BeirProtocol.SemanticChunking,
            [0.64551],
            "MEASURED 2026-08-26 on Windows 11, .NET 10.0.11, CPU ONNX Runtime — Phase 6.2.1. "
            + "SemanticChunkingStrategy at its defaults (breakpoint percentile 0.25, minimum 100 "
            + "characters), same corpus, embedder and retrieval as this dataset's Parity cell, "
            + "differing only in where the boundaries fall. "
            + "**The control is the PARITY figure, not Real**: this cell runs under the ablation "
            + "table's protocol, and against Real instead SciFact's wash reads as a 0.032 "
            + "regression. "
            + "**nDCG@10 0.64551 against the parity control's 0.64593 — -0.00042, a wash.** "
            + "13,103 units over 5,183 documents, max 18 per document, none dropped. "
            + "**The pattern across the four is document length.** Semantic chunking costs "
            + "accuracy on corpora of short documents and buys it on long ones: SciFact abstracts "
            + "-0.00042, ArguAna single arguments -0.02930, FiQA answers -0.02577, TREC-COVID "
            + "full-text articles +0.06769. TREC-COVID is also the only corpus where a document "
            + "reached 418 units; the other three top out at 12 to 22. Splitting a document that "
            + "was already one idea can only dilute it, and max-pooling cannot recover what the "
            + "split threw away."
            ),
        new(
            "fiqa",
            BeirProtocol.SemanticChunking,
            [0.34509],
            "MEASURED 2026-08-26 on Windows 11, .NET 10.0.11, CPU ONNX Runtime — Phase 6.2.1. "
            + "SemanticChunkingStrategy at its defaults (breakpoint percentile 0.25, minimum 100 "
            + "characters), same corpus, embedder and retrieval as this dataset's Parity cell, "
            + "differing only in where the boundaries fall. "
            + "**The control is the PARITY figure, not Real**: this cell runs under the ablation "
            + "table's protocol, and against Real instead SciFact's wash reads as a 0.032 "
            + "regression. "
            + "**nDCG@10 0.34509 against the parity control's 0.37086 — -0.02577.** 101,193 units "
            + "over 57,600 of 57,638 documents, max 22 per document. **38 documents produced no "
            + "units and are absent from the index**; the chunker yields nothing only for "
            + "whitespace-only text, so those 38 are empty in BEIR's FiQA — asserted by the cell "
            + "rather than assumed, because a chunker dropping documents with content would "
            + "otherwise show up only as an nDCG nobody could account for. "
            + "**The pattern across the four is document length.** Semantic chunking costs "
            + "accuracy on corpora of short documents and buys it on long ones: SciFact abstracts "
            + "-0.00042, ArguAna single arguments -0.02930, FiQA answers -0.02577, TREC-COVID "
            + "full-text articles +0.06769. TREC-COVID is also the only corpus where a document "
            + "reached 418 units; the other three top out at 12 to 22. Splitting a document that "
            + "was already one idea can only dilute it, and max-pooling cannot recover what the "
            + "split threw away."
            ),
        new(
            "arguana",
            BeirProtocol.SemanticChunking,
            [0.47502],
            "MEASURED 2026-08-26 on Windows 11, .NET 10.0.11, CPU ONNX Runtime — Phase 6.2.1. "
            + "SemanticChunkingStrategy at its defaults (breakpoint percentile 0.25, minimum 100 "
            + "characters), same corpus, embedder and retrieval as this dataset's Parity cell, "
            + "differing only in where the boundaries fall. "
            + "**The control is the PARITY figure, not Real**: this cell runs under the ablation "
            + "table's protocol, and against Real instead SciFact's wash reads as a 0.032 "
            + "regression. "
            + "**nDCG@10 0.47502 against the parity control's 0.50432 — -0.02930, the largest "
            + "loss of the four.** 16,059 units over 8,674 documents, max 12 per document, none "
            + "dropped. ArguAna documents are single arguments, which is the shape splitting "
            + "helps least. "
            + "**The pattern across the four is document length.** Semantic chunking costs "
            + "accuracy on corpora of short documents and buys it on long ones: SciFact abstracts "
            + "-0.00042, ArguAna single arguments -0.02930, FiQA answers -0.02577, TREC-COVID "
            + "full-text articles +0.06769. TREC-COVID is also the only corpus where a document "
            + "reached 418 units; the other three top out at 12 to 22. Splitting a document that "
            + "was already one idea can only dilute it, and max-pooling cannot recover what the "
            + "split threw away."
            ),
        new(
            "trec-covid",
            BeirProtocol.SemanticChunking,
            [0.52196],
            "MEASURED 2026-08-26 on Windows 11, .NET 10.0.11, CPU ONNX Runtime — Phase 6.2.1. "
            + "SemanticChunkingStrategy at its defaults (breakpoint percentile 0.25, minimum 100 "
            + "characters), same corpus, embedder and retrieval as this dataset's Parity cell, "
            + "differing only in where the boundaries fall. "
            + "**The control is the PARITY figure, not Real**: this cell runs under the ablation "
            + "table's protocol, and against Real instead SciFact's wash reads as a 0.032 "
            + "regression. "
            + "**nDCG@10 0.52196 against the parity control's 0.45427 — +0.06769, and the only "
            + "gain of the four.** 360,345 units over 171,331 of 171,332 documents, max 418 "
            + "per document — an order more splitting than any other corpus here, on the only "
            + "corpus of full-text articles. One document produced no units and is empty. "
            + "**The pattern across the four is document length.** Semantic chunking costs "
            + "accuracy on corpora of short documents and buys it on long ones: SciFact abstracts "
            + "-0.00042, ArguAna single arguments -0.02930, FiQA answers -0.02577, TREC-COVID "
            + "full-text articles +0.06769. TREC-COVID is also the only corpus where a document "
            + "reached 418 units; the other three top out at 12 to 22. Splitting a document that "
            + "was already one idea can only dilute it, and max-pooling cannot recover what the "
            + "split threw away."
            ),
];

    /// <summary>
    /// Asserts one measurement reproduced what this repository last recorded for that case.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name, as it appears in the theory data.</param>
    /// <param name="protocol">Which protocol produced <paramref name="measuredNdcgAt10"/>.</param>
    /// <param name="measuredNdcgAt10">What the run just reported.</param>
    /// <param name="output">Where the "nothing recorded yet" note is written.</param>
    /// <exception cref="InvalidOperationException">Nothing is recorded for that pair at all.</exception>
    public static void AssertReproduces(
        string datasetName,
        BeirProtocol protocol,
        double measuredNdcgAt10,
        ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var recorded = Find(datasetName, protocol);
        if (recorded.NdcgAt10.Count == 0)
        {
            output.WriteLine(NothingRecordedYet(recorded, measuredNdcgAt10));
            return;
        }

        Assert.True(
            Reproduces(recorded, measuredNdcgAt10),
            Explain(recorded, measuredNdcgAt10));
    }

    /// <summary>
    /// Provokes the table lookup for one case and compares nothing.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name.</param>
    /// <param name="protocol">Which protocol the case measures under.</param>
    /// <exception cref="InvalidOperationException">Nothing is recorded for that pair at all.</exception>
    /// <remarks>
    /// So <see cref="BeirReproductionTests"/> can assert that every described dataset has an entry
    /// without inventing an nDCG to compare against — a plausible figure there would make that test
    /// quietly depend on which numbers the table happens to hold, and an implausible one would fail
    /// it.
    /// </remarks>
    public static void RequireRecordedCase(string datasetName, BeirProtocol protocol) =>
        _ = Find(datasetName, protocol);

    /// <summary>Reports whether the table holds an entry for one pair, without throwing when it does not.</summary>
    /// <param name="datasetName">The BEIR dataset name.</param>
    /// <param name="protocol">The protocol to ask about.</param>
    /// <returns><see langword="true"/> when the table holds an entry for that pair.</returns>
    /// <remarks>
    /// <see cref="AssertReproduces"/> and <see cref="RequireRecordedCase"/> both go through
    /// <c>Find</c>, which throws on an absent pair — correct for them, and useless for asking
    /// whether a pair is absent.
    /// </remarks>
    public static bool HasReproduction(string datasetName, BeirProtocol protocol)
    {
        foreach (var reproduction in Reproductions)
        {
            if (string.Equals(reproduction.Dataset, datasetName, StringComparison.Ordinal)
                && reproduction.Protocol == protocol)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reports whether the measurement lands within tolerance of any recorded figure.</summary>
    private static bool Reproduces(Reproduction recorded, double measured)
    {
        for (var i = 0; i < recorded.NdcgAt10.Count; i++)
        {
            if (Math.Abs(measured - recorded.NdcgAt10[i]) <= Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the recorded reproduction for one dataset under one protocol.</summary>
    /// <exception cref="InvalidOperationException">The pair is not in the table.</exception>
    private static Reproduction Find(string datasetName, BeirProtocol protocol)
    {
        foreach (var reproduction in Reproductions)
        {
            if (string.Equals(reproduction.Dataset, datasetName, StringComparison.Ordinal)
                && reproduction.Protocol == protocol)
            {
                return reproduction;
            }
        }

        throw new InvalidOperationException(
            $"No reproduction is recorded for dataset '{datasetName}' under the {protocol} " +
            $"protocol. A dataset was added to {nameof(BeirDatasetDescriptor)}.All and its number " +
            $"is pinned by nothing: the published band is ±{BeirParityTarget.DefaultTolerance:F2} " +
            "and the real run's envelope is half to one-and-a-half times parity, so a regression " +
            $"of 0.015 would pass both green. Run it and add it to {nameof(BeirReproduction)}, " +
            "recording an empty figure list if it is a case nobody has run to completion.");
    }

    /// <summary>The line a case with no recorded figure prints instead of asserting.</summary>
    private static string NothingRecordedYet(Reproduction recorded, double measured) =>
        FormattableString.Invariant($"""
            NO REPRODUCTION RECORDED for {recorded.Dataset} under {recorded.Protocol}, so nothing was
            checked. This run measured nDCG@10 = {measured:F5}.
            Recorded instead: {recorded.Provenance}
            If this run finished, it is the figure — put it in {nameof(BeirReproduction)} with the
            machine and the date, and the next run will be checked against it.
            """);

    /// <summary>
    /// The message a drifted measurement leaves behind, and the reason it insists on being read
    /// before anything is edited.
    /// </summary>
    /// <remarks>
    /// The first instinct on a red band in this project has historically been to widen it, and the
    /// parity failure message already says not to. This one has an extra trap to name: the number it
    /// guards is <i>ours</i>, so "the published figure moved" is never the explanation, and the only
    /// two honest resolutions are a fixed regression or a second recorded machine.
    /// </remarks>
    private static string Explain(Reproduction recorded, double measured) =>
        FormattableString.Invariant($"""
            {recorded.Dataset} {recorded.Protocol} run measured nDCG@10 = {measured:F5}, which reproduces
            none of the figures recorded for it: {Format(recorded.NdcgAt10)} (±{Tolerance:F3}).
            THIS IS NOT A PARITY FAILURE. Nothing published is involved — the figures above are this
            repository's own, recorded as: {recorded.Provenance}
            The point of the check is that the ±{BeirParityTarget.DefaultTolerance:F2} published band and the
            real run's 0.5x-1.5x envelope are both wider than most defects: a cut-then-pool mutation of
            DocumentRanking moves this number by roughly 0.016-0.020 and passes both of them green.
            Two honest resolutions, and no third:
              1. Something regressed. Find it. The delta between the two protocols in this run is the
                 first thing to look at, then the run's own counters — units indexed, documents that
                 contributed nothing, and how many queries pooled.
              2. This is a different machine and the difference is legitimate. Add a SECOND figure to
                 this case naming the machine. Do NOT widen {nameof(Tolerance)}: that spends every
                 dataset's resolution to settle one runner.
            """);

    /// <summary>Renders the recorded figures for a message.</summary>
    private static string Format(IReadOnlyList<double> figures)
    {
        var rendered = new string[figures.Count];
        for (var i = 0; i < figures.Count; i++)
        {
            rendered[i] = FormattableString.Invariant($"{figures[i]:F5}");
        }

        return string.Join(", ", rendered);
    }

    /// <summary>What one case measured, where, and when.</summary>
    /// <param name="Dataset">The BEIR dataset name.</param>
    /// <param name="Protocol">The protocol measured.</param>
    /// <param name="NdcgAt10">
    /// Every nDCG@10 this case has legitimately reported — usually one per machine that has run
    /// it, but for a case whose tie ordering is not deterministic even on one machine (the
    /// SemanticKernel ArguAna entry) one per observed run, recording the spread. Empty means the
    /// case has never been run to completion, which <see cref="AssertReproduces"/> treats as
    /// "print it and check nothing" rather than as a failure.
    /// </param>
    /// <param name="Provenance">
    /// Where and when the figures came from, in prose. Not decoration: a reproduction check whose
    /// numbers have no machine attached is indistinguishable from a parity band, which is the one
    /// thing this must not be mistaken for.
    /// </param>
    private sealed record Reproduction(
        string Dataset,
        BeirProtocol Protocol,
        IReadOnlyList<double> NdcgAt10,
        string Provenance);
}
