namespace Rag.NET.Models.Options;

public sealed class RefineOptions
{
    /// <summary>
    /// Prompt template for the initial answer generated from the first source chunk.
    /// Supported tokens: <c>{chunk}</c> (chunk text), <c>{query}</c> (user question).
    /// </summary>
    public string? InitialPromptTemplate { get; init; }

    /// <summary>
    /// Prompt template used for each subsequent refinement step.
    /// Supported tokens: <c>{answer}</c> (current answer), <c>{chunk}</c> (chunk text), <c>{query}</c> (user question).
    /// </summary>
    public string? RefinePromptTemplate { get; init; }
}
