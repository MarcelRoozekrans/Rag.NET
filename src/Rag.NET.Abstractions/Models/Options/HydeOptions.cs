namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for HyDE (Hypothetical Document Embeddings): before retrieving, an LLM generates one or
/// more hypothetical answers to the query, which are embedded and used as the search vector
/// instead of embedding the query text directly.
/// </summary>
public sealed class HydeOptions
{
    /// <summary>
    /// Prompt sent to the <c>IChatClient</c> to generate the hypothetical document.
    /// One placeholder is required:
    /// <list type="bullet">
    /// <item><description><c>{query}</c> — replaced with the user's query.</description></item>
    /// </list>
    /// The LLM response is used verbatim as the hypothetical document text.
    /// </summary>
    public string PromptTemplate { get; set; } =
        "Please write a short passage that directly answers the following question. " +
        "Write only the passage, no preamble or explanation.\n\n" +
        "Question: {query}";

    /// <summary>
    /// Number of hypothetical documents to generate per query. When greater than 1 and an
    /// embedding generator is available, the documents are embedded in one batch and their
    /// mean (L2-normalized) embedding is used as the search vector, smoothing out
    /// single-hypothesis variance. Must be at least 1. Default: 3.
    /// </summary>
    /// <remarks>
    /// Cost: each retrieval spends <c>HypothesisCount</c> LLM calls plus
    /// <c>HypothesisCount</c> embedding inputs (one batch call).
    /// </remarks>
    public int HypothesisCount { get; set; } = 3;

    /// <summary>
    /// Sampling temperature for hypothesis generation. A relatively high default (0.8)
    /// produces diverse hypotheses, which is what makes averaging multiple of them useful.
    /// </summary>
    public float HypothesisTemperature { get; set; } = 0.8f;
}
