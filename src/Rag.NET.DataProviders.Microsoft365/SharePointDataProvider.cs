using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Graph;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.SharePoint;

/// <summary>
/// Enumerates files from a SharePoint drive via Microsoft Graph.
/// Full run: recursive drive enumeration. Delta run: Graph delta API using stored deltaLink token.
/// Stale/expired delta token: automatically falls back to full traversal.
/// <para>
/// Graph failures reach the caller through the <see cref="Result{TValue,TError}"/> channel
/// rather than as thrown exceptions: a response carrying a status becomes
/// <see cref="RagError.HttpFailed"/>, and a failure with no response at all — DNS, TLS, socket
/// reset, client-side timeout, token acquisition — becomes
/// <see cref="RagError.TransportFailed"/>. Caller cancellation always propagates.
/// </para>
/// </summary>
public sealed class SharePointDataProvider : FileContentProviderBase
{
    private readonly GraphServiceClient _graph;
    private readonly SharePointOptions _options;

    public SharePointDataProvider(GraphServiceClient graph, SharePointOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
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
        string? nextLink = null;
        do
        {
            var pageResult = await FetchChildrenPageAsync(nextLink, cancellationToken)
                .ConfigureAwait(false);
            if (pageResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(pageResult.Error);
                yield break;
            }

            var page = pageResult.Value;
            if (page is null)
                yield break;

#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross yield/await boundaries in async iterators
            foreach (var item in page.Value ?? [])
#pragma warning restore HLQ012
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is null) continue;

                yield return Result<FileHandle, RagError>.Success(ToHandle(item));
            }

            nextLink = page.OdataNextLink;
        } while (nextLink is not null);
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // C# does not permit yield inside a catch clause. Every Graph call is therefore made
        // eagerly by a helper that returns a Result, and this iterator only yields.
        var firstResult = await TryFetchFirstDeltaPageAsync(cancellationToken).ConfigureAwait(false);
        if (firstResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(firstResult.Error);
            yield break;
        }

        if (firstResult.Value is null)
        {
            // Token was stale / not found — fall back to full traversal.
            await foreach (var handle in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return handle;
            yield break;
        }

        var page = firstResult.Value;
        while (page is not null)
        {
#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross yield/await boundaries in async iterators
            foreach (var item in page.Value ?? [])
#pragma warning restore HLQ012
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is null || item.Deleted is not null) continue;

                yield return Result<FileHandle, RagError>.Success(ToHandle(item));
            }

            if (page.OdataNextLink is null)
                yield break;

            var nextResult = await FetchDeltaPageAsync(page.OdataNextLink, cancellationToken)
                .ConfigureAwait(false);
            if (nextResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(nextResult.Error);
                yield break;
            }

            page = nextResult.Value;
        }
    }

    /// <summary>
    /// Builds the handle for one drive item. Synchronous by design: the metadata dictionary is
    /// never built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>drive_id</c> (always, from options) and <c>parent_path</c> — the
    /// parent folder path Graph reports on <c>parentReference</c>. Graph omits
    /// <c>parentReference</c> on some delta payloads, so <c>parent_path</c> is optional and is
    /// left out entirely rather than written empty.
    /// <para>
    /// The key is <c>parent_path</c>, <b>not</b> <c>path</c>: everywhere else in the file/blob
    /// connectors <c>path</c> is the file's own full path, but Graph gives the containing folder
    /// (and prefixes it with the <c>/drive/root:</c> namespace token). Filing that under
    /// <c>path</c> would make a cross-connector <c>path</c> filter silently match nothing here.
    /// </para>
    /// <para>
    /// Phase 4.10 Task 5: <c>item.CreatedDateTime</c>/<c>LastModifiedDateTime</c> — both typed
    /// as <see cref="DateTimeOffset"/><c>?</c> by the Graph SDK — become
    /// <see cref="FileHandle.CreatedAt"/>/<see cref="FileHandle.UpdatedAt"/>.
    /// </para>
    /// </remarks>
    private FileHandle ToHandle(DriveItem item)
    {
        var capturedId = item.Id!;
        var parentPath = item.ParentReference?.Path;

        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["drive_id"] = _options.DriveId,
        };
        if (!string.IsNullOrEmpty(parentPath))
            metadata["parent_path"] = parentPath;

        return new FileHandle(
            Id:               (parentPath ?? string.Empty) + "/" + item.Name,
            FileName:         item.Name ?? capturedId,
            ETag:             item.ETag,
            OpenContentAsync: async ct =>
                await _graph.Drives[_options.DriveId].Items[capturedId].Content
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                    ?? Stream.Null,
            Metadata:         metadata,
            CreatedAt:        item.CreatedDateTime?.UtcDateTime,
            UpdatedAt:        item.LastModifiedDateTime?.UtcDateTime);
    }

    private async Task<Result<DriveItemCollectionResponse?, RagError>> FetchChildrenPageAsync(
        string? nextLink, CancellationToken cancellationToken)
    {
        var builder = _graph.Drives[_options.DriveId].Items["root"].Children;
        try
        {
            var page = nextLink is not null
                ? await builder.WithUrl(nextLink).GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : await builder.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<DriveItemCollectionResponse?, RagError>.Success(page);
        }
        catch (Exception ex) when (GraphErrorMapping.IsMappable(ex, cancellationToken))
        {
            return Result<DriveItemCollectionResponse?, RagError>.Failure(GraphErrorMapping.Map(ex));
        }
    }

    private async Task<Result<DeltaGetResponse?, RagError>> FetchDeltaPageAsync(
        string url, CancellationToken cancellationToken)
    {
        try
        {
            var page = await _graph.Drives[_options.DriveId].Items["root"].Delta
                .WithUrl(url).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Result<DeltaGetResponse?, RagError>.Success(page);
        }
        catch (Exception ex) when (GraphErrorMapping.IsMappable(ex, cancellationToken))
        {
            return Result<DeltaGetResponse?, RagError>.Failure(GraphErrorMapping.Map(ex));
        }
    }

    /// <summary>
    /// Attempts to fetch the first delta page.
    /// <list type="bullet">
    /// <item>Success with a page — the delta token was accepted.</item>
    /// <item>Success with <see langword="null"/> — the token is stale (<c>resyncRequired</c>)
    /// or the item is gone (<c>itemNotFound</c>), so the caller falls back to a full
    /// traversal.</item>
    /// <item>Failure — any other Graph or transport failure.</item>
    /// </list>
    /// </summary>
    private async Task<Result<DeltaGetResponse?, RagError>> TryFetchFirstDeltaPageAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await _graph.Drives[_options.DriveId].Items["root"].Delta
                .WithUrl(_options.DeltaToken!)
                .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Result<DeltaGetResponse?, RagError>.Success(page);
        }
        catch (ODataError ex) when (GraphErrorMapping.IsStaleDeltaToken(ex))
        {
            return Result<DeltaGetResponse?, RagError>.Success(null);
        }
        catch (Exception ex) when (GraphErrorMapping.IsMappable(ex, cancellationToken))
        {
            return Result<DeltaGetResponse?, RagError>.Failure(GraphErrorMapping.Map(ex));
        }
    }
}
