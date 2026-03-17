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
public sealed class GitHubDataProvider : IFileContentProvider
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        _owner = owner;
        _repo = repo;
        _client = client;
        _options = options ?? new GitHubDataProviderOptions();
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_options.LastIngestedCommitSha is not null)
        {
            await foreach (var entry in GetDeltaFilesAsync(cancellationToken).ConfigureAwait(false))
                yield return entry;
        }
        else
        {
            await foreach (var entry in GetFullTreeFilesAsync(cancellationToken).ConfigureAwait(false))
                yield return entry;
        }
    }

    private async IAsyncEnumerable<FileEntry> GetFullTreeFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tree = await _client.Git.Tree.GetRecursive(_owner, _repo, _options.Branch).ConfigureAwait(false);

        foreach (var item in tree.Tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type != TreeType.Blob) continue;
            if (!MatchesExtension(item.Path)) continue;
            if (_options.Filter is not null && !_options.Filter(item.Path)) continue;

            var capturedPath = item.Path;
            yield return new FileEntry(
                Id: item.Path,
                FileName: Path.GetFileName(item.Path),
                OpenContentAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content.GetRawContent(_owner, _repo, capturedPath).ConfigureAwait(false);
                    return (Stream)new MemoryStream(bytes);
                },
                ETag: item.Sha);
        }
    }

    private async IAsyncEnumerable<FileEntry> GetDeltaFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comparison = await _client.Repository.Commit
            .Compare(_owner, _repo, _options.LastIngestedCommitSha!, _options.Branch).ConfigureAwait(false);

        foreach (var file in comparison.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(file.Status, "removed", StringComparison.Ordinal)) continue;
            if (!MatchesExtension(file.Filename)) continue;
            if (_options.Filter is not null && !_options.Filter(file.Filename)) continue;

            var capturedPath = file.Filename;
            yield return new FileEntry(
                Id: file.Filename,
                FileName: Path.GetFileName(file.Filename),
                OpenContentAsync: async ct =>
                {
                    var bytes = await _client.Repository.Content.GetRawContent(_owner, _repo, capturedPath).ConfigureAwait(false);
                    return (Stream)new MemoryStream(bytes);
                },
                ETag: file.Sha);
        }
    }

    private bool MatchesExtension(string path)
    {
        if (_options.Extensions is ["*"]) return true;
        var ext = Path.GetExtension(path);
        return _options.Extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, "*", StringComparison.Ordinal));
    }
}
