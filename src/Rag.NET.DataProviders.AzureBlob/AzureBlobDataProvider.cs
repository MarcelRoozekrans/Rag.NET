using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.AzureBlob;

/// <summary>
/// Enumerates blobs from an Azure Blob Storage container.
/// Full run: all blobs in container. Delta run: blobs whose ETag differs from those seen in previous runs.
/// Resilience is handled by <see cref="Azure.Storage.Blobs.BlobClientOptions"/> retry — do not add external retry.
/// </summary>
public sealed class AzureBlobDataProvider : FileContentProviderBase
{
    private readonly BlobContainerClient _container;
    private readonly AzureBlobOptions _options;

    public AzureBlobDataProvider(BlobContainerClient container, AzureBlobOptions? options = null)
        : base(options ??= new AzureBlobOptions())
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
        _options = options;
    }

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in _container
            .GetBlobsAsync(prefix: _options.Prefix, cancellationToken: cancellationToken,
                traits: Azure.Storage.Blobs.Models.BlobTraits.None,
                states: Azure.Storage.Blobs.Models.BlobStates.None)
            .ConfigureAwait(false))
        {
            yield return Result<FileHandle, RagError>.Success(ToHandle(blob));
        }
    }

    /// <summary>
    /// Builds the handle for a single blob. Synchronous by design: the metadata dictionary is
    /// never built inside the async iterator (design §1).
    /// </summary>
    /// <remarks>
    /// Metadata emitted: <c>path</c> (the full blob name, which is the only path a blob has —
    /// the container is flat and "directories" are just name prefixes) and <c>container</c>.
    /// Both are always present, so the dictionary is never null here.
    /// <para>
    /// Phase 4.10 Task 5: <c>blob.Properties.CreatedOn</c>/<c>LastModified</c> become
    /// <see cref="FileHandle.CreatedAt"/>/<see cref="FileHandle.UpdatedAt"/>. Both are part of
    /// the standard <c>BlobItemProperties</c> the List Blobs response always returns — unlike
    /// <c>Metadata</c>/<c>Tags</c>, they do not require an opt-in <c>BlobTraits</c> flag, so the
    /// <see cref="Azure.Storage.Blobs.Models.BlobTraits.None"/> passed to <c>GetBlobsAsync</c>
    /// does not affect them.
    /// </para>
    /// </remarks>
    private FileHandle ToHandle(Azure.Storage.Blobs.Models.BlobItem blob)
    {
        var capturedName = blob.Name;
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["path"]      = blob.Name,
            ["container"] = _container.Name,
        };

        return new FileHandle(
            Id:               blob.Name,
            FileName:         Path.GetFileName(blob.Name),
            ETag:             blob.Properties.ETag?.ToString("H"),
            OpenContentAsync: async ct =>
            {
                var blobClient = _container.GetBlobClient(capturedName);
                var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct)
                    .ConfigureAwait(false);
                return download.Value.Content;
            },
            Metadata:         metadata,
            CreatedAt:        blob.Properties.CreatedOn?.UtcDateTime,
            UpdatedAt:        blob.Properties.LastModified?.UtcDateTime);
    }
}
