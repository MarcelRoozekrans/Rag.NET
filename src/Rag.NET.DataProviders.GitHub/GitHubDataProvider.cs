using System.Runtime.CompilerServices;
using Octokit;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.GitHub;

/// <summary>
/// Enumerates files from a GitHub repository.
/// On first run (no <see cref="GitHubDataProviderOptions.LastIngestedCommitSha"/>): full recursive tree.
/// On subsequent runs: only files changed since <c>LastIngestedCommitSha</c> via compare API.
/// ETag is the blob SHA — Git's own content hash, so ETag matches guarantee byte-identical content.
/// </summary>
public sealed class GitHubDataProvider : FileContentProviderBase
{
    private readonly string _owner;
    private readonly string _repo;
    /// <summary>The <c>owner/name</c> slug emitted as the <c>repo</c> tag. Fixed for the
    /// provider's lifetime, so it is built once rather than per file.</summary>
    private readonly string _repoSlug;
    private readonly IGitHubClient _client;
    private readonly GitHubDataProviderOptions _options;

    public GitHubDataProvider(
        string owner,
        string repo,
        IGitHubClient client,
        GitHubDataProviderOptions? options = null)
        : base(options ??= new GitHubDataProviderOptions())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        _owner = owner;
        _repo = repo;
        _repoSlug = $"{owner}/{repo}";
        _client = client;
        _options = options;  // options is now guaranteed non-null by ??= above
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.LastIngestedCommitSha is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullTreeHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullTreeHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tree = await _client.Git.Tree
            .GetRecursive(_owner, _repo, _options.Branch).ConfigureAwait(false);

        foreach (var item in tree.Tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type != TreeType.Blob) continue;

            yield return Result<FileHandle, RagError>.Success(
                ToHandle(item.Path, item.Sha, changeStatus: null));
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comparison = await _client.Repository.Commit
            .Compare(_owner, _repo, _options.LastIngestedCommitSha!, _options.Branch)
            .ConfigureAwait(false);

        foreach (var file in comparison.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(file.Status, "removed", StringComparison.Ordinal)) continue;

            yield return Result<FileHandle, RagError>.Success(
                ToHandle(file.Filename, file.Sha, MapChangeStatus(file.Status)));
        }
    }

    /// <summary>
    /// Builds the handle for one file. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>path</c>, <c>repo</c> (<c>owner/name</c>) and <c>ref</c> (the
    /// configured branch) on every run, plus <c>change_status</c> on delta runs only — a full
    /// tree traversal has no notion of change.
    /// </remarks>
    private FileHandle ToHandle(string path, string? sha, string? changeStatus)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["path"] = path,
            ["repo"] = _repoSlug,
            ["ref"]  = _options.Branch,
        };
        if (changeStatus is not null)
            metadata["change_status"] = changeStatus;

        return new FileHandle(
            Id:               path,
            FileName:         Path.GetFileName(path),
            ETag:             sha,
            OpenContentAsync: async ct =>
            {
                var bytes = await _client.Repository.Content
                    .GetRawContent(_owner, _repo, path).ConfigureAwait(false);
                return (Stream)new MemoryStream(bytes);
            },
            Metadata:         metadata);
    }

    /// <summary>
    /// Normalises GitHub's compare-API <c>status</c> onto the cross-connector
    /// <c>change_status</c> vocabulary (<c>added</c>/<c>modified</c>/<c>removed</c>/<c>renamed</c>)
    /// so the tag can be filtered on identically across GitLab, Bitbucket and Box.
    /// <list type="bullet">
    /// <item><c>added</c> → <c>added</c>; <c>copied</c> → <c>added</c> (a copy creates a new blob
    /// at the destination path, which is the path this handle carries).</item>
    /// <item><c>modified</c> → <c>modified</c>; <c>changed</c> → <c>modified</c> (GitHub uses
    /// <c>changed</c> for mode/permission-only edits).</item>
    /// <item><c>renamed</c> → <c>renamed</c>.</item>
    /// <item><c>removed</c> → <c>removed</c>. Unreachable in practice: removed files are skipped
    /// before a handle is built. Mapped anyway so the vocabulary is complete.</item>
    /// <item>Anything else (including <c>unchanged</c>, which asserts no change at all) →
    /// <see langword="null"/>, and the key is omitted rather than carrying a vendor string the
    /// other connectors cannot produce.</item>
    /// </list>
    /// </summary>
    private static string? MapChangeStatus(string? status) => status switch
    {
        "added" or "copied"     => "added",
        "modified" or "changed" => "modified",
        "renamed"               => "renamed",
        "removed"               => "removed",
        _                       => null,
    };
}
