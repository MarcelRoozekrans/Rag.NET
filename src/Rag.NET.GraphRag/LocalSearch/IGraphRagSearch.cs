namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// GraphRAG's own query surface — a search strategy, not a step in someone else's pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local search is not a re-ranker, and expressing it as one is how this library lost it
/// twice.</b> Microsoft's local search builds a context window out of five kinds of graph material
/// and answers from it; it ranks no documents and re-scores nothing. Rag.NET shipped it as an
/// <c>IRetrievalBehavior</c> — a thing that takes a ranked candidate list and returns a ranked
/// candidate list — which left it with nowhere to put entities, relationships or reports, so it
/// blended PageRank into the scores instead. That blend was the entire −0.02761 nDCG@10 charged to
/// GraphRAG in Milestone 5.2.
/// </para>
/// <para>
/// So this is a separate entry point, matching upstream's own structure. The cost, accepted
/// deliberately (#316): <b>local search no longer composes with hybrid search, reranking, or the
/// rest of the retrieval pipeline.</b> A caller picks this or picks the pipeline. The composition
/// that existed before was never real — the blend re-scored candidates the graph had no say in
/// choosing.
/// </para>
/// </remarks>
public interface IGraphRagSearch
{
    /// <summary>
    /// Assembles the context window a local-search answer would be generated from, without
    /// generating it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LocalSearchAsync"/> because the context is the interesting half and
    /// the expensive half is the model call. Evaluating retrieval, debugging an answer that cited
    /// nothing, or measuring what the graph actually contributes all want the context and not the
    /// completion.
    /// </remarks>
    /// <param name="query">The user's question.</param>
    /// <param name="cancellationToken">Cancels the searches and store reads.</param>
    /// <returns>The assembled context and what each section cost.</returns>
    /// <remarks>Equivalent to calling the three-argument overload with an empty history.</remarks>
    Task<LocalSearchContext> BuildLocalContextAsync(
        string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles the context window a local-search answer would be generated from, folding prior
    /// conversation turns into both entity selection and the rendered context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="LocalSearchAsync(string, IReadOnlyList{ConversationTurn}, CancellationToken)"/>
    /// because the context is the interesting half and the expensive half is the model call.
    /// Evaluating retrieval, debugging an answer that cited nothing, or measuring what the graph
    /// actually contributes all want the context and not the completion.
    /// </para>
    /// <para>
    /// History reaches the result through two independent paths (spec §9.5). The last
    /// <see cref="LocalSearchContextOptions.ConversationHistoryMaxTurns"/> user turns are folded
    /// onto <paramref name="query"/>, newest first, before entity selection — a follow-up question
    /// such as "and who measured it?" embeds to almost nothing on its own, and it is the preceding
    /// questions that make it match an entity. Separately, the history is rendered into its own
    /// context section, oldest pairs first, the way <see cref="LocalSearchContextBuilder"/> already
    /// builds it. The two do not use the same ordering, and reproducing that disagreement is
    /// deliberate — it is upstream's own behaviour, in the same call.
    /// </para>
    /// </remarks>
    /// <param name="query">The user's question.</param>
    /// <param name="history">
    /// The conversation so far, oldest turn first. Only
    /// <see cref="LocalSearchContextOptions.ConversationHistoryMaxTurns"/> of them reach the
    /// assembled context — as QA pairs in the rendered section, and as the last that many user
    /// turns folded onto the query for entity selection.
    /// </param>
    /// <param name="cancellationToken">Cancels the searches and store reads.</param>
    /// <returns>The assembled context and what each section cost.</returns>
    Task<LocalSearchContext> BuildLocalContextAsync(
        string query, IReadOnlyList<ConversationTurn> history, CancellationToken cancellationToken = default);

    /// <summary>Answers a question from a locally-assembled graph context.</summary>
    /// <param name="query">The user's question.</param>
    /// <param name="cancellationToken">Cancels the searches, store reads and the model call.</param>
    /// <returns>The answer and the context it was generated from.</returns>
    /// <remarks>Equivalent to calling the three-argument overload with an empty history.</remarks>
    Task<LocalSearchAnswer> LocalSearchAsync(
        string query, CancellationToken cancellationToken = default);

    /// <summary>Answers a question from a locally-assembled graph context, given prior conversation.</summary>
    /// <param name="query">The user's question.</param>
    /// <param name="history">
    /// The conversation so far, oldest turn first. Only
    /// <see cref="LocalSearchContextOptions.ConversationHistoryMaxTurns"/> of them reach the
    /// assembled context — see
    /// <see cref="BuildLocalContextAsync(string, IReadOnlyList{ConversationTurn}, CancellationToken)"/>
    /// for how.
    /// </param>
    /// <param name="cancellationToken">Cancels the searches, store reads and the model call.</param>
    /// <returns>The answer and the context it was generated from.</returns>
    Task<LocalSearchAnswer> LocalSearchAsync(
        string query, IReadOnlyList<ConversationTurn> history, CancellationToken cancellationToken = default);
}
