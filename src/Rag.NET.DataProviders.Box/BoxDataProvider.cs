using System.Globalization;
using System.Runtime.CompilerServices;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using Box.Sdk.Gen.Schemas;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;
using BoxFile = Box.Sdk.Gen.Schemas.File;

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
            const long limit = 100;

            while (true)
            {
                var items = await _client.Folders.GetFolderItemsAsync(
                    folderId,
                    new GetFolderItemsQueryParams
                    {
                        Fields = ["id", "name", "type", "sha1", "created_at", "modified_at"],
                        Offset = offset,
                        Limit = limit,
                    }).ConfigureAwait(false);

                var entries = items.Entries ?? [];
                foreach (var handle in FullPageHandles(entries, folderId, stack))
                    yield return handle;

                offset += entries.Count;
                if (items.TotalCount is null || offset >= items.TotalCount) break;
            }
        }
    }

    /// <summary>
    /// Splits one page of folder items into stack pushes (subfolders) and yieldable handles
    /// (files and web links), synchronously — kept out of the async iterator body so the
    /// enumeration loop above stays within MA0051's line budget.
    /// </summary>
    private IEnumerable<Result<FileHandle, RagError>> FullPageHandles(
        IReadOnlyList<Item> entries, string folderId, Stack<string> stack)
    {
        foreach (var item in entries)
        {
            if (item.FolderMini is { } folder)
            {
                stack.Push(folder.Id);
                continue;
            }

            // Item is a closed union of FileFull/FolderMini/WebLink. A web link has no
            // downloadable content and no sha1, but is still surfaced the way the pre-migration
            // connector surfaced it — id/name only, source left null so timestamps stay unset.
            var id = item.FileFull?.Id ?? item.WebLink?.Id;
            var name = item.FileFull?.Name ?? item.WebLink?.Name;
            if (id is null || name is null) continue;

            yield return Result<FileHandle, RagError>.Success(ToHandle(
                id, name, item.FileFull?.Sha1,
                folderId: folderId, changeStatus: null, source: item.FileFull));
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamPosition = _options.DeltaToken!;
        const long limit = 100;

        while (true)
        {
            var events = await _client.Events.GetEventsAsync(new GetEventsQueryParams
            {
                StreamPosition = streamPosition,
                Limit = limit,
            }).ConfigureAwait(false);

            if (events.Entries is not null)
            {
                foreach (var handle in DeltaPageHandles(events.Entries, cancellationToken))
                    yield return handle;
            }

            if ((events.ChunkSize ?? 0) < limit) break; // no more events
            streamPosition = events.NextStreamPosition?.StringVal
                ?? events.NextStreamPosition?.LongVal?.ToString(CultureInfo.InvariantCulture)
                ?? streamPosition;
        }
    }

    /// <summary>
    /// Filters and maps one page of events, synchronously — same MA0051 reasoning as
    /// <see cref="FullPageHandles"/>.
    /// </summary>
    private IEnumerable<Result<FileHandle, RagError>> DeltaPageHandles(
        IReadOnlyList<Event> entries, CancellationToken cancellationToken)
    {
        foreach (var ev in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ev.Source?.File is not { Name: { } fileName } file) continue;
            var eventType = ev.EventType?.StringValue;
            if (!string.Equals(eventType, "UPLOAD", StringComparison.Ordinal)
             && !string.Equals(eventType, "COPY", StringComparison.Ordinal)) continue;

            yield return Result<FileHandle, RagError>.Success(ToHandle(
                file.Id, fileName, file.Sha1,
                folderId: null, MapChangeStatus(eventType), source: file));
        }
    }

    /// <summary>
    /// Builds the handle for one file. Synchronous by design: the metadata dictionary is never
    /// built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>folder_id</c> on the full run only, and <c>change_status</c> on the
    /// delta run only. <paramref name="source"/>'s <c>CreatedAt</c>/<c>ModifiedAt</c> are
    /// forwarded onto the typed <see cref="FileHandle.CreatedAt"/>/<see cref="FileHandle.UpdatedAt"/>
    /// channel on both runs, converted from the SDK's <c>DateTimeOffset?</c> via
    /// <c>UtcDateTime</c> to match <see cref="FileHandle"/>'s <c>DateTime?</c> channel.
    /// <paramref name="source"/> is taken as the reference-typed Box SDK <c>File</c> itself,
    /// rather than two separate <c>DateTimeOffset?</c> scalar parameters, because passing
    /// <c>Nullable&lt;DateTimeOffset&gt;</c> by value here trips ErrorProne.NET's EPS05 (pass
    /// large readonly structs by <c>in</c>) while adding <c>in</c> then trips Roslynator's
    /// RCS1242 (don't pass non-readonly structs by readonly reference) — the two analyzers
    /// disagree, and a reference-typed parameter sidesteps the conflict entirely. <c>File</c> is
    /// the type both call sites can supply: the full run's <c>FileFull</c> derives from it, and
    /// the delta run's <c>EventSourceResource.File</c> already returns it.
    /// <para>
    /// The delta run <i>could</i> supply <c>folder_id</c>: <c>File.Parent</c> is present on the
    /// events source object, and the events call passes no field selection at all, so nothing
    /// restricts it. This code simply does not read it today. (The
    /// <c>fields: ["id","name","type","sha1","created_at","modified_at"]</c> selection is on the
    /// <i>full</i> traversal's <c>GetFolderItemsAsync</c> call and has no bearing on the delta
    /// path — the events call already returns <c>created_at</c>/<c>modified_at</c> unrestricted.)
    /// </para>
    /// <para><b>Internal for testing.</b> Pinned directly so the emitted metadata keys stay
    /// exact, alongside the <see cref="GetFilesAsync"/>-level enumeration tests that now drive
    /// this through a fake <c>INetworkClient</c> (see the test project).</para>
    /// </remarks>
    internal FileHandle ToHandle(
        string id, string name, string? sha1, string? folderId, string? changeStatus,
        BoxFile? source = null)
    {
        Dictionary<string, MetadataValue>? metadata = null;
        if (folderId is not null || changeStatus is not null)
        {
            metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
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
                return await _client.Downloads.DownloadFileAsync(id).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Box returned no content stream for '{id}'.");
            },
            Metadata:         metadata,
            CreatedAt:        source?.CreatedAt?.UtcDateTime,
            UpdatedAt:        source?.ModifiedAt?.UtcDateTime);
    }

    /// <summary>
    /// Normalises Box's Events <c>event_type</c> onto the cross-connector <c>change_status</c>
    /// vocabulary (<c>added</c>/<c>modified</c>/<c>removed</c>/<c>renamed</c>) so the tag can be
    /// filtered on identically across GitHub, GitLab and Bitbucket.
    /// <list type="bullet">
    /// <item><c>COPY</c> → <c>added</c>: a copy always produces a new item at the destination,
    /// which is the item this handle carries.</item>
    /// <item><c>UPLOAD</c> → <see langword="null"/>, so the key is <b>omitted</b>. Box raises one
    /// <c>UPLOAD</c> event both for a brand-new file and for a new version of an existing file.
    /// In this vocabulary <c>added</c> and <c>modified</c> are disjoint, so guessing either one
    /// is outright false half the time — there is no "weaker, still-true" option. The design
    /// prescribes omitting a field the connector cannot determine, and a sparse-but-true tag is
    /// worth more than a dense half-false one.
    /// <para>
    /// This <i>is</i> resolvable: <c>File</c> carries <c>SequenceId</c>, <c>ev.Source.File</c> is
    /// already a <c>File</c>, and the events call passes no field selection that would strip it.
    /// It is left undone only because whether the API populates it on the user-events payload
    /// could not be confirmed against a real response — the SDK declaring a property is not
    /// evidence the wire carries it. Confirm that and <c>SequenceId == "0"</c> → <c>added</c>,
    /// otherwise <c>modified</c>, is the fix.
    /// </para></item>
    /// <item>Anything else → <see langword="null"/>, and the key is omitted. Unreachable today:
    /// the delta loop only lets <c>UPLOAD</c> and <c>COPY</c> through.</item>
    /// </list>
    /// <para><b>Internal for testing</b>, for the same reason as <see cref="ToHandle"/>. This
    /// still takes the raw wire string (<c>"UPLOAD"</c>/<c>"COPY"</c>) rather than the SDK's
    /// <c>StringEnum&lt;EventEventTypeField&gt;</c> — <c>StringEnum.StringValue</c> already
    /// carries that exact string, so callers unwrap once at the call site and this stays a pure
    /// string map, testable without constructing SDK model types.</para>
    /// </summary>
    internal static string? MapChangeStatus(string? eventType) => eventType switch
    {
        "COPY" => "added",
        _      => null,
    };
}
