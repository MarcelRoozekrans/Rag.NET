using System.Runtime.CompilerServices;
using Google.Apis.Drive.v3;
using Rag.NET.DataProviders;

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

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
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

    private async IAsyncEnumerable<FileHandle> GetWholeDriveHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum)";
            request.PageSize = 100;
            request.Q = "mimeType != 'application/vnd.google-apps.folder' and trashed = false";
            if (pageToken is not null) request.PageToken = pageToken;

            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in page.Files ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)) continue;
                yield return BuildHandle(file.Id, file.Name, file.Md5Checksum);
            }
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);
    }

    private async IAsyncEnumerable<FileHandle> GetFolderHandlesAsync(
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
                request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum)";
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
                    yield return BuildHandle(file.Id, file.Name, file.Md5Checksum);
                }
                pageToken = page.NextPageToken;
            }
            while (pageToken is not null);
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
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
                yield return BuildHandle(change.File.Id, change.File.Name, change.File.Md5Checksum);
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
            request.Fields = "nextPageToken, newStartPageToken, changes(file(id, name, mimeType, md5Checksum), removed)";
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
        request.Fields = "nextPageToken, newStartPageToken, changes(file(id, name, mimeType, md5Checksum), removed)";
        request.PageSize = 100;
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private FileHandle BuildHandle(string id, string name, string? etag)
    {
        var capturedId = id;
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
            });
    }
}
