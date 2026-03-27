namespace Rag.NET.Models.Options;

public sealed class DeepResearchOptions
{
    /// <summary>Maximum number of sufficiency-check iterations before returning accumulated results.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Maximum number of sub-queries generated per insufficiency response.</summary>
    public int SubQueryCount { get; set; } = 3;

    /// <summary>
    /// Custom sufficiency-check prompt sent to the LLM. When null the built-in default is used.
    /// The custom prompt is sent verbatim — the caller is responsible for embedding the query
    /// and context when using a custom prompt.
    /// </summary>
    public string? SufficiencyPrompt { get; set; }
}
