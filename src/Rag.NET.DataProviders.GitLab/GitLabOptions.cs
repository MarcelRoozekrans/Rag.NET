using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitLab;

/// <summary>Configuration for <see cref="GitLabDataProvider"/>.</summary>
public sealed class GitLabOptions : CloudStorageOptions
{
    /// <summary>
    /// GitLab instance base URL (e.g. <c>https://gitlab.com</c>).
    /// Read after the <c>configure</c> callback runs — the private-token client is constructed
    /// from this value, so a callback that changes it changes the instance targeted.
    /// </summary>
    public required string BaseUrl { get; set; }

    /// <summary>Numeric project ID or <c>namespace/project</c> path.</summary>
    public required string ProjectIdOrPath { get; set; }

    /// <summary>Branch or ref to traverse. Default: <c>"main"</c>.</summary>
    public string Ref { get; set; } = "main";

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
