using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Graph;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.OneDrive;

/// <summary>
/// Enumerates files from a OneDrive user drive via Microsoft Graph.
/// Full run: children of drive root. Delta run: Graph delta API using stored deltaLink token.
/// Stale delta token: falls back to full traversal automatically.
/// The user's drive ID is resolved once on first use.
/// <para>
/// Graph failures reach the caller through the <see cref="Result{TValue,TError}"/> channel
/// rather than as thrown exceptions: a response carrying a status becomes
/// <see cref="RagError.HttpFailed"/>, and a failure with no response at all — DNS, TLS, socket
/// reset, client-side timeout, token acquisition — becomes
/// <see cref="RagError.TransportFailed"/>. Caller cancellation always propagates.
/// </para>
/// </summary>
public sealed class OneDriveDataProvider : FileContentProviderBase
{
    private readonly GraphServiceClient _graph;
    private readonly OneDriveOptions _options;

    public OneDriveDataProvider(GraphServiceClient graph, OneDriveOptions options)
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

    private async Task<Result<string, RagError>> GetDriveIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var drive = await _graph.Users[_options.UserId].Drive
                .GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return drive?.Id is { } id
                ? Result<string, RagError>.Success(id)
                // Synthetic status per the RagError.HttpFailed convention: the server answered,
                // but with no payload this connector can proceed from. Mirrors
                // ExchangeMailDataProvider. Previously an InvalidOperationException.
                : Result<string, RagError>.Failure(new RagError.HttpFailed(
                    HttpStatusCode.NoContent,
                    $"Could not resolve OneDrive ID for user '{_options.UserId}'."));
        }
        catch (Exception ex) when (GraphErrorMapping.IsMappable(ex, cancellationToken))
        {
            return Result<string, RagError>.Failure(GraphErrorMapping.Map(ex));
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var driveIdResult = await GetDriveIdAsync(cancellationToken).ConfigureAwait(false);
        if (driveIdResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(driveIdResult.Error);
            yield break;
        }

        var driveId = driveIdResult.Value;
        await foreach (var handle in EnumerateChildrenAsync(driveId, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return handle;
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> EnumerateChildrenAsync(
        string driveId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? nextLink = null;
        do
        {
            var pageResult = await FetchChildrenPageAsync(driveId, nextLink, cancellationToken)
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

                yield return Result<FileHandle, RagError>.Success(ToHandle(driveId, item));
            }

            nextLink = page.OdataNextLink;
        } while (nextLink is not null);
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // C# does not permit yield inside a catch clause. Every Graph call is therefore made
        // eagerly by a helper that returns a Result, and this iterator only yields.
        var driveIdResult = await GetDriveIdAsync(cancellationToken).ConfigureAwait(false);
        if (driveIdResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(driveIdResult.Error);
            yield break;
        }

        var driveId = driveIdResult.Value;
        var firstResult = await TryFetchFirstDeltaPageAsync(driveId, cancellationToken)
            .ConfigureAwait(false);
        if (firstResult.IsFailure)
        {
            yield return Result<FileHandle, RagError>.Failure(firstResult.Error);
            yield break;
        }

        if (firstResult.Value is null)
        {
            // Token was stale / not found — fall back to full traversal.
            await foreach (var handle in EnumerateChildrenAsync(driveId, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return handle;
            }

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

                yield return Result<FileHandle, RagError>.Success(ToHandle(driveId, item));
            }

            if (page.OdataNextLink is null)
                yield break;

            var nextResult = await FetchDeltaPageAsync(driveId, page.OdataNextLink, cancellationToken)
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
    /// Metadata emitted: <c>drive_id</c> (always) and <c>parent_path</c> — the parent folder path
    /// Graph reports on <c>parentReference</c>. Graph omits <c>parentReference</c> on some delta
    /// payloads, so <c>parent_path</c> is optional and is left out entirely rather than written
    /// empty.
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
    private FileHandle ToHandle(string driveId, DriveItem item)
    {
        var capturedId = item.Id!;
        var capturedDriveId = driveId;
        var parentPath = item.ParentReference?.Path;

        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["drive_id"] = driveId,
        };
        if (!string.IsNullOrEmpty(parentPath))
            metadata["parent_path"] = parentPath;

        return new FileHandle(
            Id:               (parentPath ?? string.Empty) + "/" + item.Name,
            FileName:         item.Name ?? capturedId,
            ETag:             item.ETag,
            OpenContentAsync: async ct =>
                await _graph.Drives[capturedDriveId].Items[capturedId].Content
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                    ?? Stream.Null,
            Metadata:         metadata,
            CreatedAt:        item.CreatedDateTime?.UtcDateTime,
            UpdatedAt:        item.LastModifiedDateTime?.UtcDateTime);
    }

    private async Task<Result<DriveItemCollectionResponse?, RagError>> FetchChildrenPageAsync(
        string driveId, string? nextLink, CancellationToken cancellationToken)
    {
        var builder = _graph.Drives[driveId].Items["root"].Children;
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
        string driveId, string url, CancellationToken cancellationToken)
    {
        try
        {
            var page = await _graph.Drives[driveId].Items["root"].Delta
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
        string driveId, CancellationToken cancellationToken)
    {
        try
        {
            var page = await _graph.Drives[driveId].Items["root"].Delta
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
