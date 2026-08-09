namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for multi-query expansion: an LLM generates alternative phrasings of the query, each
/// retrieved separately, and the result sets are merged before downstream ranking.
/// </summary>
public sealed class MultiQueryOptions
{
    /// <summary>
    /// Number of alternative query phrasings to generate. Must be at least 1; enforced by
    /// <c>UseMultiQueryRetrieval</c> at registration time.
    /// </summary>
    public int VariantCount { get; set; } = 3;

    /// <summary>
    /// Prompt sent to the <c>IChatClient</c> to generate query variants.
    /// Two placeholders are required:
    /// <list type="bullet">
    /// <item><description><c>{count}</c> — replaced with the requested number of variants.</description></item>
    /// <item><description><c>{query}</c> — replaced with the original user query.</description></item>
    /// </list>
    /// </summary>
    public string PromptTemplate { get; set; } =
        "Generate {count} different phrasings of the following question.\n" +
        "Return only the rephrased questions, one per line, with no numbering or extra text.\n\n" +
        "Question: {query}";
}
