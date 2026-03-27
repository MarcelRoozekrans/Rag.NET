namespace Rag.NET.Models.Options;

public sealed class DeepResearchOptions
{
    public int MaxDepth { get; init; } = 3;
    public int SubQueryCount { get; init; } = 3;

    /// <summary>Custom sufficiency prompt. When null the built-in default is used.</summary>
    public string? SufficiencyPrompt { get; init; }
}
