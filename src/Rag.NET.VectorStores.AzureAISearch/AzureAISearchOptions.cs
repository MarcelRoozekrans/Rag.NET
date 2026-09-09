namespace Rag.NET.AzureAISearch;

/// <summary>Options for <see cref="AzureAISearchVectorStore"/>, validated eagerly in <c>UseAzureAISearch</c>.</summary>
public sealed class AzureAISearchOptions
{
    /// <summary>
    /// How many nearest neighbours the vector arm retrieves, sent as the query's
    /// <c>k</c>. <see langword="null"/> — the default — omits the parameter entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is not "no value"; it is Azure's value.</b> Microsoft's <i>Create a Vector Query</i>
    /// documents that "both <c>k</c> and <c>top</c> are optional. When unspecified, the default
    /// number of results in a response is 50." Omitting the parameter therefore asks for 50, and
    /// leaves the number where the platform can change it.
    /// </para>
    /// <para>
    /// <b>This store used to send <c>k = TopK</c>, which was worse than sending nothing.</b> At a
    /// typical top-5 that narrowed the vector arm's recall to a tenth of Azure's own default —
    /// starving RRF fusion of candidates to fuse, and starving any reranker that follows. It was
    /// reported as "make this settable" (#328); it was also an active regression against the
    /// platform default, which is why the default here is to stop overriding it.
    /// </para>
    /// <para>
    /// <b>Set this to 50 if you turn on semantic ranking.</b> The same Microsoft page is explicit:
    /// "Whenever you use semantic ranking with vectors, set <c>k</c> to 50. Semantic ranker uses up
    /// to 50 matches as input. Specifying less than 50 deprives the semantic ranking models of
    /// necessary inputs." Setting this below 50 while <see cref="EnableSemanticRanking"/> is on is
    /// therefore refused at registration, not merely discouraged — see that option's remarks.
    /// </para>
    /// <para>
    /// Note the asymmetry with <c>TopK</c>: Microsoft documents <c>k</c> as governing "results for
    /// vector-only queries" and <c>top</c> as governing "results for hybrid queries that include a
    /// <c>search</c> parameter", so on the hybrid path this widens the candidate set the fusion
    /// draws from rather than the number of results returned.
    /// </para>
    /// </remarks>
    public int? KNearestNeighborsCount { get; set; }

    /// <summary>
    /// Whether the dense search path asks Azure's semantic ranker to rerank results. Off by
    /// default; opting in changes what <see cref="Rag.NET.Models.SearchResult.Score"/> means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The score becomes ordinal.</b> With the ranker on, the store returns Azure's
    /// <c>RerankerScore</c> — a 0–4 relevance score — unrescaled, and declares
    /// <see cref="Rag.NET.Abstractions.ScoreScale.OpaqueRanking"/>. It is not converted into a
    /// similarity, because an invented similarity is worse than an honest ordinal, and
    /// <c>MinScore</c> is therefore not applied on that path.
    /// </para>
    /// <para>
    /// <b>Per instance, not per request.</b> It reshapes the score of the ordinary search path,
    /// and <see cref="Rag.NET.Abstractions.IScoreScaleAware"/> requires that scale to be constant
    /// for the instance's lifetime — callers probe it once and may cache the answer.
    /// </para>
    /// <para>
    /// <b>Requires a service that actually ranks.</b> Semantic ranking needs Basic tier or higher
    /// in a supporting region. A service that cannot rank answers the request successfully and
    /// returns ordinary scores, so the store throws rather than publish a number it cannot
    /// describe.
    /// </para>
    /// </remarks>
    public bool EnableSemanticRanking { get; set; }
}
