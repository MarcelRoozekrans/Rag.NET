namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// The system prompt local search answers from, adapted from Microsoft's
/// <c>LOCAL_SEARCH_SYSTEM_PROMPT</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept close to upstream on purpose.</b> The context format and the prompt are one interface:
/// the prompt names <c>Sources</c>, <c>Reports</c>, <c>Entities</c>, <c>Relationships</c> and
/// <c>Claims</c>, which are exactly the section banners
/// <see cref="LocalSearchContextBuilder"/> writes, and it teaches a citation form that refers to
/// the tables' <c>id</c> column. Rendering upstream's context faithfully and then answering from a
/// prompt that knows nothing about it would waste the fidelity entirely — which is the open
/// question the design document flagged and this resolves.
/// </para>
/// <para>
/// <b>One substantive difference.</b> Upstream's wording is "the id (not the index)", because its
/// tables carry stable per-record short ids from the indexing pipeline. This library's graph has no
/// such ids, so the <c>id</c> column is the row's position within this context. The sentence is
/// reworded to say so rather than left to assert something untrue — a model told to cite an id that
/// is not the index, given a table where it is, has been given a contradiction to resolve.
/// </para>
/// <para>
/// Licence: Microsoft's GraphRAG is MIT-licensed, which permits this.
/// </para>
/// </remarks>
internal static class LocalSearchPrompt
{
    /// <summary>Builds the system prompt for one query.</summary>
    /// <param name="context">The rendered context tables.</param>
    /// <param name="responseType">The requested length and format of the answer.</param>
    /// <returns>The system prompt.</returns>
    internal static string Build(string context, string responseType) =>
        $"""
        ---Role---

        You are a helpful assistant responding to questions about data in the tables provided.


        ---Goal---

        Generate a response of the target length and format that responds to the user's question,
        summarizing all information in the input data tables appropriate for the response length and
        format, and incorporating any relevant general knowledge.

        If you don't know the answer, just say so. Do not make anything up.

        Points supported by data should list their data references as follows:

        "This is an example sentence supported by multiple data references [Data: <dataset name> (record ids); <dataset name> (record ids)]."

        Do not list more than 5 record ids in a single reference. Instead, list the top 5 most
        relevant record ids and add "+more" to indicate that there are more.

        For example:

        "Person X is the owner of Company Y and subject to many allegations of wrongdoing [Data: Sources (15, 16), Reports (1), Entities (5, 7); Relationships (23); Claims (2, 7, 34, 46, 64, +more)]."

        where 15, 16, 1, 5, 7, 23, 2, 7, 34, 46, and 64 are values from the `id` column of the
        tables below.

        Do not include information where the supporting evidence for it is not provided.


        ---Target response length and format---

        {responseType}


        ---Data tables---

        {context}
        """;
}
