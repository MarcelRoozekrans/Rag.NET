namespace Rag.NET.DataProviders;

/// <summary>
/// Provides a stream of file entries from an arbitrary source (local disk, web, GitHub, etc.).
/// </summary>
public interface IFileContentProvider
{
    IAsyncEnumerable<FileEntry> GetFilesAsync(CancellationToken cancellationToken = default);
}
