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

    internal BitbucketDataProvider(IBitbucketApi api, HttpClient http, BitbucketOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(http);
        _api = api;
        _http = http;
        _options = options;
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

                var capturedPath = entry.Path;
                var etag = entry.Commit?.Hash;

                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id: capturedPath,
                    FileName: Path.GetFileName(capturedPath),
                    ETag: etag,
                    OpenContentAsync: ct => GetRawFileStreamAsync(
                        _options.Workspace, _options.RepoSlug, _options.Ref, capturedPath, ct)));
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

                var capturedPath = filePath;

                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id: capturedPath,
                    FileName: Path.GetFileName(capturedPath),
                    ETag: null,
                    OpenContentAsync: ct => GetRawFileStreamAsync(
                        _options.Workspace, _options.RepoSlug, _options.Ref, capturedPath, ct)));
            }

            pageToken = ExtractPageToken(page.Next);
        }
        while (pageToken is not null);
    }

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
