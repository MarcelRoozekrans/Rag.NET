using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Bitbucket;

/// <summary>Configuration for <see cref="BitbucketDataProvider"/>.</summary>
public sealed class BitbucketOptions : CloudStorageOptions
{
    /// <summary>Bitbucket workspace slug (e.g. <c>myteam</c>).</summary>
    public required string Workspace { get; set; }

    /// <summary>Repository slug (e.g. <c>my-repo</c>).</summary>
    public required string RepoSlug { get; set; }

    /// <summary>Git ref to enumerate (branch, tag, or commit hash). Defaults to <c>main</c>.</summary>
    public string Ref { get; set; } = "main";

    /// <summary>
    /// Commit hash of the last ingested revision.
    /// Maps to <see cref="CloudStorageOptions.DeltaToken"/> for delta traversal.
    /// When <see langword="null"/>, a full traversal is performed.
    /// </summary>
    public string? LastIngestedCommitHash
    {
        get => DeltaToken;
        init => DeltaToken = value;
    }
}
