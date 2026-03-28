using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitHub;

/// <summary>Configuration for <see cref="GitHubDataProvider"/>.</summary>
public sealed class GitHubDataProviderOptions : CloudStorageOptions
{
    /// <summary>Branch or ref to traverse. Default: <c>"main"</c>.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>
    /// When set, performs a delta run: only files changed since this commit SHA are returned.
    /// Maps to <see cref="CloudStorageOptions.DeltaToken"/>.
    /// When <see langword="null"/>, performs a full tree traversal.
    /// </summary>
    public string? LastIngestedCommitSha
    {
        get => DeltaToken;
        init => DeltaToken = value;
    }
}
