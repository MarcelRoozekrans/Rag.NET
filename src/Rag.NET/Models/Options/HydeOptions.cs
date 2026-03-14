namespace Rag.NET.Models.Options;

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
}
