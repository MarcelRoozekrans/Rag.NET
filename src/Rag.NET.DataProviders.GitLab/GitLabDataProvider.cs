using System.Runtime.CompilerServices;
using NGitLab;
using NGitLab.Models;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitLab;

/// <summary>
/// Enumerates files from a GitLab repository.
/// On first run (no <see cref="GitLabOptions.LastIngestedCommitSha"/>): full recursive tree.
/// On subsequent runs: only files changed since <c>LastIngestedCommitSha</c> via compare API.
/// ETag is the blob SHA — Git's own content hash, so ETag matches guarantee byte-identical content.
/// </summary>
public sealed class GitLabDataProvider : FileContentProviderBase
{
    private readonly IGitLabClient _client;
    private readonly GitLabOptions _options;

    public GitLabDataProvider(
        IGitLabClient client,
        GitLabOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.LastIngestedCommitSha is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullTreeHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullTreeHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repo = _client.GetRepository((ProjectId)_options.ProjectIdOrPath);
        var treeOptions = new RepositoryGetTreeOptions
        {
            Ref = _options.Ref,
            Recursive = true,
        };

        await foreach (var item in repo.GetTreeAsync(treeOptions).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type != ObjectType.blob) continue;

            var capturedPath = item.Path;
            var blobId = item.Id.ToString();
            yield return new FileHandle(
                Id:               capturedPath,
                FileName:         Path.GetFileName(capturedPath),
                ETag:             blobId,
                OpenContentAsync: async ct =>
                {
                    var ms = new MemoryStream();
                    await repo.Files.GetRawAsync(capturedPath, stream => stream.CopyToAsync(ms, ct),
                        new GetRawFileRequest { Ref = _options.Ref }, ct).ConfigureAwait(false);
                    ms.Position = 0;
                    return (Stream)ms;
                });
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repo = _client.GetRepository((ProjectId)_options.ProjectIdOrPath);
        var comparison = await repo.CompareAsync(
            new CompareQuery(_options.LastIngestedCommitSha!, _options.Ref), cancellationToken)
            .ConfigureAwait(false);

        foreach (var diff in comparison.Diff)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diff.IsDeletedFile) continue;

            var capturedPath = diff.NewPath;
            yield return new FileHandle(
                Id:               capturedPath,
                FileName:         Path.GetFileName(capturedPath),
                ETag:             null,
                OpenContentAsync: async ct =>
                {
                    var ms = new MemoryStream();
                    await repo.Files.GetRawAsync(capturedPath, stream => stream.CopyToAsync(ms, ct),
                        new GetRawFileRequest { Ref = _options.Ref }, ct).ConfigureAwait(false);
                    ms.Position = 0;
                    return (Stream)ms;
                });
        }
    }
}
