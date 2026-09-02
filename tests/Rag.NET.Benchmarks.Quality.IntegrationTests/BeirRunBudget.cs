namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Which measurements the nightly can afford to run, and what the rest cost — the one place the
/// split between them is decided.
/// <para>
/// <b>Why there is a budget at all.</b> <c>nightly.yml</c>'s <c>env-gated</c> job has
/// <c>timeout-minutes: 120</c> and spends part of that restoring, building the whole solution and
/// running four other <c>&lt;RequiresSecrets&gt;</c> projects before this one starts. Phase 3.12
/// Task 4 added FiQA, whose parity run measures 1 h 11 m and whose real run — estimated at 8–9 h,
/// revised to a derived ~1.5–2 h by Phase 3.16's packing chunker — measured 1 h 4 m when Phase
/// 3.15 finally ran it. There is no arrangement of a 120-minute job in which those two
/// finish, so the job as it stood would have timed out — and <b>a timeout reports nothing about
/// parity</b>, which is the same silence this workflow was fixed to stop producing.
/// </para>
/// <para>
/// <b>Why the costs are the cold ones.</b> <c>RAGNET_BEIR_CACHE</c> points at
/// <c>RUNNER_TEMP/beir</c>, which is a fresh directory every night. Nothing is cached across runs,
/// so <see cref="EmbeddingCache"/> saves the nightly nothing and every figure recorded here is the
/// price paid from empty. The warm figures are quoted alongside only because they are what a
/// developer re-running a case locally will actually see, and a cost table that quoted them as the
/// cost would understate the nightly by an order of magnitude.
/// </para>
/// <para>
/// <b>What the nightly keeps, and why that is the right half.</b> SciFact and ArguAna under
/// <see cref="BeirProtocol.Parity"/> — roughly 15–20 minutes cold, all four cases — because parity
/// is the only protocol whose number can be checked against a published one, and that number is
/// what this milestone exists to protect. Everything else is opt-in. That is not a judgement that
/// the real runs matter less; it is that roughly 11 measured minutes each for ArguAna's and
/// SciFact's real legs (with the parity vectors already cached — the fully cold price is higher and
/// untimed, see their entries) on a hosted runner slower than the machine these were measured on is
/// how the timeout comes back.
/// </para>
/// <para>
/// <b>What that costs, stated rather than buried.</b> No chunk-to-document max-pooling runs against
/// a corpus in the nightly any more. What still runs there is
/// <see cref="BeirRealChunkingTests.Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments"/>, which
/// needs no model, finishes in seconds and catches a chunker that stopped chunking — half of the
/// guard, at none of the cost. The pooling half is
/// <c>DocumentRankingTests</c>' fixture and an opt-in run.
/// </para>
/// </summary>
public static class BeirRunBudget
{
    /// <summary>
    /// The variable that opts a run into the cases the nightly cannot afford.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> set by <c>nightly.yml</c>, and listed in that job's presence report
    /// anyway so the log says plainly that the long runs were off rather than leaving a reader to
    /// infer it from a test count.
    /// </remarks>
    public const string OptInVariable = "RAGNET_BEIR_LONG_RUNS";

    /// <summary>
    /// Every dataset under every protocol, with what it cost when it was last timed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table rather than a rule. "FiQA is expensive" would be a rule, and it would quietly decide
    /// the answer for the fourth dataset somebody adds; a table cannot, because
    /// <see cref="Find"/> throws on a pair that is not in it. Adding a descriptor to
    /// <see cref="BeirDatasetDescriptor.All"/> therefore fails here, loudly, until someone has
    /// measured what they added.
    /// </para>
    /// <para>
    /// Every string is a measurement or says that it is not one. The alternative — round numbers
    /// nobody timed — is what produced a 120-minute job containing a nine-hour test.
    /// </para>
    /// </remarks>
    private static readonly Cost[] Costs =
    [
        new(
            "scifact",
            BeirProtocol.Parity,
            FitsTheNightly: true,
            "~5 min per separator, cold. The theory runs both separators, so ~10 min for the dataset."),
        new(
            "scifact",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "10 min 43 s, measured 2026-07-31 (643.1 s: parity vectors warm from the cache, all " +
            "16,628 chunk vectors embedded fresh). That run produced SciFact's real nDCG@10 of " +
            "0.67742 under Phase 3.16's packing chunker, which cut the leg from 56,707 chunks to " +
            "20,155; the pre-3.16 fully-warm runs were 260.9 s and 314.6 s. The fully COLD figure " +
            "remains DERIVED — the nightly pays the cold price, so it is the cold one that has to " +
            "be timed before it is quoted as measured."),
        new(
            "fiqa",
            BeirProtocol.Parity,
            FitsTheNightly: false,
            "1 h 11 m, measured. One separator only — FiQA titles none of its 57,638 documents, so " +
            "the second separator would re-derive a number that is equal by construction."),
        new(
            "fiqa",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "1 h 4 m for the whole test, MEASURED 2026-08-02 (Phase 3.15); the real leg alone was " +
            "3,587.5 s (59.8 min), with the parity leg's vectors warm from the cache. 121,236 " +
            "chunk embeddings plus the 648 judged queries' — the only ones retrieved since Phase " +
            "3.15 — then InMemoryVectorStore sorts all 121,236 entries per query. The derived " +
            "~1.5-2 h estimate this replaces (~27 embeddings/s, taken from the packed SciFact " +
            "and ArguAna real legs) was conservative: it overshot the measured hour, and is " +
            "recorded as having overshot rather than quietly replaced. The 8-9 h figure before " +
            "it priced the pre-3.16 fragmenting chunker's 429,850 chunks and died with them."),
        new(
            "trec-covid",
            BeirProtocol.Parity,
            FitsTheNightly: false,
            "MEASURED 2026-08-12: 3,765.3 s (1 h 3 m) for the first separator and 2,847.6 s " +
            "(47 m) for the second, 1 h 50 m for the pair. The budget gate is keyed on dataset " +
            "and protocol rather than on separator, so the pair is bought together or not at " +
            "all. " +
            "**The derivation this replaces said ~6 h 20 m and was wrong by 3.4x, in the " +
            "direction that makes work look unaffordable.** It scaled FiQA's parity leg -- 1 h " +
            "11 m for 64,247 embeddings -- by TREC-COVID's 2.67x larger corpus. The error was " +
            "treating that figure as embedding cost when most of it was retrieval: FiQA's leg " +
            "retrieved for 6,648 queries through the pre-Phase-5.1.1 dense search, the one " +
            "allocating a corpus-sized list per query, while TREC-COVID retrieves for 50. " +
            "Scaling a total by the size of the part that is not dominating it is how a phase " +
            "gets deferred for being too expensive. " +
            "**A second claim in the derivation was also wrong and is worth more than the " +
            "timing.** It reasoned that changing the separator changes the embedded text, so the " +
            "first case's warm cache would be worth nothing to the second. Measured, the second " +
            "leg took 42,203 cache hits against 129,179 misses -- 24.6% of texts identical " +
            "across the two separators, on a corpus where 171,325 of 171,332 documents carry a " +
            "title. The two legs also produce the same nDCG@10 to five decimals, so on this " +
            "corpus the separator moves the number by nothing at all."),
        new(
            "trec-covid",
            BeirProtocol.HybridBm25,
            FitsTheNightly: false,
            "NOT RUN, and not derivable from the other datasets' cells. The BM25 arm indexes the " +
            "whole corpus, and this corpus is 3.0x FiQA's and 33x SciFact's, so scaling either " +
            "measured figure would be a guess wearing a number. Phase 5.3 lands the parity leg " +
            "only, because that is the one with a published figure to check against."),
        new(
            "trec-covid",
            BeirProtocol.Hyde,
            FitsTheNightly: false,
            "NOT RUN. Needs the hypothetical cache, which only the generation tool writes and " +
            "which is never committed -- so this cell cannot run on a fresh machine at any " +
            "budget, exactly as the other three datasets' Hyde cells cannot."),
        new(
            "trec-covid",
            BeirProtocol.Reranked,
            FitsTheNightly: false,
            "NOT RUN, and it would be the most expensive reranker cell in the suite by a wide " +
            "margin if it were: the cross-encoder scores every retrieved candidate for every " +
            "judged query, and TREC-COVID judges 50 queries against a densely judged corpus " +
            "averaging 493.5 relevant documents each."),
        new(
            "trec-covid",
            BeirProtocol.Comparison,
            FitsTheNightly: false,
            "NOT RUN. The library comparison's entrants are pinned to SciFact, ArguAna and FiQA " +
            "by Phase 5.1's published matrix; adding a fourth corpus to it is a decision for that " +
            "phase rather than a side effect of landing a dataset here."),
        new(
            "trec-covid",
            BeirProtocol.SemanticKernel,
            FitsTheNightly: false,
            "NOT RUN, for the same reason as the Comparison cell: the Semantic Kernel entrant " +
            "exists to sit beside the control in Phase 5.1's matrix, and that matrix's corpora " +
            "are fixed."),
        new(
            "trec-covid",
            BeirProtocol.LangChain,
            FitsTheNightly: false,
            "NOT RUN, for the same reason as the Comparison cell: the LangChain entrant exists to sit " +
            "beside the control in Phase 5.1's published matrix, and that matrix's three corpora " +
            "are fixed. Adding a fourth is a decision for that phase, not a side effect of " +
            "landing a dataset here."),
        new(
            "trec-covid",
            BeirProtocol.LlamaIndex,
            FitsTheNightly: false,
            "NOT RUN, for the same reason as the Comparison cell: the LlamaIndex entrant exists to sit " +
            "beside the control in Phase 5.1's published matrix, and that matrix's three corpora " +
            "are fixed. Adding a fourth is a decision for that phase, not a side effect of " +
            "landing a dataset here."),
        new(
            "trec-covid",
            BeirProtocol.Haystack,
            FitsTheNightly: false,
            "NOT RUN, for the same reason as the Comparison cell: the Haystack entrant exists to sit " +
            "beside the control in Phase 5.1's published matrix, and that matrix's three corpora " +
            "are fixed. Adding a fourth is a decision for that phase, not a side effect of " +
            "landing a dataset here."),
        new(
            "trec-covid",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "DERIVED, not measured, and deliberately unrun so far: the real leg chunks the corpus " +
            "rather than taking one unit per document, and TREC-COVID's abstracts are long enough " +
            "that the chunk count -- and so the cost -- is not derivable from the parity leg the " +
            "way FiQA's was. Phase 5.3 lands the parity leg first, because that is the one with a " +
            "published figure to check against."),
        new(
            "arguana",
            BeirProtocol.Parity,
            FitsTheNightly: true,
            "~4 min per separator cold, 50 s with the embedding cache warm. The theory runs both " +
            "separators, so ~8 min cold for the dataset."),
        new(
            "arguana",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "11 min 7 s, measured 2026-07-31, both legs (667.1 s: parity vectors warm, all 18,961 " +
            "chunk vectors fresh) under Phase 3.16's packing chunker, which cut the leg from " +
            "82,618 chunks to 24,003; the pre-3.16 figures were 28 min cold and 461.9 s fully " +
            "warm."),
        new(
            "scifact",
            BeirProtocol.HybridBm25,
            FitsTheNightly: false,
            "~1 m 50 s wall clock, measured in Phase 3.15 (2026-08-01/02) on the development " +
            "machine. Cold from an empty cache the cell pays the parity leg's embedding price " +
            "first (~5 min — DERIVED for this cell, not separately timed)."),
        new(
            "scifact",
            BeirProtocol.Hyde,
            FitsTheNightly: false,
            "~1 m 30 s wall clock, measured in Phase 3.15 (2026-08-01/02); cold adds the parity " +
            "leg's ~5 min of embedding (DERIVED). The cell also needs the hypothetical cache, " +
            "which only the generation tool writes and which is never committed — so this cell " +
            "can never run on a fresh runner at any budget, and an opted-in run without the cache " +
            "fails through refuse-on-miss rather than skipping."),
        new(
            "scifact",
            BeirProtocol.Reranked,
            FitsTheNightly: false,
            "~4 m wall clock, measured in Phase 3.15 (2026-08-01/02) after commit a912187 " +
            "replaced the whitespace word-lookup tokenizer with WordPiece. The pre-fix run " +
            "measured ~14 m, but the two figures are not a like-for-like speedup: the pre-fix run " +
            "also predates the judged-queries cut and reranked all 1,109 queries where the fixed " +
            "run reranks the 300 judged."),
        new(
            "fiqa",
            BeirProtocol.HybridBm25,
            FitsTheNightly: false,
            "~58 m wall clock, measured in Phase 3.15 (2026-08-01/02). Whether the corpus " +
            "embedding cache was warm for that figure was not recorded alongside it; fully cold " +
            "the cell also pays FiQA's parity embedding price, measured at 1 h 11 m (that part " +
            "DERIVED for this cell)."),
        new(
            "fiqa",
            BeirProtocol.Hyde,
            FitsTheNightly: false,
            "~1 m 30 s wall clock, measured in Phase 3.15 (2026-08-01/02) with the corpus " +
            "embeddings warm — cold, the cell pays FiQA's 1 h 11 m parity embedding price first " +
            "(DERIVED). Needs the hypothetical cache, which only the generation tool writes and " +
            "which is never committed — so this cell can never run on a fresh runner at any " +
            "budget, and an opted-in run without the cache fails through refuse-on-miss."),
        new(
            "fiqa",
            BeirProtocol.Reranked,
            FitsTheNightly: false,
            "~4 m wall clock, measured in Phase 3.15 (2026-08-01/02) after commit a912187's " +
            "WordPiece fix, with the corpus embeddings warm — cold, the cell pays FiQA's " +
            "1 h 11 m parity embedding price first (DERIVED)."),
        new(
            "arguana",
            BeirProtocol.HybridBm25,
            FitsTheNightly: false,
            "~2 m wall clock, measured in Phase 3.15 (2026-08-01/02). Cold from an empty cache " +
            "the cell pays the parity leg's ~4 min embedding price first (DERIVED)."),
        new(
            "arguana",
            BeirProtocol.Hyde,
            FitsTheNightly: false,
            "~3 m 49 s wall clock, measured in Phase 3.15 (2026-08-01/02); cold adds the parity " +
            "leg's ~4 min of embedding (DERIVED). Needs the hypothetical cache, which only the " +
            "generation tool writes and which is never committed — so this cell can never run on " +
            "a fresh runner at any budget, and an opted-in run without the cache fails through " +
            "refuse-on-miss."),
        new(
            "arguana",
            BeirProtocol.Reranked,
            FitsTheNightly: false,
            "~28 m wall clock, measured in Phase 3.15 (2026-08-01/02) after commit a912187's " +
            "WordPiece fix; the pre-fix run measured ~1 h 32 m over the same 1,406 judged " +
            "queries. ArguAna is the expensive reranker cell because it judges every query — " +
            "14,060 cross-encoder pairs against FiQA's 6,480 and SciFact's 3,000."),
        new(
            "scifact",
            BeirProtocol.Comparison,
            FitsTheNightly: false,
            "56 s with the parity leg's vectors warm in the embedding cache (12 s on an " +
            "immediate re-run), measured 2026-08-02 (Phase 3.14 Task 2); cold it pays the " +
            "parity leg's ~5 min embedding price first (that part DERIVED for this pair). " +
            "Opt-in even though it is cheap warm: " +
            "the nightly's cache is cold and xUnit runs test classes in parallel, so this case " +
            "beside BeirParityTests would race it for the same fresh cache and could pay the " +
            "corpus embedding twice — for a figure the parity case re-measures the same night " +
            "anyway. What is exclusively this case's — the run-file boundary's format and " +
            "ordering — is pinned in ci.yml's fast tier by TrecRunFileTests at no model cost."),
        new(
            "fiqa",
            BeirProtocol.Comparison,
            FitsTheNightly: false,
            "NEVER RUN to completion — no wall-clock figure is recorded for this pair, and " +
            "nothing below is a measurement of it. The retrieval work is exactly FiQA's parity " +
            "leg's, whose cold price was measured 2026-07-31 at 1 h 11 m, so cold this pair is " +
            "DERIVED to cost the same; warm it should cost minutes like the other two control " +
            "legs. Phase 3.14 Task 2 ran SciFact's and ArguAna's legs and deliberately not this " +
            "one, for the same budget reason FiQA's parity case is opt-in."),
        new(
            "arguana",
            BeirProtocol.Comparison,
            FitsTheNightly: false,
            "2 m 10 s with the parity leg's vectors warm in the embedding cache, measured " +
            "2026-08-02 (Phase 3.14 Task 2); cold it pays the parity leg's ~4 min embedding " +
            "price first (that part DERIVED for this pair). Opt-in for SciFact's reason: the " +
            "nightly's cache is cold, this case would race BeirParityTests for it, and the " +
            "boundary it uniquely exercises is already pinned in the fast tier by " +
            "TrecRunFileTests."),
        new(
            "scifact",
            BeirProtocol.SemanticKernel,
            FitsTheNightly: false,
            "2.1 s measurement (3.4 s whole test) with the parity corpus's vectors warm in the " +
            "embedding cache, measured 2026-08-02 (Phase 3.14 Task 4). The texts SK embeds are " +
            "exactly the parity corpus's, so cold this pair pays the parity leg's ~5 min " +
            "embedding price first (that part DERIVED for this pair). Opt-in for the control " +
            "row's reason: a comparator row racing BeirParityTests for a cold nightly cache " +
            "would pay the corpus embedding twice."),
        new(
            "fiqa",
            BeirProtocol.SemanticKernel,
            FitsTheNightly: false,
            "NEVER RUN to completion — no wall-clock figure is recorded for this pair, and " +
            "nothing below is a measurement of it. SK embeds exactly the parity corpus's texts " +
            "(one record per document, same model), so cold this pair is DERIVED to cost FiQA's " +
            "parity leg's 1 h 11 m; warm it should cost minutes like the other two entrant " +
            "legs. Phase 3.14 Task 4 ran SciFact's and ArguAna's legs and deliberately not this " +
            "one, for the same budget reason FiQA's parity case is opt-in."),
        new(
            "arguana",
            BeirProtocol.SemanticKernel,
            FitsTheNightly: false,
            "5.7 s measurement (7.1 s whole test) with the parity corpus's vectors warm in the " +
            "embedding cache, measured 2026-08-02 (Phase 3.14 Task 4); cold it pays the parity " +
            "leg's ~4 min embedding price first (that part DERIVED for this pair). Opt-in for " +
            "the control row's reason: a comparator row racing BeirParityTests for a cold " +
            "nightly cache would pay the corpus embedding twice."),
        new(
            "scifact",
            BeirProtocol.LangChain,
            FitsTheNightly: false,
            "Scoring the read-back run file costs seconds; producing the file is the cost, " +
            "measured 2026-08-02: 121.7 s for the LangChain SciFact run (5,205 units over " +
            "5,183 documents; the Python-side vector cache was partly warm from the identity " +
            "work — a fully cold run pays roughly the corpus's ~4 min of embedding). Opt-in " +
            "because the run file " +
            "comes from the pinned Python harness (benchmarks/library-comparison-python), " +
            "which the nightly does not run — an opted-in case without the file FAILS " +
            "(refuse-on-miss, the Hyde cell's rule) rather than skipping."),
        new(
            "fiqa",
            BeirProtocol.LangChain,
            FitsTheNightly: false,
            "NEVER RUN — no wall-clock figure is recorded, and nothing below is a measurement " +
            "of it. The dominant cost is embedding FiQA's 57,638 documents' chunks through the " +
            "Python-side pinned embedder, DERIVED from FiQA's .NET parity leg (1 h 11 m for " +
            "64,247 embeddings) to be roughly an hour per Python entrant. Phase 3.14 Stage 2 " +
            "ran SciFact and ArguAna and deliberately not FiQA, per the plan's budget rule."),
        new(
            "arguana",
            BeirProtocol.LangChain,
            FitsTheNightly: false,
            "Scoring the read-back run file costs seconds; producing the file is the cost, " +
            "measured 2026-08-02: 695.9 s for the LangChain ArguAna run, the Python-side " +
            "vector cache effectively cold (8,699 units over 8,674 documents plus 1,406 " +
            "judged queries, 9,997 embedded fresh). Opt-in for the SciFact " +
            "entry's reason; an opted-in case without the file FAILS rather than skipping."),
        new(
            "scifact",
            BeirProtocol.LlamaIndex,
            FitsTheNightly: false,
            "Scoring the read-back run file costs seconds; producing the file is the cost, " +
            "measured 2026-08-02: 45.2 s for the LlamaIndex SciFact run (5,196 units over " +
            "5,183 documents; nearly every chunk text coincided with the LangChain run's, so " +
            "the Python-side vector cache served 5,469 of 5,496 texts — cold, this pays the " +
            "corpus embedding like the others). Opt-in for " +
            "the LangChain entry's reason; no file, opted-in = FAIL."),
        new(
            "fiqa",
            BeirProtocol.LlamaIndex,
            FitsTheNightly: false,
            "NEVER RUN — no wall-clock figure is recorded, and nothing below is a measurement " +
            "of it. DERIVED to cost roughly an hour like the other Python entrants on FiQA " +
            "(the corpus embedding dominates). Phase 3.14 Stage 2 ran SciFact and ArguAna and " +
            "deliberately not FiQA, per the plan's budget rule."),
        new(
            "arguana",
            BeirProtocol.LlamaIndex,
            FitsTheNightly: false,
            "Scoring the read-back run file costs seconds; producing the file is the cost, " +
            "measured 2026-08-02: 263.1 s for the LlamaIndex ArguAna run (8,679 units; " +
            "nearly every chunk text coincided with the LangChain run's, so the Python-side " +
            "cache served 10,055 of 10,085 texts — cold it pays the LangChain entry's " +
            "embedding price). Opt-in for the " +
            "LangChain entry's reason; no file, opted-in = FAIL."),
        new(
            "scifact",
            BeirProtocol.Haystack,
            FitsTheNightly: false,
            "Scoring the read-back run file costs seconds; producing the file is the cost, " +
            "measured 2026-08-02: 250.8 s for the Haystack SciFact run — its 200-word " +
            "splitter produced the most units of the three Python entrants (8,042 over 5,183 " +
            "documents, max 8 from one), 5,550 embedded fresh. Opt-in for the LangChain " +
            "entry's reason; no " +
            "file, opted-in = FAIL."),
        new(
            "fiqa",
            BeirProtocol.Haystack,
            FitsTheNightly: false,
            "NEVER RUN — no wall-clock figure is recorded, and nothing below is a measurement " +
            "of it. DERIVED to cost roughly an hour like the other Python entrants on FiQA " +
            "(the corpus embedding dominates), likely more: Haystack's 200-word default " +
            "produces the most chunks of the three. Phase 3.14 Stage 2 ran SciFact and " +
            "ArguAna and deliberately not FiQA, per the plan's budget rule."),
        new(
            "arguana",
            BeirProtocol.Haystack,
            FitsTheNightly: false,
            "Scoring the read-back run file costs seconds; producing the file is the cost, " +
            "measured 2026-08-02: 404.7 s for the Haystack ArguAna run (11,342 units over " +
            "8,674 documents, max 6 from one, 5,094 embedded fresh). Opt-in for the " +
            "LangChain entry's reason; no file, opted-in = FAIL."),
        new(
            "multihop-rag",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "MEASURED COLD AND IDLE, TWICE, 2026-08-15 (#174): **343.5 s and 388.5 s for the " +
            "real leg**, 34.1 s and 58.1 s for the parity control it is differenced against, " +
            "**6 m 18 s and 7 m 27 s for the case**. Windows 11, .NET 10.0.11, CPU ONNX Runtime, " +
            "20 logical processors at 2-4% load before each run, nothing else of this " +
            "repository's running. Cold by the nightly's own definition: each run went against a " +
            "fresh cache root holding only the converted dataset, the model and the vocabulary, " +
            "with the embeddings directory absent -- 0 hits and 2,864 misses on the parity leg, " +
            "2,314 hits (the query vectors the parity leg had just written) and 17,589 misses on " +
            "the real leg, 20,453 texts embedded per run -- and the directory was deleted between " +
            "the two. Both runs produced nDCG@10 = 0.63967, Recall@10 = 0.78684, MRR@10 = " +
            "0.70150 to five decimals, and the parity control 0.55724 both times, so none of this " +
            "touches the figures in BeirReproduction -- only what it costs to obtain them. " +
            "**The 13% between the two real legs is the honest width of an idle measurement " +
            "here**, and the two are quoted rather than averaged; the parity leg's 34-58 s spread " +
            "is a small leg's warm-up noise and is not a finding. " +
            "**What this replaces: 600.2 s real, 78.9 s parity, 11 m 19 s for the case, taken " +
            "2026-08-12 on this machine at 45% CPU under a media player, three browsers, four " +
            "editors and several MCP servers, and published as an UPPER BOUND with an instruction " +
            "to re-measure idle before anybody scheduled against it.** Idle is 1.5-1.75x faster " +
            "on the real leg, so the bound held and was loose by that much; over-estimating " +
            "failed safe, as that cell said it would. The same day's partly-warm 7 m 14 s (11,501 " +
            "hits, 8,402 misses) sits between the two states and is kept so a reader who " +
            "measures seven minutes knows why. The derivation before all of these declined to " +
            "guess, which was right: 609 documents is an order of magnitude under SciFact's " +
            "corpus and costs about the same, because the articles average 10,340 characters and " +
            "the embedding bill tracks chunks, not documents. " +
            "**FitsTheNightly: DECIDED, and it stays false -- for a different reason than " +
            "before.** The old reason was that the figure was inflated by an unmeasured amount; " +
            "that is gone. The reason now is that this figure is about this machine and the " +
            "nightly runs on another: a hosted ubuntu-latest runner with 4 vCPUs against 20 " +
            "logical processors here, and CPU ONNX embedding of 20,453 texts scales with cores, " +
            "so the runner's cold cost is plausibly 3-5x this one -- 20-35 minutes for the case " +
            "-- and unmeasured. Flipping a 120-minute job's gate on a local figure that the " +
            "runner may not reproduce is the same casual decision the old cell refused, one " +
            "machine over. Two more things weigh the same way: every Real leg in this table is " +
            "opt-in by policy, SciFact's 10 m 43 s included, and #175 records that the nightly's " +
            "MultiHop-RAG footprint (the download and conversion the ungated chunking check " +
            "triggers) is already unbudgeted there. **What would flip it** is a runner-side " +
            "measurement -- one opted-in dispatch of this case on ubuntu-latest, timed -- and " +
            "then a decision about the nightly's total, not this cell alone. Until then this is " +
            "the fastest opt-in Real leg in the table and the command below runs it."),
        new(
            "multihop-rag",
            BeirProtocol.GraphRag,
            FitsTheNightly: false,
            "MEASURED 2026-08-12, and the only cell in this table whose cost is in two currencies. " +
            "**The run itself: 5 m 45 s from a cold embedding cache, 33-35 s warm.** " +
            "GraphRagFunctionsTests over the pinned 60-article slice (MultiHopRagSlice), Windows " +
            "11, .NET 10, CPU ONNX Runtime. The cold figure is the honest one for a fresh machine: " +
            "the slice's 2,044 article chunks are the small part, and the 33,100 entity and " +
            "relationship chunks GraphRAG itself produces plus 655 community reports are what " +
            "actually gets embedded -- roughly 35,800 vectors, against 17,648 for the whole " +
            "609-article corpus under the Real protocol. **Graph construction costs more embedding " +
            "than the corpus does.** " +
            "**The other currency: 4,088 OpenRouter calls, once.** Entity extraction is an LLM " +
            "call per chunk plus one gleaning pass, so 2,044 chunks cost 4,088 requests against " +
            "openai/gpt-4o-mini at temperature 0. That took 34 m 25 s (2,065.2 s) at twelve " +
            "articles in flight, after a 58-request smoke run of 225.1 s on one article. **No " +
            "token or cost figure was captured** -- the generation tool never read " +
            "ChatResponse.Usage, so nothing here is a spend measurement and none should be " +
            "inferred from the request count. " +
            "**But the nightly would pay none of it, and neither does a re-run.** Every one of " +
            "those 4,088 responses is in GraphExtractionCache, replayed refuse-on-miss; the run " +
            "above makes zero model calls. The cache is never committed, so this cell can no more " +
            "run on a fresh runner than the Hyde cells can, and an opted-in run without it FAILS " +
            "naming the missing key rather than skipping. " +
            "FitsTheNightly stays false for that reason before any timing argument: the nightly " +
            "has no cache to replay and cannot make the calls. " +
            "**A SECOND generation run is now part of the price, and it is bought the same way " +
            "(#172).** Community reports are one LLM call per community, generated once by the " +
            "tool's --stage reports into the graph-reports directory of the same cache and " +
            "replayed refuse-on-miss ever after -- so the guard asserts against reports a model " +
            "wrote instead of the head of their own prompt, and still makes no model calls. That " +
            "became possible when the report prompt stopped being unbounded: it was 1,806,352 " +
            "characters while Leiden over-merged, and is under 50,000 now that " +
            "MaxCommunityReportPromptLength bounds it. The figures above predate that run and do " +
            "not include the embedding of its reports; --stage reports --plan-only states its own " +
            "cost before any of it is spent. " +
            "**One cost is still NOT in these figures.** CommunityDetectionBehavior is an " +
            "ingestion behavior, so a real pipeline re-detects and regenerates every report on " +
            "every document: 60 passes over this slice, of which 59 are overwritten. Both the tool " +
            "and the guard run it once, over the finished graph, which is what that behaviour " +
            "converges to. " +
            "**SECOND CASE UNDER THIS SAME CELL, and it is the expensive one (#173).** " +
            "BeirGraphRagCorpusTests measures the WHOLE 609-article corpus under the graph path and " +
            "scores local search with nDCG@10 over all 2,255 judged queries, where " +
            "GraphRagFunctionsTests above runs 60 articles and 27 queries and scores nothing. The " +
            "gate is keyed on (dataset, protocol), so the two share this cell and the filter in the " +
            "skip message selects BOTH -- which is right for a cell that prices both, and wrong for " +
            "an operator who wanted only one. For the corpus run alone: " +
            "--filter \"FullyQualifiedName~BeirGraphRagCorpusTests.NdcgAt10_UnderTheGraphPath\" -- " +
            "the method, not the class, because since Phase 5.2.1 the class also holds the depth " +
            "control's run, which is priced by its own cell. For the " +
            "slice guard alone: --filter \"FullyQualifiedName~GraphRagFunctionsTests\". A THIRD case " +
            "shares the cell since #239: BeirGraphRagCorpusTests.Ablations_UnderTheGraphPath, one more " +
            "graph build plus one query pass, recorded in Phase 5.2.1 rather than pinned. A FOURTH " +
            "since Phase 5.2.2: BeirGraphRagAnswerTests, the same graph build plus one answering pass " +
            "per arm replayed from the graph-answers cache; its own pins live in " +
            "MultiHopRagAnswerReproduction and its generation gate is described on the class. The " +
            "confound check that has to pass before either number means anything is " +
            "--filter \"DisplayName~Chunking_UnderTheGraphPath\", which needs no model and takes " +
            "under a second. " +
            "**Its cost is now MEASURED, 2026-08-15: 43 m 29 s wall clock (01:16-01:59), of " +
            "which 1,338.1 s (22 m 18 s) is graph construction**, 2,564.3 s the scored " +
            "local-search pass over all 2,255 judged queries, and 1,226.2 s the candidate-set " +
            "control pass. Windows 11, .NET 10.0.11, CPU ONNX Runtime, same machine as the Real " +
            "leg's figures. The embedding cache took 145,840 hits against 177,566 misses, so " +
            "this is close to but not exactly the cold price a fresh machine pays. The store " +
            "came out at 321,151 indexed units: 17,648 article chunks, 299,916 entity and " +
            "relationship chunks, 3,587 community reports. It needs #231 to be the run this " +
            "figure describes -- without it local search discards about a third of every " +
            "candidate set and both the timing and the nDCG are of something else. " +
            "**The derivation this replaces was wrong in both directions and is kept so the next " +
            "estimate is better.** It said 1.5-3 h wall clock against a measured 43 m 29 s, so " +
            "it OVERSHOT the time by 2-4x -- and it UNDERSHOT the store, predicting ~251,000 " +
            "vectors where 321,151 were indexed (~230,000 entity and relationship chunks against " +
            "299,916 actual, scaled from the slice's 33,100 at 6.9x). Its two named error bars, " +
            "both superlinear and both in graph construction, are where the overshoot lived: " +
            "SqliteGraphStore rewriting each entity description on every occurrence " +
            "(O(occurrences^2) bytes, derived at ~48x the slice's cost), and " +
            "GraphLocalSearchBehavior (now the frozen LegacyPageRankLocalSearch in this harness) " +
            "traversing per query through GetNeighborsAsync, " +
            "GetRelationshipsAsync and GetCommunitiesForEntityAsync over a `relationships` table " +
            "with no index on source_entity or target_entity -- ~45,000 full scans of 147,021 " +
            "rows over the run, derived at 15-40 min. Graph construction measured 22 m 18 s in " +
            "total, so those terms are real but were priced above what they cost. The lesson is " +
            "the ordinary one: a derivation that names which of its terms it distrusts is " +
            "correctable, and this one was, in both directions at once. " +
            "**The graph-construction term was split on 2026-08-18, and the recompute was not where " +
            "the time went.** Measured over the real corpus with the extraction and report caches " +
            "replayed and a stub embedder, at 50/100/200/400/609 documents, twice. The 609-document " +
            "graph reproduced exactly -- 62,392 entities, 147,021 relationships -- so it is this " +
            "corpus and not an approximation of it. Chunking 44-51 ms. Leiden + PageRank + the score " +
            "write-back **2.7 s**, at 0.044 ms per entity, and that coefficient held within 6% across " +
            "both runs and all five sizes. Extraction measured 152.9 s cold and 18.7 s warm -- an 8x " +
            "to 12x page-cache artefact from reading 35,176 cache files, NOT a property of extraction, " +
            "and the reason no extraction figure is quoted here. Report generation 47.6-59.2 s, " +
            "cache-replayed and likewise I/O-bound. " +
            "**What #300 removed, projected from the measured per-entity coefficient:** detection ran " +
            "once per document over a graph growing to 62,392 entities, so the sum over 609 documents " +
            "is **13.6 minutes** -- both runs give 13.6 to one decimal, because the coefficient is " +
            "stable even where the totals are not. That is against the 2.7 s one detection now costs. " +
            "The old cost was in fact worse than that: per-document detection also regenerated " +
            "reports, and report cache keys are the rendered prompt, so a graph that changed every " +
            "document MISSED every time and would have called the model. " +
            "**Two of the three calls in that traversal term no longer happen.** " +
            "GetRelationshipsAsync and GetCommunitiesForEntityAsync were awaited with their " +
            "results discarded, and #239 removed them; the ~45,000 scans above are a HISTORICAL " +
            "term and a re-measurement should come in under it. The sentence is left as written " +
            "because it records what was derived at the time. GetNeighborsAsync stays -- local " +
            "search reads its PageRank scores, which is the one result the traversal used. " +
            "**It needs a report cache covering the FULL corpus graph, which is not the slice's.** " +
            "Report cache keys are the rendered report prompts, and those are a function of the " +
            "graph: the corpus graph's ~3,587 communities are ~3,587 entries the slice's 607 do " +
            "not provide (#226 made that generation parallel; it was sequential). An opted-in run against " +
            "a partly-filled report cache FAILS refuse-on-miss naming the missing key, which is the " +
            "correct behaviour and is not a bug to work around. " +
            "The extraction side needs nothing new: 35,296 requests over the corpus resolve to " +
            "35,176 cache entries, because exactly 60 of the 17,648 chunk texts repeat verbatim and " +
            "share a key -- counted, not assumed, by " +
            "BeirGraphRagCorpusTests.Chunking_UnderTheGraphPath_IsIdenticalToTheRealProtocols. " +
            "FitsTheNightly stays false for the cell's existing reason before any timing argument: " +
            "the nightly has no cache to replay and cannot make the calls."),
        new(
            "multihop-rag",
            BeirProtocol.GraphRagDepthControl,
            FitsTheNightly: false,
            "MEASURED 2026-08-15, three times in a row on Windows 11, .NET 10.0.11, CPU ONNX " +
            "Runtime: **120.0 s, then 8 s, then 7.0 s** -- the same nDCG@10, Recall@10 and " +
            "MRR@10 to five decimals all three times, and a 17x spread on the clock. Phase 5.2.1 " +
            "(#232): the depth-matched dense control, BeirRealChunkingTests' 17,648 article " +
            "chunks indexed alone -- the Real leg's store exactly -- and retrieved for the 2,255 " +
            "judged queries at the graph path's candidate depth of 500 rather than the Real " +
            "protocol's derived 2,010. **All three runs were 'warm' by the embedding cache's " +
            "own count** -- 19,903 hits, 0 misses, every vector the Real leg had already written " +
            "-- so the 17x is not embedding: it is the OS page cache over 19,903 small vector " +
            "files in a directory of 857,000, cold on the first read and hot on the next two. " +
            "That is the same artefact library-comparison.md records at 23x and #174 records at " +
            "1.6x, and it is why this cell quotes both ends rather than one of them: **7-8 s is " +
            "the compute** -- chunk loading, index construction, 2,255 top-500 scans over 17,648 " +
            "entries -- **and 120 s is what a first run on this machine pays in disk reads on " +
            "top of it.** A machine that has never run the Real leg pays the Real cell's cold " +
            "embedding price on top of that (600.2 s upper bound, itself flagged as taken under " +
            "load, #174), so the fresh-runner cost of this case is the Real cell's, not this " +
            "one's. The derivation this replaces said 'under the Real leg's time'; it was right, " +
            "and by more than it guessed. No graph is built, no extraction or report cache is " +
            "touched, no model is called. The figure -- 0.63967, the Real leg's own -- is in " +
            "BeirReproduction with what it means. " +
            "FitsTheNightly is false for the Real cell's reason: on a nightly whose cache is " +
            "fresh this costs the Real leg's cold embedding first, and that figure is not yet " +
            "clean. Revisit both together when #174 lands."),
            new(
            "scifact",
            BeirProtocol.SemanticChunking,
            FitsTheNightly: false,
            "NOT YET RUN. SciFact abstracts are short, so this cell is also the test of whether semantic chunking splits them at all — the arm asserts more units than documents before reporting, so a corpus it cannot split fails there rather than reporting the Real figure under another name. "
            + "Cost is embedding-bound and pays no model: the chunker embeds each sentence to "
            + "find boundaries, then the harness embeds the resulting units, so it is strictly "
            + "more embedding work than the Real cell on the same dataset and no LLM calls at "
            + "all. The embedding cache absorbs the unit side on a re-run; the sentence side is "
            + "new text and is not in any cache, so a re-run costs the same as the first. "
            + "**MEASURED 2026-08-26: all four datasets in 4 h 18 m, ~202,000 CPU-seconds.** That "
            + "first run reported its figures through ITestOutputHelper, which this project's "
            + "runner suppresses for passing tests, so it proved the mechanism and produced no "
            + "number. Run this cell with -showLiveOutput. Not caching the sentence embeddings is "
            + "worth revisiting for the same reason: at four hours a run, repeatability is worth "
            + "more than the disk it would take."),
        new(
            "fiqa",
            BeirProtocol.SemanticChunking,
            FitsTheNightly: false,
            "NOT YET RUN. FiQA answers are longer than SciFact abstracts, so this is the cell most likely to show a real difference either way. "
            + "Cost is embedding-bound and pays no model: the chunker embeds each sentence to "
            + "find boundaries, then the harness embeds the resulting units, so it is strictly "
            + "more embedding work than the Real cell on the same dataset and no LLM calls at "
            + "all. The embedding cache absorbs the unit side on a re-run; the sentence side is "
            + "new text and is not in any cache."),
        new(
            "arguana",
            BeirProtocol.SemanticChunking,
            FitsTheNightly: false,
            "NOT YET RUN. ArguAna documents are single arguments and among the shortest in the suite; expect few splits, and treat a no-split failure as the finding rather than as a broken cell. "
            + "Cost is embedding-bound and pays no model: the chunker embeds each sentence to "
            + "find boundaries, then the harness embeds the resulting units, so it is strictly "
            + "more embedding work than the Real cell on the same dataset and no LLM calls at "
            + "all. The embedding cache absorbs the unit side on a re-run; the sentence side is "
            + "new text and is not in any cache."),
        new(
            "trec-covid",
            BeirProtocol.SemanticChunking,
            FitsTheNightly: false,
            "NOT YET RUN. NOT RUN, for the same reason the other TREC-COVID cells are not: the corpus is an order larger than the other three and no cell here has been budgeted for it. "
            + "Cost is embedding-bound and pays no model: the chunker embeds each sentence to "
            + "find boundaries, then the harness embeds the resulting units, so it is strictly "
            + "more embedding work than the Real cell on the same dataset and no LLM calls at "
            + "all. The embedding cache absorbs the unit side on a re-run; the sentence side is "
            + "new text and is not in any cache."),

        // Phase 6.2.1: the RealHyde and RealReranked cells measure HyDE and cross-encoder reranking
        // over Rag.NET's own chunking rather than parity's one-chunk-per-document units. Eight
        // entries because both protocols apply to all four BEIR datasets; only SciFact was
        // scheduled to run. Three are now MEASURED and say so — SciFact's two and FiQA's reranker,
        // which ran because the opt-in ungated every dataset; the other five are DERIVED or NOT RUN
        // and say that. Each entry states which it is, because this table exists precisely so an
        // unmeasured case cannot silently join the nightly, and a blanket claim about the whole
        // block stops being true the first time one of them runs.
        new(
            "scifact",
            BeirProtocol.RealHyde,
            FitsTheNightly: false,
            "MEASURED 2026-09-02, TWICE, and the two timings differ by 16x: 199.5 s on the first "
            + "run and 12.5 s on the second, same machine, minutes apart, same binary, nothing "
            + "changed. Both report the same 21,355 embedding-cache hits and 0 misses, and the same "
            + "nDCG to five decimals. The candidate cause -- NOT a diagnosis -- is the OS page "
            + "cache over the 21,355 shard files the embedding cache reads, cold on the first run "
            + "and warm on the second; in-process chunking is redone either way, so it is not that. "
            + "**Budget against 199.5 s, not 12.5 s.** A warm figure quoted as the cost is how a "
            + "nightly gets scheduled against a number only a developer's second run can produce, "
            + "and this repository has already published three false findings off a 23x page-cache "
            + "artefact. "
            + "This replaces a DERIVED estimate that declined to give a number at all -- it said "
            + "only 'more than the parity cell's ~1 m 30 s, and treat the difference as unknown'. "
            + "The cold truth is 2.2x that, which is roughly the ratio of the chunked corpus to the "
            + "document one and nothing more surprising. Declining to name a figure is why this "
            + "entry needed no correction, unlike the sibling RealReranked cell whose ~4 m estimate "
            + "was wrong by 27x. Pays NO model calls: the hypothetical cache is "
            + "keyed on model identity, prompt template, query and hypothesis index — not the "
            + "corpus — so the entries the parity cell generated replay unchanged. Without that "
            + "cache the cell fails through refuse-on-miss rather than skipping, exactly as the "
            + "parity Hyde cell does."),
        new(
            "scifact",
            BeirProtocol.RealReranked,
            FitsTheNightly: false,
            "MEASURED 2026-09-01: 6,414.5 s -- 1 h 47 m, embedding cache 20,455 hits and 0 misses, so "
            + "pure compute. This replaces a DERIVED estimate of ~4 m that was wrong by roughly "
            + "27x. The error was reasoning from the parity cell: the cross-encoder scores "
            + "candidates drawn from 20,155 chunks here rather than 5,183 documents, and it is that "
            + "candidate count, not the corpus size, that sets the cost. Pays no model calls -- the "
            + "reranker is a local ONNX model."),
        new(
            "fiqa",
            BeirProtocol.RealHyde,
            FitsTheNightly: false,
            "MEASURED 2026-09-02: 1,786.9 s -- 29 m 47 s, embedding cache 123,828 hits and 0 "
            + "misses, so pure compute and no model calls. Against SciFact's cold 199.5 s that is "
            + "9.0x the time over 6.0x the units (121,236 against 20,155), so cost is mildly "
            + "superlinear in corpus size rather than proportional -- the retrieval side sorts a "
            + "larger candidate set per query. Quoted COLD, deliberately: SciFact's warm re-run of "
            + "the same cell was 16x faster than its cold one, and a warm figure quoted as the cost "
            + "is how a nightly gets scheduled against a time only a second run can produce -- this "
            + "cell measured 99.3 s warm, an 18x ratio matching SciFact's 16x. Needs "
            + "the hypothetical cache, which only the generation tool writes and which is never "
            + "committed -- FiQA's exists because its parity Hyde cell ran in Phase 3.15."),
        new(
            "fiqa",
            BeirProtocol.RealReranked,
            FitsTheNightly: false,
            "MEASURED 2026-09-01: 22,674.7 s -- 6 h 18 m, embedding cache 121,884 hits and 0 misses. Ran "
            + "because RAGNET_BEIR_LONG_RUNS ungates every dataset, not because it was scheduled; "
            + "the figure is real and is pinned. 3.5x SciFact's cell, tracking the candidate count "
            + "rather than anything else."),
        new(
            "arguana",
            BeirProtocol.RealHyde,
            FitsTheNightly: false,
            "NOT RUN. Applicable and unscheduled, as FiQA's is."),
        new(
            "arguana",
            BeirProtocol.RealReranked,
            FitsTheNightly: false,
            "NOT RUN. Applicable and unscheduled, as FiQA's is."),
        new(
            "trec-covid",
            BeirProtocol.RealHyde,
            FitsTheNightly: false,
            "NOT RUN. Applicable and unscheduled. It would also be the most expensive of the four: "
            + "this corpus is 33x SciFact's, so the chunked side is larger again."),
        new(
            "trec-covid",
            BeirProtocol.RealReranked,
            FitsTheNightly: false,
            "NOT RUN. Applicable and unscheduled, and it inherits the parity Reranked cell's "
            + "warning: TREC-COVID judges 50 queries against a densely judged corpus averaging "
            + "493.5 relevant documents each, which is what makes its reranker cell the suite's "
            + "most expensive."),
];

    /// <summary>
    /// Reports whether this case is one the nightly cannot afford and nobody asked for.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name, as it appears in the theory data.</param>
    /// <param name="protocol">Which protocol the case measures under.</param>
    /// <param name="reason">
    /// Receives the skip message when the result is <see langword="true"/>, and
    /// <see cref="string.Empty"/> otherwise.
    /// </param>
    /// <returns><see langword="true"/> when the case must be skipped.</returns>
    /// <remarks>
    /// Shaped like <see cref="BeirHarness.IsProvisioned"/> on purpose, so both gates read the same
    /// way at the top of a test and <c>Assert.SkipWhen</c> stays visible in the test itself rather
    /// than being buried in a helper that skips on the caller's behalf.
    /// </remarks>
    public static bool IsGatedOff(string datasetName, BeirProtocol protocol, out string reason)
    {
        var cost = Find(datasetName, protocol);
        if (cost.FitsTheNightly || IsOptedIn(datasetName))
        {
            reason = string.Empty;
            return false;
        }

        reason = Explain(cost);
        return true;
    }

    /// <summary>
    /// Reports what the table alone says about one case, with no reference to the environment.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name, as it appears in the theory data.</param>
    /// <param name="protocol">Which protocol the case measures under.</param>
    /// <returns><see langword="true"/> when the case runs without anyone asking for it.</returns>
    /// <remarks>
    /// <see cref="IsGatedOff"/> answers "did this case run", which is the question a test method
    /// needs and which necessarily consults <see cref="OptInVariable"/>. That makes it the wrong
    /// question for a test <i>about the table</i>: with the variable set, every case is ungated and
    /// an assertion phrased against <see cref="IsGatedOff"/> holds no matter what the table says.
    /// This is the table, read directly, so
    /// <see cref="BeirRunBudgetTests.TheNightlyStillMeasuresParityOnAtLeastTwoDatasets"/> asserts the
    /// same thing on a developer's machine mid-measurement as it does in <c>ci.yml</c>.
    /// </remarks>
    public static bool FitsTheNightly(string datasetName, BeirProtocol protocol) =>
        Find(datasetName, protocol).FitsTheNightly;

    /// <summary>Reports whether the table holds a cell for one pair, without throwing when it does not.</summary>
    /// <param name="datasetName">The BEIR dataset name.</param>
    /// <param name="protocol">The protocol to ask about.</param>
    /// <returns><see langword="true"/> when the table holds a cell for that pair.</returns>
    /// <remarks>
    /// <see cref="IsGatedOff"/> and <see cref="FitsTheNightly"/> both go through <c>Find</c>, which
    /// throws on an absent pair — correct for them, and useless for asking whether a pair is absent.
    /// </remarks>
    public static bool HasCost(string datasetName, BeirProtocol protocol)
    {
        foreach (var cost in Costs)
        {
            if (string.Equals(cost.Dataset, datasetName, StringComparison.Ordinal)
                && cost.Protocol == protocol)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reports whether the long runs were explicitly asked for, for one dataset.</summary>
    /// <param name="datasetName">The dataset the caller is about to measure.</param>
    /// <returns><see langword="true"/> when <see cref="OptInVariable"/> asks for that dataset.</returns>
    /// <remarks>
    /// Takes a dataset because the variable is read per case and the answer differs per case; see
    /// <see cref="IsOptedInFor"/> for what the value may say and why an unrecognised one throws.
    /// Private again since Phase 3.15 recorded the ablation cells' measured costs: while those
    /// entries did not exist, <see cref="BeirAblationTests"/> gated on this directly so an
    /// unmeasured cell could not default into the nightly through <see cref="IsGatedOff"/>'s table
    /// lookup throwing — every cell now gates through the table like every other case.
    /// </remarks>
    private static bool IsOptedIn(string datasetName) =>
        IsOptedInFor(Environment.GetEnvironmentVariable(OptInVariable), datasetName);

    /// <summary>Reads one opt-in value, with no reference to the environment.</summary>
    /// <param name="value">The raw value of <see cref="OptInVariable"/>, or <see langword="null"/>.</param>
    /// <param name="datasetName">The dataset the caller is about to measure.</param>
    /// <returns><see langword="true"/> when that value opts that dataset in.</returns>
    /// <exception cref="InvalidOperationException">
    /// The value is neither an on/off word nor a list of dataset names.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Four readings, in order. Absent, blank, <c>0</c> and <c>false</c> are <b>off</b>:
    /// <c>RAGNET_BEIR_LONG_RUNS=0</c> in a workflow reads to every human as "off", and a gate that
    /// turned nine hours of measurement on for it would be a trap rather than a switch. <c>1</c> and
    /// <c>true</c> are <b>on for every dataset</b>, which is what all thirteen documented
    /// invocations in this repository say and what they must keep meaning. A comma-separated list of
    /// dataset names is <b>on for exactly those</b>. Anything else <b>throws</b>.
    /// </para>
    /// <para>
    /// <b>Why the list exists.</b> xunit filters on classes and methods, never on theory arguments,
    /// so measuring one cell means running its theory across every dataset — and the opt-in used to
    /// ungate all of them together. The datasets are not interchangeable: TREC-COVID's Real leg has
    /// never been embedded and its corpus is 33x SciFact's, so a run aimed at SciFact would chunk
    /// and embed that corpus from cold on its way past. That is not hypothetical. FiQA's
    /// <c>RealReranked</c> cell ran 6 h 18 m on 2026-09-01 because, in its own commit message, the
    /// variable "ungates every dataset rather than because it was scheduled".
    /// </para>
    /// <para>
    /// <b>Why an unknown name throws rather than widening.</b> The old rule was "anything else
    /// present is on", so <c>scifct</c> would have bought every dataset — the most expensive
    /// possible reading of a typo, taken silently. A run nobody asked for is exactly what this gate
    /// exists to prevent, and a mistyped name is the likeliest way to ask for one. A partly
    /// recognised list throws for the same reason at one remove: it would measure the half it
    /// understood and pass, and a green summary does not show the half it dropped.
    /// </para>
    /// </remarks>
    internal static bool IsOptedInFor(string? value, string datasetName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var selected = false;
        foreach (var part in trimmed.Split(','))
        {
            var name = part.Trim();
            if (!IsKnownDataset(name))
            {
                throw new InvalidOperationException(
                    $"{OptInVariable} is set to '{trimmed}', and '{name}' is not a dataset this "
                    + "suite knows. Set it to 1 to opt every dataset in, or to a comma-separated "
                    + $"list of {KnownDatasets()} to opt in only those. It is not read as a "
                    + "yes-word: an unrecognised value used to mean every dataset, which turns a "
                    + "typo into the most expensive run available.");
            }

            if (string.Equals(name, datasetName, StringComparison.OrdinalIgnoreCase))
            {
                selected = true;
            }
        }

        return selected;
    }

    /// <summary>Reports whether one name is a dataset this suite carries a descriptor for.</summary>
    private static bool IsKnownDataset(string name)
    {
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every dataset name, for the message an unknown one fails with.</summary>
    private static string KnownDatasets()
    {
        var names = new List<string>();
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            names.Add(descriptor.Name);
        }

        return string.Join(", ", names);
    }

    /// <summary>The skip message one gated pair would carry, without consulting the environment.</summary>
    /// <param name="datasetName">The BEIR dataset name, as it appears in the theory data.</param>
    /// <param name="protocol">Which protocol the case measures under.</param>
    /// <returns>The message <see cref="IsGatedOff"/> would hand back for that pair.</returns>
    /// <remarks>
    /// A seam for <see cref="BeirRunBudgetTests"/>, for the reason that test records: asking
    /// <see cref="IsGatedOff"/> reads <see cref="OptInVariable"/>, so on the one machine most likely
    /// to be editing the table the message under test is the empty string.
    /// </remarks>
    internal static string ExplainFor(string datasetName, BeirProtocol protocol) =>
        Explain(Find(datasetName, protocol));

    /// <summary>Finds the recorded cost for one dataset under one protocol.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been measured for that pair.</exception>
    private static Cost Find(string datasetName, BeirProtocol protocol)
    {
        foreach (var cost in Costs)
        {
            if (string.Equals(cost.Dataset, datasetName, StringComparison.Ordinal)
                && cost.Protocol == protocol)
            {
                return cost;
            }
        }

        throw new InvalidOperationException(
            $"No run cost is recorded for dataset '{datasetName}' under the {protocol} protocol. " +
            $"A dataset was added to BeirDatasetDescriptor.All without measuring what it costs, and " +
            $"{nameof(BeirRunBudget)} refuses to guess: an unmeasured case either silently joins a " +
            "120-minute nightly job or is silently gated out of it, and both have happened. Time it " +
            $"with {OptInVariable}=1 and add it to {nameof(BeirRunBudget)}.{nameof(Costs)}.");
    }

    /// <summary>
    /// The message a gated case skips with: what did not run, what it costs, and the command that
    /// runs it.
    /// </summary>
    /// <remarks>
    /// All three parts are required and none is decoration. A skip that says only "skipped" is
    /// indistinguishable from a pass in a test summary — the failure mode this project has spent
    /// three phases removing — and one that names the case without naming the cost invites the next
    /// person to put it back in the nightly, which is how tonight's timeout was built.
    /// </remarks>
    private static string Explain(Cost cost) =>
        $"""
        {cost.Dataset} {Describe(cost.Protocol)} run is OPT-IN and did NOT run.
        Measured cost: {cost.Measured}
        Why: nightly.yml's env-gated job has timeout-minutes: 120, and also restores, builds the whole
        solution and runs four other <RequiresSecrets> projects. RAGNET_BEIR_CACHE is RUNNER_TEMP/beir,
        fresh every night, so the embedding cache saves that job nothing and the cost above is what it
        would pay. The nightly keeps SciFact and ArguAna PARITY (~15-20 min cold, all four cases),
        which is the published number this milestone exists to protect.
        To run this case:
          {OptInVariable}={cost.Dataset} dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build --filter "{Filter(cost)}"
        """;

    /// <summary>Names the protocol the way the run's own output does.</summary>
    private static string Describe(BeirProtocol protocol) => protocol switch
    {
        BeirProtocol.Parity => "PARITY (one chunk per document, truncated at 256)",
        BeirProtocol.Real => "REAL (RecursiveChunkingStrategy at defaults, max-pooled to documents)",
        BeirProtocol.HybridBm25 =>
            "+BM25 HYBRID ablation cell (parity corpus, dense fused with InMemoryBm25Index via RRF)",
        BeirProtocol.Hyde =>
            "+HYDE ablation cell (parity corpus, searched with the cached hypotheticals' mean vector)",
        BeirProtocol.Reranked =>
            "+RERANKER ablation cell (parity corpus, dense top-k rescored by the cross-encoder)",
        BeirProtocol.Comparison =>
            "COMPARISON CONTROL (parity corpus, scored from a TREC run file read back from disk)",
        BeirProtocol.SemanticKernel =>
            "SEMANTIC KERNEL entrant (unchunked documents in SK's InMemory connector, pinned " +
            "embedder, scored from a TREC run file)",
        BeirProtocol.LangChain =>
            "LANGCHAIN entrant (RecursiveCharacterTextSplitter defaults, InMemoryVectorStore " +
            "cosine, pinned embedder, scored from the Python harness's TREC run file)",
        BeirProtocol.LlamaIndex =>
            "LLAMAINDEX entrant (SentenceSplitter defaults, SimpleVectorStore cosine, pinned " +
            "embedder, scored from the Python harness's TREC run file)",
        BeirProtocol.Haystack =>
            "HAYSTACK entrant (DocumentSplitter defaults, InMemoryDocumentStore dot_product, " +
            "pinned embedder, scored from the Python harness's TREC run file)",
        BeirProtocol.SemanticChunking =>
            "SEMANTIC CHUNKING (documents split on embedding-based sentence boundaries instead of " +
            "indexed one chunk per document, max-pooled back to documents, against the parity " +
            "dense figure)",
        BeirProtocol.GraphRag =>
            "GRAPHRAG (entities and relations extracted into a graph, communities detected, " +
            "local and global search over the result)",
        BeirProtocol.GraphRagDepthControl =>
            "GRAPHRAG DEPTH CONTROL (the Real leg's article chunks alone, dense-retrieved at the " +
            "graph path's candidate depth, max-pooled to documents)",
        BeirProtocol.RealHyde =>
            "+HYDE OVER REAL CHUNKING ablation cell (the Real protocol's chunked corpus, searched " +
            "with the cached hypotheticals' mean vector, against the Real dense figure)",
        BeirProtocol.RealReranked =>
            "+RERANKER OVER REAL CHUNKING ablation cell (the Real protocol's chunked corpus, dense " +
            "top-k rescored by the cross-encoder, against the Real dense figure)",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    /// <summary>
    /// Every cell in the table, paired with the <c>--filter</c> its skip message prints.
    /// </summary>
    /// <remarks>
    /// Exists for <see cref="BeirRunBudgetTests.EveryCellsPrintedFilterCanSelectATest"/>, and it
    /// yields <see cref="Filter"/>'s own output rather than rebuilding it. A guard that composed the
    /// string itself would be checking its own copy, and the string that matched nothing for a
    /// release was the one <see cref="Explain"/> printed — so that is the one that has to be under
    /// test.
    /// </remarks>
    internal static IEnumerable<(string Dataset, BeirProtocol Protocol, string Filter)>
        PrintedFilters()
    {
        foreach (var cost in Costs)
        {
            yield return (cost.Dataset, cost.Protocol, Filter(cost));
        }
    }

    /// <summary>
    /// The <c>--filter</c> that selects this case.
    /// </summary>
    /// <remarks>
    /// <c>DisplayName</c> on both halves, never <c>FullyQualifiedName</c>: the latter stops at the
    /// method name and carries no theory arguments, so it matches every dataset or none. Both test
    /// files already document this, having checked it rather than assumed it. The ablation cells
    /// live three-to-a-class in <see cref="BeirAblationTests"/>, so their discriminator is a
    /// fragment of the test <i>method</i> name rather than the class name — the class alone would
    /// select all three cells and misreport what the quoted cost buys.
    /// <para>
    /// <b><see cref="BeirProtocol.GraphRag"/> is the exception, and the shape of the exception is
    /// the reason the dataset conjunct works everywhere else.</b> A dataset name reaches a display
    /// name only as a theory argument, and the older discriminator — the bare string
    /// <c>"GraphRag"</c>, conjoined with <c>multihop-rag</c> — was written while nothing measured
    /// this protocol at all, on the reasoning that a class added for the graph path would carry
    /// <c>GraphRag</c> in its display name. That was true and it was not enough:
    /// <see cref="GraphRagFunctionsTests"/> is a <c>[Fact]</c> over one pinned slice and takes no
    /// <c>datasetName</c>, so the conjunction selected <b>nothing</b> — and vstest answers an empty
    /// selection with "No test matches the given testcase filter" and <b>exit code 0</b>, so
    /// pasting that line out of a skip message produced a green run for a case that never ran.
    /// </para>
    /// <para>
    /// Two cases now share this cell — the slice guard and
    /// <see cref="BeirGraphRagCorpusTests"/>'s whole-corpus measurement — and the cell prices both,
    /// so the filter selects both, by identity rather than by display name. Each half is a
    /// <c>nameof</c> a rename breaks at compile time, and <c>FullyQualifiedName</c> rather than
    /// <c>DisplayName</c> because the class name is the part that identifies them and it is in the
    /// fully-qualified name whether or not a case is a theory.
    /// </para>
    /// <para>
    /// The corpus half names the <b>method</b>, not just the class, since Phase 5.2.1 put a third
    /// case in that class: <see cref="BeirProtocol.GraphRagDepthControl"/>'s run, which has its own
    /// cell and its own price. A class-level identity would select it under this cell's cost and
    /// misreport what the quoted 43 minutes buys — the same silent-subset shape as a dead branch,
    /// inverted. The cheap chunking check in the same class is left unselected by either filter,
    /// which is fine: it is ungated, needs no model, and the cell text tells the reader to run it
    /// first by its own name.
    /// </para>
    /// </remarks>
    private static string Filter(Cost cost)
    {
        if (cost.Protocol is BeirProtocol.GraphRag)
        {
            return $"FullyQualifiedName~{nameof(GraphRagFunctionsTests)}" +
                   $"|FullyQualifiedName~{nameof(BeirGraphRagCorpusTests)}." +
                   nameof(BeirGraphRagCorpusTests.NdcgAt10_UnderTheGraphPath_IsMeasuredOverTheWholeCorpus) +
                   $"|FullyQualifiedName~{nameof(BeirGraphRagCorpusTests)}." +
                   nameof(BeirGraphRagCorpusTests.Ablations_UnderTheGraphPath_PageRankWeightZero_AndGraphReach) +
                   $"|FullyQualifiedName~{nameof(BeirGraphRagAnswerTests)}";
        }

        var discriminator = cost.Protocol switch
        {
            BeirProtocol.Parity => nameof(BeirParityTests),
            BeirProtocol.Real => nameof(BeirRealChunkingTests),
            BeirProtocol.HybridBm25 => "UnderBm25HybridRrf",
            // Both parity discriminators carry the trailing underscore of their method name, and
            // it is load-bearing rather than cosmetic. RealHyde's method is
            // NdcgAt10_UnderCachedHydeOverRealChunking, so a bare "UnderCachedHyde" is a prefix of
            // it and the parity cell's printed command would select the Real cell too -- handing a
            // reader an expensive measurement they did not ask for, under an exit code that says
            // everything ran as priced. The underscore is what makes the parity name terminal.
            BeirProtocol.Hyde => "UnderCachedHyde_",
            BeirProtocol.Reranked => "UnderCrossEncoderRerank_",
            BeirProtocol.SemanticChunking => "UnderSemanticChunking",
            BeirProtocol.Comparison => nameof(BeirComparisonControlTests),
            BeirProtocol.SemanticKernel => nameof(BeirSemanticKernelDefaultsTests),
            BeirProtocol.LangChain => "ThroughLangChain",
            BeirProtocol.LlamaIndex => "ThroughLlamaIndex",
            BeirProtocol.Haystack => "ThroughHaystack",
            BeirProtocol.GraphRagDepthControl => "DenseAtTheGraphPathsDepth",
            BeirProtocol.RealHyde => "UnderCachedHydeOverRealChunking",
            BeirProtocol.RealReranked => "UnderCrossEncoderRerankOverRealChunking",
            _ => throw new ArgumentOutOfRangeException(nameof(cost), cost.Protocol, null),
        };

        return $"DisplayName~{discriminator}&DisplayName~{cost.Dataset}";
    }

    /// <summary>What one dataset costs under one protocol, and whether the nightly can afford it.</summary>
    /// <param name="Dataset">The BEIR dataset name.</param>
    /// <param name="Protocol">The protocol measured.</param>
    /// <param name="FitsTheNightly">Whether the case runs without being asked for.</param>
    /// <param name="Measured">
    /// What it cost when it was last timed, in prose, and saying so when the figure is an estimate
    /// rather than a measurement.
    /// </param>
    private sealed record Cost(
        string Dataset, BeirProtocol Protocol, bool FitsTheNightly, string Measured);
}
