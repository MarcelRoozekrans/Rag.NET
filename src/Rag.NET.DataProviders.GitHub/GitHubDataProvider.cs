using System.Runtime.CompilerServices;
using Octokit;
using Rag.NET.DataProviders;

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
        _client = client;
        _options = options;  // options is now guaranteed non-null by ??= above
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.LastIngestedCommitSha is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullTreeHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullTreeHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tree = await _client.Git.Tree
            .GetRecursive(_owner, _repo, _options.Branch).ConfigureAwait(false);

        foreach (var item in tree.Tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type != TreeType.Blob) continue;

            var capturedPath = item.Path;
            yield return new FileHandle(
                Id:               item.Path,
                FileName:         Path.GetFileName(item.Path),
                ETag:             item.Sha,
                OpenContentAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content
                        .GetRawContent(_owner, _repo, capturedPath).ConfigureAwait(false);
                    return (Stream)new MemoryStream(bytes);
                });
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comparison = await _client.Repository.Commit
            .Compare(_owner, _repo, _options.LastIngestedCommitSha!, _options.Branch)
            .ConfigureAwait(false);

        foreach (var file in comparison.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(file.Status, "removed", StringComparison.Ordinal)) continue;

            var capturedPath = file.Filename;
            yield return new FileHandle(
                Id:               file.Filename,
                FileName:         Path.GetFileName(file.Filename),
                ETag:             file.Sha,
                OpenContentAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content
                        .GetRawContent(_owner, _repo, capturedPath).ConfigureAwait(false);
                    return (Stream)new MemoryStream(bytes);
                });
        }
    }
}
