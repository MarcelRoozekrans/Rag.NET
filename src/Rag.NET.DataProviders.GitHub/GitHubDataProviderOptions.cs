namespace Rag.NET.DataProviders.GitHub;

/// <summary>Configuration for <see cref="GitHubDataProvider"/>.</summary>
public sealed class GitHubDataProviderOptions
{
    /// <summary>Branch or ref to traverse. Default: <c>"main"</c>.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>
    /// File extensions to include (e.g. <c>[".md", ".cs"]</c>).
    /// Defaults to <c>["*"]</c> which matches all extensions.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = ["*"];

    /// <summary>Optional predicate to exclude files by repository path.</summary>
    public Func<string, bool>? Filter { get; init; }

    /// <summary>
    /// When set, performs a delta run: only files changed since this commit SHA are returned.
    /// When <see langword="null"/>, performs a full tree traversal.
    /// Update this value after a successful run to enable incremental ingestion.
    /// </summary>
    public string? LastIngestedCommitSha { get; init; }
}
