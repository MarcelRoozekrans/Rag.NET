using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Rag.NET.DataProviders;

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

    protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in _container
            .GetBlobsAsync(prefix: _options.Prefix, cancellationToken: cancellationToken,
                traits: Azure.Storage.Blobs.Models.BlobTraits.None,
                states: Azure.Storage.Blobs.Models.BlobStates.None)
            .ConfigureAwait(false))
        {
            var etag = blob.Properties.ETag?.ToString("H");
            var capturedName = blob.Name;

            yield return new FileHandle(
                Id:               blob.Name,
                FileName:         Path.GetFileName(blob.Name),
                ETag:             etag,
                OpenContentAsync: async ct =>
                {
                    var blobClient = _container.GetBlobClient(capturedName);
                    var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct)
                        .ConfigureAwait(false);
                    return download.Value.Content;
                });
        }
    }
}
