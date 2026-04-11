using System.Runtime.CompilerServices;
using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>
/// Base class for cloud storage data providers.
/// Handles extension filtering and <see cref="CloudStorageOptions.Filter"/> application.
/// Connectors only need to implement <see cref="GetFileHandlesAsync"/>.
/// </summary>
public abstract class FileContentProviderBase : IFileContentProvider
{
    private readonly CloudStorageOptions _options;

    protected FileContentProviderBase(CloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Enumerate raw file handles from the vendor SDK.
    /// No filtering required — the base class handles it.
    /// </summary>
    protected abstract IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var handle in GetFileHandlesAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!MatchesExtension(handle.FileName)) continue;
            if (_options.Filter is not null && !_options.Filter(handle.Id)) continue;

            yield return new FileEntry(
                Id:               new EntryId(handle.Id),
                FileName:         handle.FileName,
                OpenContentAsync: handle.OpenContentAsync,
                ETag:             handle.ETag);
        }
    }

    private bool MatchesExtension(string fileName)
        => FileExtensionMatcher.Matches(fileName, _options.Extensions);
}
