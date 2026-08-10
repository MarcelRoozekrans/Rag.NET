using System.Runtime.CompilerServices;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Bitbucket;

/// <summary>
/// Enumerates files from a Bitbucket Cloud repository via the REST API 2.0.
/// <para>
/// A full run lists the source tree at the configured <see cref="BitbucketOptions.Ref"/>.
/// A delta run uses the diffstat endpoint to enumerate only files changed since
/// <see cref="BitbucketOptions.LastIngestedCommitHash"/>.
/// </para>
/// </summary>
public sealed class BitbucketDataProvider : FileContentProviderBase
{
    private readonly IBitbucketApi _api;
    private readonly HttpClient _http;
    private readonly BitbucketOptions _options;
    /// <summary>The <c>workspace/slug</c> pair emitted as the <c>repo</c> tag. Fixed for the
    /// provider's lifetime, so it is built once rather than per file.</summary>
    private readonly string _repoSlug;

    internal BitbucketDataProvider(IBitbucketApi api, HttpClient http, BitbucketOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(http);
        _api = api;
        _http = http;
        _options = options;
        _repoSlug = $"{options.Workspace}/{options.RepoSlug}";
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var result = await _api.GetSourceAsync(
                _options.Workspace,
                _options.RepoSlug,
                _options.Ref,
                path: "",
                pagelen: 100,
                page: pageToken,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));
                yield break;
            }

            var page = result.Value;
            for (int i = 0; i < page.Values.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = page.Values[i];

                if (!string.Equals(entry.Type, "commit_file", StringComparison.Ordinal))
                    continue;

                yield return Result<FileHandle, RagError>.Success(
                    ToHandle(entry.Path, entry.Commit?.Hash, changeStatus: null));
            }

            pageToken = ExtractPageToken(page.Next);
        }
        while (pageToken is not null);
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var spec = $"{_options.DeltaToken}..{_options.Ref}";
        string? pageToken = null;
        do
        {
            var result = await _api.GetDiffstatAsync(
                _options.Workspace,
                _options.RepoSlug,
                spec,
                pagelen: 100,
                page: pageToken,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(result.Error.StatusCode, result.Error.Message));
                yield break;
            }

            var page = result.Value;
            for (int i = 0; i < page.Values.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = page.Values[i];

                if (string.Equals(entry.Status, "removed", StringComparison.Ordinal))
                    continue;

                var filePath = entry.New?.Path;
                if (filePath is null)
                    continue;

                yield return Result<FileHandle, RagError>.Success(
                    ToHandle(filePath, etag: null, MapChangeStatus(entry.Status)));
            }

            pageToken = ExtractPageToken(page.Next);
        }
        while (pageToken is not null);
    }

    /// <summary>
    /// Builds the handle for one file. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>path</c>, <c>repo</c> (<c>workspace/slug</c>) and <c>ref</c> on every
    /// run, plus <c>change_status</c> on diffstat (delta) runs only — a source listing has no
    /// notion of change.
    /// <para>
    /// <b>Phase 4.10 Task 5 — investigated, left unset.</b> <see cref="BitbucketSourceCommit"/>
    /// maps only <c>hash</c> because that is what the src-listing and diffstat endpoints were
    /// modelled from, and Bitbucket Cloud's documented shape for the <c>commit</c> object
    /// embedded on each file entry there is a minimal reference (type/hash/links) distinct from
    /// the full commit resource — which does carry <c>date</c> — returned by the dedicated
    /// <c>/repositories/{workspace}/{repo}/commit/{hash}</c> endpoint. Available documentation
    /// could not be retrieved in enough detail to confirm the embedded object's exact fields
    /// without live API access, so no date is read here rather than guessed. Even if confirmed
    /// absent, obtaining it would mean a second API call per file (fetching the full commit
    /// resource by hash) — widening the request, which Task 5 explicitly excludes. Both
    /// <see cref="FileHandle.CreatedAt"/> and <see cref="FileHandle.UpdatedAt"/> are therefore
    /// left unset: a truthful "unknown", not an oversight.
    /// </para>
    /// </remarks>
    private FileHandle ToHandle(string path, string? etag, string? changeStatus)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["path"] = path,
            ["repo"] = _repoSlug,
            ["ref"]  = _options.Ref,
        };
        if (changeStatus is not null)
            metadata["change_status"] = changeStatus;

        return new FileHandle(
            Id:               path,
            FileName:         Path.GetFileName(path),
            ETag:             etag,
            OpenContentAsync: ct => GetRawFileStreamAsync(
                _options.Workspace, _options.RepoSlug, _options.Ref, path, ct),
            Metadata:         metadata);
    }

    /// <summary>
    /// Normalises Bitbucket's diffstat <c>status</c> onto the cross-connector
    /// <c>change_status</c> vocabulary (<c>added</c>/<c>modified</c>/<c>removed</c>/<c>renamed</c>)
    /// so the tag can be filtered on identically across GitHub, GitLab and Box.
    /// <list type="bullet">
    /// <item><c>added</c>, <c>modified</c> and <c>renamed</c> already match the vocabulary and
    /// pass through unchanged — they are re-stated here rather than forwarded blind, so a new
    /// vendor value cannot silently leak into the tag.</item>
    /// <item><c>removed</c> → <c>removed</c>. Unreachable in practice: removed entries are
    /// skipped before a handle is built. Mapped anyway so the vocabulary is complete.</item>
    /// <item>Anything else → <see langword="null"/>, and the key is omitted.</item>
    /// </list>
    /// </summary>
    private static string? MapChangeStatus(string? status) => status switch
    {
        "added"    => "added",
        "modified" => "modified",
        "renamed"  => "renamed",
        "removed"  => "removed",
        _          => null,
    };

    private async Task<Stream> GetRawFileStreamAsync(
        string workspace, string repo, string @ref, string path, CancellationToken ct)
    {
        var url = $"2.0/repositories/{workspace}/{repo}/src/{@ref}/{path}";
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the <c>page</c> query-string value from a Bitbucket <c>next</c> URL.
    /// </summary>
    private static string? ExtractPageToken(string? next)
    {
        if (next is null) return null;
        var idx = next.IndexOf("page=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + 5;
        var end = next.IndexOf('&', start);
        return end < 0 ? next[start..] : next[start..end];
    }
}
