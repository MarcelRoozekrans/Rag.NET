using System.Runtime.CompilerServices;
using Rag.NET.Models;
using ZeroAlloc.Results;

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
    /// Yield <see cref="Result{TValue,TError}.Failure"/> on HTTP errors.
    /// No filtering required — the base class handles it.
    /// </summary>
    protected abstract IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in GetFileHandlesAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (result.IsFailure)
            {
                yield return Result<FileEntry, RagError>.Failure(result.Error);
                continue;
            }

            var handle = result.Value;
            if (!MatchesExtension(handle.FileName)) continue;
            if (_options.Filter is not null && !_options.Filter(handle.Id)) continue;

            yield return Result<FileEntry, RagError>.Success(new FileEntry(
                Id:               new EntryId(handle.Id),
                FileName:         handle.FileName,
                OpenContentAsync: handle.OpenContentAsync,
                ETag:             handle.ETag,
                Metadata:         handle.Metadata,
                CreatedAt:        handle.CreatedAt,
                UpdatedAt:        handle.UpdatedAt));
        }
    }

    private bool MatchesExtension(string fileName)
        => FileExtensionMatcher.Matches(fileName, _options.Extensions);
}
