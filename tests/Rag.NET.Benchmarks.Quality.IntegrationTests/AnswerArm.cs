namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>The arms by name — the strings the environment variable and the pin table use.</summary>
internal static class AnswerArm
{
    /// <summary>Dense top-6 over the article-only store: the Real leg's chunks, nothing else.</summary>
    public const string Dense = "dense";

    /// <summary>
    /// Dense top-6 over the graph run's store with no graph behaviour — the answer-level analogue of
    /// #229's candidate-set control. Against <see cref="Dense"/> it prices what the 303,503
    /// graph-derived units do to the context; against <see cref="Local"/> it prices the behaviour.
    /// </summary>
    public const string Control = "control";

    /// <summary>
    /// GraphRAG local search at <c>PageRankWeight = 0.3</c>, top-6 of what it returns.
    /// </summary>
    /// <remarks>
    /// Said "as shipped" until #239 changed the library default to 0. The figures this arm pins were
    /// measured at 0.3, so <c>GraphRagRun</c> now sets that value explicitly and this arm deliberately
    /// measures a <b>non-default</b> configuration. The shipped configuration corresponds to the
    /// <c>PageRankWeight = 0</c> ablation, which reproduced the candidate-set control exactly.
    /// </remarks>
    public const string Local = "local";

    /// <summary>GraphRAG global search: the synthesised answer first, then the next candidates.</summary>
    public const string Global = "global";

    /// <summary>
    /// The same dense candidates as <see cref="Control"/>, with every graph-derived unit dropped
    /// before the top-6 is taken — issue #247's option (c), over-fetch and filter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it isolates.</b> <see cref="Control"/> and this arm see the identical top-500 from
    /// the identical store; the only difference is that this one removes the entity, relationship
    /// and community-report chunks before choosing six. So <c>filtered − control</c> is the cost of
    /// the pollution <i>and nothing else</i> — same store, same depth, same embedder, same model,
    /// same prompt. If they come out equal, #247's premise is wrong.
    /// </para>
    /// <para>
    /// <b>Why it costs nothing extra to retrieve.</b> The over-fetch option (c) needs is already
    /// there: local search hands back its top-500 candidate list, and six survivors are taken from
    /// it either way. That is what makes (c) measurable before the library commits to it — no store
    /// change, no filter-contract change, no re-indexing.
    /// </para>
    /// <para>
    /// Against <see cref="Dense"/> it answers the question the library actually needs: does
    /// filtering recover the article-only accuracy, or is some of the gap caused by something other
    /// than pollution?
    /// </para>
    /// </remarks>
    public const string Filtered = "filtered";

    /// <summary>
    /// Microsoft's local search as specified: entity selection by description embedding, community
    /// reports, an uncapped in-network relationship table, source chunks via entity provenance, all
    /// under a 12,000-token budget — <c>IGraphRagSearch</c>, not the retrieval pipeline.
    /// </summary>
    /// <remarks>
    /// <b>Not a variant of <see cref="Local"/>; a different thing with the same name upstream.</b>
    /// That arm is the PageRank blend at weight 0.3 over dense candidates, which is not in
    /// Microsoft's local search at all. This arm is what Milestone 5.2 believed it was measuring
    /// when it concluded GraphRAG does not help on this corpus. Both are kept: the comparison
    /// between them is the measurement.
    /// </remarks>
    public const string LocalSpec = "localspec";

    /// <summary>
    /// RAPTOR at its shipped defaults since #340: corpus-level tree, <c>Blend</c>, top-6.
    /// </summary>
    /// <remarks>
    /// <b>This is the arm whose figure is RAPTOR's result on this corpus.</b> Not <see cref="Raptor"/>
    /// — that is the retired per-document variant. Publishing the per-document number as RAPTOR's
    /// would repeat 5.2's misattribution, where a variant's figure was presented as the technique's
    /// and took #316, #323 and #326 to unpick.
    /// </remarks>
    public const string RaptorCorpus = "raptorcorpus";

    /// <summary>
    /// RAPTOR's per-document tree — <c>TreeScope = PerDocument</c>, <c>Blend</c>, top-6.
    /// </summary>
    /// <remarks>
    /// The behaviour that shipped before #340, kept selectable rather than deleted precisely so
    /// this comparison could be run. <c>raptorcorpus − raptor</c> is what the 6.2.3 breaking change
    /// bought, on the corpus it was justified against — the number #331 was filed on and nobody has.
    /// </remarks>
    public const string Raptor = "raptor";

    /// <summary>
    /// The same corpus store as <see cref="RaptorCorpus"/>, with every summary chunk dropped
    /// before the top-6 is taken.
    /// </summary>
    /// <remarks>
    /// <b>A validation gate before it is a result.</b> Against <see cref="Dense"/> it should be
    /// ≈ 0: both see the same article chunks, so a difference means the corpora diverged and no
    /// other figure in the table means anything. Against <see cref="RaptorCorpus"/> it prices what
    /// the summaries do to the answer — negative means displacement, the graph path's finding
    /// (#247) reproduced for RAPTOR.
    /// </remarks>
    public const string RaptorFiltered = "raptorfiltered";

    /// <summary>
    /// The corpus store under <c>Boost</c> at the shipped 1.2, <b>after</b> Phase 6.2.4's
    /// over-fetch fix.
    /// </summary>
    /// <remarks>
    /// <b>Measures a working <c>Boost</c>, not the broken one.</b> Before 6.2.4 the behaviour saw
    /// only the truncated top-k, so it could reorder summaries within the result set but never
    /// promote one into it — provable from the code, and therefore not worth an answer arm.
    /// <c>raptorboost − raptorcorpus</c> is the question reading cannot answer: does promoting
    /// summaries into the context actually help?
    /// </remarks>
    public const string RaptorBoost = "raptorboost";

    /// <summary>
    /// Dense retrieval, answered by the shipped <c>ChatAnswerEngine</c> in one call — <b>the
    /// control for every other engine arm</b>.
    /// <para>
    /// Each engine builds its own prompts, so differencing an engine against <see cref="Dense"/>
    /// would bundle the mechanism with a prompt change and no result could say which caused what.
    /// This arm is single-shot through the same routing, so <c>chatengine − dense</c> is the prompt
    /// effect alone and <c>&lt;engine&gt; − chatengine</c> is the mechanism alone.
    /// </para>
    /// </summary>
    public const string ChatEngine = "chatengine";

    /// <summary>
    /// Dense retrieval, answered by <c>MapReduceAnswerEngine</c>: one call per context chunk, then
    /// one reduce over their outputs. Seven calls at top-6, but roughly a single-shot answer's token
    /// count, because each map call carries one chunk rather than all six.
    /// </summary>
    public const string MapReduce = "mapreduce";

    /// <summary>
    /// Dense retrieval, answered by <c>RefineAnswerEngine</c>: an initial answer from the first
    /// chunk, then one sequential rewrite per remaining chunk. Six calls at top-6.
    /// </summary>
    public const string Refine = "refine";

    /// <summary>
    /// Dense retrieval, answered by <c>FlareAnswerEngine</c> <b>as shipped</b> — sentence by
    /// sentence, re-retrieving mid-generation whenever a sentence scores below
    /// <c>ConfidenceThreshold</c>.
    /// <para>
    /// <b>This arm does not hold retrieval fixed</b>, which is why <see cref="FlareFixed"/> exists
    /// beside it: <c>flare − flarefixed</c> is what the lookahead buys, and it is the only
    /// difference here that is not purely a generation difference.
    /// </para>
    /// </summary>
    public const string Flare = "flare";

    /// <summary>
    /// Dense retrieval, answered by <c>FlareAnswerEngine</c> with <c>MaxRetrievals = 0</c> — the
    /// sentence-by-sentence mechanism with lookahead off, so retrieval is held fixed and the arm is
    /// comparable to <see cref="MapReduce"/> and <see cref="Refine"/>.
    /// </summary>
    public const string FlareFixed = "flarefixed";

    public static readonly IReadOnlyList<string> All =
        [
            Dense, Control, Local, Global, Filtered, LocalSpec, RaptorCorpus, Raptor, RaptorFiltered, RaptorBoost,
            ChatEngine, MapReduce, Refine, Flare, FlareFixed
        ];
}
