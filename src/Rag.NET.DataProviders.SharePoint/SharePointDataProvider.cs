using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models.ODataErrors;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.SharePoint;

/// <summary>
/// Enumerates files from a SharePoint drive via Microsoft Graph.
/// Full run: recursive drive enumeration. Delta run: Graph delta API using stored deltaLink token.
/// Stale/expired delta token: automatically falls back to full traversal.
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
        var page = await _graph.Drives[_options.DriveId].Items["root"].Children
            .GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        while (page is not null)
        {
#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross yield/await boundaries in async iterators
            foreach (var item in page.Value ?? [])
#pragma warning restore HLQ012
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is null) continue;

                var capturedId = item.Id!;
                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id:               (item.ParentReference?.Path ?? string.Empty) + "/" + item.Name,
                    FileName:         item.Name ?? capturedId,
                    ETag:             item.ETag,
                    OpenContentAsync: async ct =>
                        await _graph.Drives[_options.DriveId].Items[capturedId].Content
                            .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                            ?? Stream.Null));
            }

            page = page.OdataNextLink is not null
                ? await _graph.Drives[_options.DriveId].Items["root"].Children
                    .WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // C# does not permit yield inside a catch clause. We eagerly attempt the first
        // delta page fetch (no yielding yet), and if the token is stale we fall back to
        // a full traversal — delegating entirely to GetFullHandlesAsync.
        DeltaGetResponse? firstPage = await TryFetchFirstDeltaPageAsync(cancellationToken)
            .ConfigureAwait(false);

        if (firstPage is null)
        {
            // Token was stale / not found — fall back to full traversal.
            await foreach (var handle in GetFullHandlesAsync(cancellationToken).ConfigureAwait(false))
                yield return handle;
            yield break;
        }

        var page = firstPage;
        while (page is not null)
        {
#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross yield/await boundaries in async iterators
            foreach (var item in page.Value ?? [])
#pragma warning restore HLQ012
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is null || item.Deleted is not null) continue;

                var capturedId = item.Id!;
                yield return Result<FileHandle, RagError>.Success(new FileHandle(
                    Id:               (item.ParentReference?.Path ?? string.Empty) + "/" + item.Name,
                    FileName:         item.Name ?? capturedId,
                    ETag:             item.ETag,
                    OpenContentAsync: async ct =>
                        await _graph.Drives[_options.DriveId].Items[capturedId].Content
                            .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                            ?? Stream.Null));
            }

            page = page.OdataNextLink is not null
                ? await _graph.Drives[_options.DriveId].Items["root"].Delta
                    .WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
    }

    /// <summary>
    /// Attempts to fetch the first delta page. Returns <c>null</c> when the delta token is
    /// stale (<c>resyncRequired</c>) or the item is no longer found (<c>itemNotFound</c>).
    /// All other exceptions propagate normally.
    /// </summary>
    private async Task<DeltaGetResponse?> TryFetchFirstDeltaPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _graph.Drives[_options.DriveId].Items["root"].Delta
                .WithUrl(_options.DeltaToken!).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ODataError ex)
            when (string.Equals(ex.Error?.Code, "resyncRequired", StringComparison.Ordinal)
               || string.Equals(ex.Error?.Code, "itemNotFound", StringComparison.Ordinal))
        {
            return null;
        }
    }
}
