namespace Rag.NET.Models.Options;

public sealed class MapReduceOptions
{
    public int MapConcurrency { get; init; } = 5;

    /// <summary>
    /// Prompt template used for the map step. Supports the following placeholders:
    /// <list type="bullet">
    ///   <item><term>{chunk}</term><description>The text of the source chunk being evaluated.</description></item>
    ///   <item><term>{query}</term><description>The user's query.</description></item>
    /// </list>
    /// When <see langword="null"/>, the engine uses its built-in default map prompt.
    /// </summary>
    public string? MapPromptTemplate { get; init; }

    /// <summary>
    /// Prompt template used for the reduce step. Supports the following placeholders:
    /// <list type="bullet">
    ///   <item><term>{partials}</term><description>The newline-separated partial answers produced by the map step.</description></item>
    ///   <item><term>{query}</term><description>The user's query.</description></item>
    /// </list>
    /// When <see langword="null"/>, the engine uses its built-in default reduce prompt.
    /// </summary>
    public string? ReducePromptTemplate { get; init; }
}
