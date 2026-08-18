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
    /// <b>This arm is now what the library does by default.</b> #247's filter shipped as
    /// <c>GraphRagRetrievalOptions.FilterGraphChunksFromResults</c>, defaulting to on, so this arm
    /// stopped being a hypothetical and became the shipped path — measured here against the
    /// <see cref="Control"/> that shows what it costs to turn off.
    /// </para>
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

    public static readonly IReadOnlyList<string> All = [Dense, Control, Local, Global, Filtered];
}
