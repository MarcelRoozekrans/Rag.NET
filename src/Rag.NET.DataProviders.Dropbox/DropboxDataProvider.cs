using System.Runtime.CompilerServices;
using Dropbox.Api;
using Dropbox.Api.Files;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Dropbox;

/// <summary>
/// Enumerates files from Dropbox.
/// Full run: ListFolder recursive. Delta run: ListFolderContinue using stored cursor.
/// Dropbox cursors do not expire — no stale-cursor fallback needed.
/// </summary>
public sealed class DropboxDataProvider : FileContentProviderBase
{
    private readonly ITokenProvider _tokenProvider;
    private readonly DropboxOptions _options;

    public DropboxDataProvider(ITokenProvider tokenProvider, DropboxOptions? options = null)
        : base(options ??= new DropboxOptions())
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _options = options;
    }

    private async Task<DropboxClient> CreateClientAsync(CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct).ConfigureAwait(false);
        return new DropboxClient(token);
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.Files.ListFolderAsync(
            new ListFolderArg(_options.FolderPath, recursive: true))
            .ConfigureAwait(false);

        while (true)
        {
            foreach (var entry in result.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not FileMetadata file) continue;

                var capturedPath = file.PathLower!;
                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id:               file.PathDisplay ?? capturedPath,
                    FileName:         file.Name,
                    ETag:             file.ContentHash,
                    OpenContentAsync: async ct =>
                    {
                        using var c = await CreateClientAsync(ct).ConfigureAwait(false);
                        var dl = await c.Files.DownloadAsync(capturedPath).ConfigureAwait(false);
                        var ms = new MemoryStream();
                        using var content = await dl.GetContentAsStreamAsync().ConfigureAwait(false);
                        await content.CopyToAsync(ms, ct).ConfigureAwait(false);
                        ms.Seek(0, SeekOrigin.Begin);
                        return (Stream)ms;
                    }));
            }

            if (!result.HasMore) break;
            result = await client.Files.ListFolderContinueAsync(result.Cursor).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.Files.ListFolderContinueAsync(_options.DeltaToken!)
            .ConfigureAwait(false);

        while (true)
        {
            foreach (var entry in result.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not FileMetadata file) continue;

                var capturedPath = file.PathLower!;
                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id:               file.PathDisplay ?? capturedPath,
                    FileName:         file.Name,
                    ETag:             file.ContentHash,
                    OpenContentAsync: async ct =>
                    {
                        using var c = await CreateClientAsync(ct).ConfigureAwait(false);
                        var dl = await c.Files.DownloadAsync(capturedPath).ConfigureAwait(false);
                        var ms = new MemoryStream();
                        using var content = await dl.GetContentAsStreamAsync().ConfigureAwait(false);
                        await content.CopyToAsync(ms, ct).ConfigureAwait(false);
                        ms.Seek(0, SeekOrigin.Begin);
                        return (Stream)ms;
                    }));
            }

            if (!result.HasMore) break;
            result = await client.Files.ListFolderContinueAsync(result.Cursor).ConfigureAwait(false);
        }
    }
}
