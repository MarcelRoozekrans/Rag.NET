namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for <see cref="Rag.NET.Models.Options.SynthesisStrategy.Refine"/> synthesis: an initial
/// answer is generated from the first source chunk, then iteratively refined against each
/// remaining chunk in turn. Read only when <see cref="RagOptions.SynthesisStrategy"/> is
/// <see cref="Rag.NET.Models.Options.SynthesisStrategy.Refine"/>.
/// </summary>
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
