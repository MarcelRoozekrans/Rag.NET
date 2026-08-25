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
    /// necessary inputs." Semantic ranking is not implemented here yet — #328 stays open for it —
    /// so this note is for anyone configuring the index themselves in the meantime.
    /// </para>
    /// <para>
    /// Note the asymmetry with <c>TopK</c>: Microsoft documents <c>k</c> as governing "results for
    /// vector-only queries" and <c>top</c> as governing "results for hybrid queries that include a
    /// <c>search</c> parameter", so on the hybrid path this widens the candidate set the fusion
    /// draws from rather than the number of results returned.
    /// </para>
    /// </remarks>
    public int? KNearestNeighborsCount { get; set; }
}
