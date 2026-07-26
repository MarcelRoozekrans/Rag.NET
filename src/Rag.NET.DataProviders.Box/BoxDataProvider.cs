using System.Runtime.CompilerServices;
using Box.V2;
using Box.V2.Models;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

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

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetFullHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
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

                    yield return Result<FileHandle, RagError>.Success(ToHandle(
                        item.Id, item.Name, (item as BoxFile)?.Sha1,
                        folderId: folderId, changeStatus: null));
                }

                offset += items.Entries.Count;
                if (offset >= items.TotalCount) break;
            }
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
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

                    yield return Result<FileHandle, RagError>.Success(ToHandle(
                        file.Id, file.Name, file.Sha1,
                        folderId: null, MapChangeStatus(ev.EventType)));
                }
            }

            if (events.ChunkSize < limit) break; // no more events
            streamPosition = events.NextStreamPosition ?? streamPosition;
        }
    }

    /// <summary>
    /// Builds the handle for one file. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>folder_id</c> on the full run only (the traversal stack knows the
    /// containing folder; the Events feed does not, and the <c>fields</c> selection deliberately
    /// does not request <c>path_collection</c>), and <c>change_status</c> on the delta run only.
    /// <para><b>Internal for testing.</b> <c>BoxClient</c> is a sealed-in-practice concrete type
    /// with no injectable transport, so the enumeration paths cannot be driven from a unit test;
    /// this is the seam that pins the emitted keys.</para>
    /// </remarks>
    internal FileHandle ToHandle(
        string id, string name, string? sha1, string? folderId, string? changeStatus)
    {
        var capturedId = id;

        Dictionary<string, string>? metadata = null;
        if (folderId is not null || changeStatus is not null)
        {
            metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (folderId is not null)     metadata["folder_id"]     = folderId;
            if (changeStatus is not null) metadata["change_status"] = changeStatus;
        }

        return new FileHandle(
            Id:               id,
            FileName:         name,
            ETag:             sha1,
            OpenContentAsync: async ct =>
            {
                ct.ThrowIfCancellationRequested();
                return await _client.FilesManager.DownloadAsync(capturedId, null)
                    .ConfigureAwait(false);
            },
            Metadata:         metadata);
    }

    /// <summary>
    /// Normalises Box's Events <c>event_type</c> onto the cross-connector <c>change_status</c>
    /// vocabulary (<c>added</c>/<c>modified</c>/<c>removed</c>/<c>renamed</c>) so the tag can be
    /// filtered on identically across GitHub, GitLab and Bitbucket.
    /// <list type="bullet">
    /// <item><c>COPY</c> → <c>added</c>: a copy always produces a new item at the destination,
    /// which is the item this handle carries.</item>
    /// <item><c>UPLOAD</c> → <c>modified</c>. <b>This mapping is lossy.</b> Box raises one
    /// <c>UPLOAD</c> event for a brand-new file and for a new version of an existing file, and
    /// the event payload carries nothing that distinguishes them. <c>modified</c> is the weaker
    /// of the two claims and is therefore never actively wrong about a version upload; calling a
    /// version upload <c>added</c> would be. Fixing this properly needs the <c>path_collection</c>
    /// / <c>version_number</c> fields, which the <c>fields</c> selection does not request
    /// (recorded as debt in the Phase 2.2 design).</item>
    /// <item>Anything else → <see langword="null"/>, and the key is omitted. Unreachable today:
    /// the delta loop only lets <c>UPLOAD</c> and <c>COPY</c> through.</item>
    /// </list>
    /// <para><b>Internal for testing</b>, for the same reason as <see cref="ToHandle"/>.</para>
    /// </summary>
    internal static string? MapChangeStatus(string? eventType) => eventType switch
    {
        "UPLOAD" => "modified",
        "COPY"   => "added",
        _        => null,
    };
}
