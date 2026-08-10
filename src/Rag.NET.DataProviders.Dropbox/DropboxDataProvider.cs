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

                yield return Result<FileHandle, RagError>.Success(ToHandle(file));
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

                yield return Result<FileHandle, RagError>.Success(ToHandle(file));
            }

            if (!result.HasMore) break;
            result = await client.Files.ListFolderContinueAsync(result.Cursor).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the handle for one file. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1). Shared by the full and delta paths — Dropbox's
    /// delta feed reports the same <see cref="FileMetadata"/> shape, so both emit the same keys.
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>path</c> (the display path, falling back to the lowercased path when
    /// Dropbox omits it) and <c>folder</c> — the configured root. <see cref="DropboxOptions.FolderPath"/>
    /// defaults to the empty string, which means "the whole Dropbox"; that is not a folder name,
    /// so the key is omitted rather than written empty.
    /// <para><b>Internal for testing.</b> <c>DropboxClient</c> is constructed from a token inside
    /// this provider and exposes no injectable transport, so the enumeration paths cannot be
    /// driven from a unit test; this is the seam that pins the emitted keys.</para>
    /// <para>
    /// <see cref="FileHandle.UpdatedAt"/> is set from <see cref="FileMetadata.ServerModified"/> —
    /// the Dropbox SDK types it as a non-nullable <see cref="DateTime"/>, so no parsing is needed.
    /// Dropbox has no creation-time field on <see cref="FileMetadata"/>, so
    /// <see cref="FileHandle.CreatedAt"/> is left unset.
    /// </para>
    /// </remarks>
    internal FileHandle ToHandle(FileMetadata file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var capturedPath = file.PathLower!;
        var path = file.PathDisplay ?? capturedPath;

        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["path"] = path,
        };
        if (!string.IsNullOrEmpty(_options.FolderPath))
            metadata["folder"] = _options.FolderPath;

        return new FileHandle(
            Id:               path,
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
            },
            Metadata:         metadata,
            UpdatedAt:        file.ServerModified);
    }
}
