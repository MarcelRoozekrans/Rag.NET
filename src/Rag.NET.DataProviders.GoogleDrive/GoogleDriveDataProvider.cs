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
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = _drive.Files.List();
            request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum, parents)";
            request.PageSize = 100;
            request.Q = _options.FolderId is not null
                ? $"'{_options.FolderId}' in parents and trashed = false"
                : "trashed = false";
            if (pageToken is not null)
                request.PageToken = pageToken;

            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            foreach (var file in page.Files ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(file.MimeType, "application/vnd.google-apps.folder",
                    StringComparison.Ordinal)) continue;

                var capturedId = file.Id;
                yield return new FileHandle(
                    Id:               file.Id,
                    FileName:         file.Name,
                    ETag:             file.Md5Checksum,
                    OpenContentAsync: async ct =>
                    {
                        var ms = new MemoryStream();
                        await _drive.Files.Get(capturedId).DownloadAsync(ms, ct).ConfigureAwait(false);
                        ms.Seek(0, SeekOrigin.Begin);
                        return (Stream)ms;
                    });
            }

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = _options.DeltaToken;
        bool hasMore = true;

        while (hasMore)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = _drive.Changes.List(pageToken!);
            request.Fields = "nextPageToken, newStartPageToken, changes(file(id, name, mimeType, md5Checksum), removed)";
            request.PageSize = 100;

            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            foreach (var change in page.Changes ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Removed == true || change.File is null) continue;
                if (string.Equals(change.File.MimeType, "application/vnd.google-apps.folder",
                    StringComparison.Ordinal)) continue;

                var capturedId = change.File.Id;
                yield return new FileHandle(
                    Id:               change.File.Id,
                    FileName:         change.File.Name,
                    ETag:             change.File.Md5Checksum,
                    OpenContentAsync: async ct =>
                    {
                        var ms = new MemoryStream();
                        await _drive.Files.Get(capturedId).DownloadAsync(ms, ct).ConfigureAwait(false);
                        ms.Seek(0, SeekOrigin.Begin);
                        return (Stream)ms;
                    });
            }

            if (page.NextPageToken is not null)
                pageToken = page.NextPageToken;
            else
                hasMore = false;
        }
    }
}
