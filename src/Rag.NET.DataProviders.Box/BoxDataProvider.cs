using System.Runtime.CompilerServices;
using Box.V2;
using Box.V2.Models;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Box;

/// <summary>
/// Enumerates files from Box.
/// Full run: recursive folder traversal. Delta run: Box Events stream cursor.
/// Box stream positions do not expire.
/// </summary>
public sealed class BoxDataProvider : FileContentProviderBase
{
    private readonly BoxClient _client;
    private readonly BoxOptions _options;

    public BoxDataProvider(BoxClient client, BoxOptions? options = null)
        : base(options ??= new BoxOptions())
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
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
        var stack = new Stack<string>();
        stack.Push(_options.RootFolderId);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderId = stack.Pop();
            long offset = 0;
            const int limit = 100;

            while (true)
            {
                var items = await _client.FoldersManager.GetFolderItemsAsync(
                    folderId, limit, (int)offset, fields: ["id", "name", "type", "sha1"])
                    .ConfigureAwait(false);

                for (int i = 0; i < items.Entries.Count; i++)
                {
                    var item = items.Entries[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(item.Type, "folder", StringComparison.Ordinal))
                    {
                        stack.Push(item.Id);
                        continue;
                    }

                    var capturedId = item.Id;
                    var sha1 = (item as BoxFile)?.Sha1;
                    yield return new FileHandle(
                        Id:               item.Id,
                        FileName:         item.Name,
                        ETag:             sha1,
                        OpenContentAsync: async ct =>
                        {
                            ct.ThrowIfCancellationRequested();
                            return await _client.FilesManager.DownloadAsync(capturedId, null)
                                .ConfigureAwait(false);
                        });
                }

                offset += items.Entries.Count;
                if (offset >= items.TotalCount) break;
            }
        }
    }

    private async IAsyncEnumerable<FileHandle> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamPosition = _options.DeltaToken!;
        const int limit = 100;

        while (true)
        {
            var events = await _client.EventsManager.UserEventsAsync(
                limit: limit, streamPosition: streamPosition)
                .ConfigureAwait(false);

            if (events.Entries is not null)
            {
                for (int i = 0; i < events.Entries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ev = events.Entries[i];
                    if (ev.Source is not BoxFile file) continue;
                    if (!string.Equals(ev.EventType, "UPLOAD", StringComparison.Ordinal)
                     && !string.Equals(ev.EventType, "COPY",   StringComparison.Ordinal)) continue;

                    var capturedId = file.Id;
                    yield return new FileHandle(
                        Id:               file.Id,
                        FileName:         file.Name,
                        ETag:             file.Sha1,
                        OpenContentAsync: async ct =>
                        {
                            ct.ThrowIfCancellationRequested();
                            return await _client.FilesManager.DownloadAsync(capturedId, null)
                                .ConfigureAwait(false);
                        });
                }
            }

            if (events.ChunkSize < limit) break; // no more events
            streamPosition = events.NextStreamPosition ?? streamPosition;
        }
    }
}
