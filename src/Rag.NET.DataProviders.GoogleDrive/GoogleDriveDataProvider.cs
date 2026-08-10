using System.Runtime.CompilerServices;
using Google.Apis.Drive.v3;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.GoogleDrive;

/// <summary>
/// Enumerates files from Google Drive.
/// Full run: files in folder (or whole drive). Delta run: Changes.List API with pageToken.
/// </summary>
public sealed class GoogleDriveDataProvider : FileContentProviderBase
{
    private readonly DriveService _drive;
    private readonly GoogleDriveOptions _options;

    public GoogleDriveDataProvider(DriveService drive, GoogleDriveOptions? options = null)
        : base(options ??= new GoogleDriveOptions())
    {
        ArgumentNullException.ThrowIfNull(drive);
        _drive = drive;
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
        if (_options.FolderId is null)
        {
            await foreach (var handle in GetWholeDriveHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return handle;
        }
        else
        {
            await foreach (var handle in GetFolderHandlesAsync(_options.FolderId, cancellationToken).ConfigureAwait(false))
                yield return handle;
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetWholeDriveHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum, createdTime, modifiedTime)";
            request.PageSize = 100;
            request.Q = "mimeType != 'application/vnd.google-apps.folder' and trashed = false";
            if (pageToken is not null) request.PageToken = pageToken;

            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in page.Files ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)) continue;
                // No folder_id: a whole-drive listing does not know which folder a file sits in.
                yield return Result<FileHandle, RagError>.Success(
                    BuildHandle(file.Id, file.Name, file.Md5Checksum, file.MimeType, folderId: null, source: file));
            }
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFolderHandlesAsync(
        string rootFolderId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var folderQueue = new Queue<string>();
        folderQueue.Enqueue(rootFolderId);

        while (folderQueue.Count > 0)
        {
            var folderId = folderQueue.Dequeue();
            string? pageToken = null;
            do
            {
                var request = _drive.Files.List();
                request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum, createdTime, modifiedTime)";
                request.PageSize = 100;
                request.Q = $"'{folderId}' in parents and trashed = false";
                if (pageToken is not null) request.PageToken = pageToken;

                var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                foreach (var file in page.Files ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
                    {
                        folderQueue.Enqueue(file.Id);
                        continue;
                    }
                    // The traversal knows the containing folder here, so folder_id is emitted.
                    yield return Result<FileHandle, RagError>.Success(
                        BuildHandle(file.Id, file.Name, file.Md5Checksum, file.MimeType, folderId, source: file));
                }
                pageToken = page.NextPageToken;
            }
            while (pageToken is not null);
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var firstPage = await TryFetchFirstDeltaPageAsync(cancellationToken).ConfigureAwait(false);
        if (firstPage is null)
        {
            // Stale page token — fall back to full traversal
            await foreach (var handle in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return handle;
            yield break;
        }

        var page = firstPage;
        while (page is not null)
        {
            foreach (var change in page.Changes ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Removed == true || change.File is null) continue;
                if (string.Equals(change.File.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)) continue;
                // No folder_id: the Changes feed reports the file, not the folder it lives in.
                yield return Result<FileHandle, RagError>.Success(BuildHandle(
                    change.File.Id, change.File.Name, change.File.Md5Checksum,
                    change.File.MimeType, folderId: null, source: change.File));
            }

            if (page.NextPageToken is null) break;
            page = await FetchNextDeltaPageAsync(page.NextPageToken, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Google.Apis.Drive.v3.Data.ChangeList?> TryFetchFirstDeltaPageAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var request = _drive.Changes.List(_options.DeltaToken!);
            request.Fields = "nextPageToken, newStartPageToken, changes(file(id, name, mimeType, md5Checksum, createdTime, modifiedTime), removed)";
            request.PageSize = 100;
            return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    private async Task<Google.Apis.Drive.v3.Data.ChangeList> FetchNextDeltaPageAsync(
        string pageToken,
        CancellationToken cancellationToken)
    {
        var request = _drive.Changes.List(pageToken);
        request.Fields = "nextPageToken, newStartPageToken, changes(file(id, name, mimeType, md5Checksum, createdTime, modifiedTime), removed)";
        request.PageSize = 100;
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the handle for one file. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>mime_type</c>, which the field selection already fetches at every
    /// call site (to skip folders) and then discarded, and <c>folder_id</c> — only the folder
    /// traversal knows the containing folder, so the whole-drive and Changes paths pass
    /// <see langword="null"/> and the key is omitted rather than written empty.
    /// <para>
    /// <paramref name="source"/>'s <c>createdTime</c>/<c>modifiedTime</c> (all four field masks
    /// now request them) become the typed <see cref="FileHandle.CreatedAt"/>/
    /// <see cref="FileHandle.UpdatedAt"/> channel. <paramref name="source"/> is the reference-typed
    /// <c>Google.Apis.Drive.v3.Data.File</c> itself, read via <c>CreatedTimeDateTimeOffset</c>/
    /// <c>ModifiedTimeDateTimeOffset</c> — the plain <c>CreatedTime</c>/<c>ModifiedTime</c>
    /// properties are <see cref="ObsoleteAttribute">obsolete</see> in this SDK version, and this
    /// repository's warnings-as-errors build turns that into a hard failure.
    /// </para>
    /// </remarks>
    private FileHandle BuildHandle(
        string id, string name, string? etag, string? mimeType, string? folderId,
        Google.Apis.Drive.v3.Data.File? source = null)
    {
        var capturedId = id;

        Dictionary<string, MetadataValue>? metadata = null;
        if (!string.IsNullOrEmpty(mimeType) || !string.IsNullOrEmpty(folderId))
        {
            metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(mimeType)) metadata["mime_type"] = mimeType;
            if (!string.IsNullOrEmpty(folderId)) metadata["folder_id"] = folderId;
        }

        return new FileHandle(
            Id:       id,
            FileName: name,
            ETag:     etag,
            OpenContentAsync: async ct =>
            {
                var ms = new MemoryStream();
                try
                {
                    await _drive.Files.Get(capturedId).DownloadAsync(ms, ct).ConfigureAwait(false);
                    ms.Seek(0, SeekOrigin.Begin);
                    return (Stream)ms;
                }
                catch
                {
                    await ms.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            Metadata:  metadata,
            CreatedAt: source?.CreatedTimeDateTimeOffset?.UtcDateTime,
            UpdatedAt: source?.ModifiedTimeDateTimeOffset?.UtcDateTime);
    }
}
