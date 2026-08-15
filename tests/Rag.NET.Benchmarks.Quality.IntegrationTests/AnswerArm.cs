namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>The arms by name — the strings the environment variable and the pin table use.</summary>
internal static class AnswerArm
{
    /// <summary>Dense top-6 over the article-only store: the Real leg's chunks, nothing else.</summary>
    public const string Dense = "dense";

    /// <summary>
    /// Dense top-6 over the graph run's store with no graph behaviour — the answer-level analogue of
    /// #229's candidate-set control. Against <see cref="Dense"/> it prices what the 303,503
    /// graph-derived units do to the context; against <see cref="Local"/> it prices the behaviour.
    /// </summary>
    public const string Control = "control";

    /// <summary>GraphRAG local search as shipped (PageRankWeight 0.3), top-6 of what it returns.</summary>
    public const string Local = "local";

    /// <summary>GraphRAG global search: the synthesised answer first, then the next candidates.</summary>
    public const string Global = "global";

    public static readonly IReadOnlyList<string> All = [Dense, Control, Local, Global];
}
