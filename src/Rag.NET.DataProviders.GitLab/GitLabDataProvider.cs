using System.Runtime.CompilerServices;
using NGitLab;
using NGitLab.Models;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

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

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.LastIngestedCommitSha is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullTreeHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullTreeHandlesAsync(
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

            yield return Result<FileHandle, RagError>.Success(
                ToHandle(repo, item.Path, item.Id.ToString(), changeStatus: null));
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
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

            yield return Result<FileHandle, RagError>.Success(
                ToHandle(repo, diff.NewPath, etag: null, MapChangeStatus(diff)));
        }
    }

    /// <summary>
    /// Builds the handle for one blob. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>path</c>, <c>project</c> (the configured id or
    /// <c>namespace/project</c> path) and <c>ref</c> on every run, plus <c>change_status</c> on
    /// delta runs only — a full tree traversal has no notion of change.
    /// </remarks>
    private FileHandle ToHandle(
        IRepositoryClient repo, string path, string? etag, string? changeStatus)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["path"]    = path,
            ["project"] = _options.ProjectIdOrPath,
            ["ref"]     = _options.Ref,
        };
        if (changeStatus is not null)
            metadata["change_status"] = changeStatus;

        return new FileHandle(
            Id:               path,
            FileName:         Path.GetFileName(path),
            ETag:             etag,
            OpenContentAsync: async ct =>
            {
                var ms = new MemoryStream();
                await repo.Files.GetRawAsync(path, stream => stream.CopyToAsync(ms, ct),
                    new GetRawFileRequest { Ref = _options.Ref }, ct).ConfigureAwait(false);
                ms.Position = 0;
                return (Stream)ms;
            },
            Metadata:         metadata);
    }

    /// <summary>
    /// Normalises GitLab's boolean diff flags onto the cross-connector <c>change_status</c>
    /// vocabulary (<c>added</c>/<c>modified</c>/<c>removed</c>/<c>renamed</c>) so the tag can be
    /// filtered on identically across GitHub, Bitbucket and Box.
    /// <list type="bullet">
    /// <item><see cref="Diff.IsNewFile"/> → <c>added</c>.</item>
    /// <item><see cref="Diff.IsRenamedFile"/> → <c>renamed</c>. Checked after
    /// <c>IsNewFile</c>: GitLab never sets both, and if it ever did, "this path is new" is the
    /// more useful claim.</item>
    /// <item><see cref="Diff.IsDeletedFile"/> → <c>removed</c>. Unreachable in practice: deleted
    /// files are skipped before a handle is built. Mapped anyway so the vocabulary is complete.</item>
    /// <item>No flag set → <c>modified</c>. GitLab has no explicit "modified" flag; an existing
    /// path appearing in a diff with none of the three flags <i>is</i> a content edit.</item>
    /// </list>
    /// </summary>
    private static string MapChangeStatus(Diff diff)
    {
        if (diff.IsNewFile)     return "added";
        if (diff.IsRenamedFile) return "renamed";
        if (diff.IsDeletedFile) return "removed";
        return "modified";
    }
}
