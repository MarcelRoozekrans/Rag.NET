using System.Runtime.CompilerServices;
using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>
/// Enumerates files from a local directory as <see cref="FileEntry"/> objects.
/// ETag is computed cheaply from last-write timestamp and file size — no I/O until
/// <see cref="FileEntry.OpenContentAsync"/> is called.
/// </summary>
public sealed class LocalFilesDataProvider : IFileContentProvider
{
    private readonly string _rootPath;
    private readonly LocalFilesOptions _options;

    public LocalFilesDataProvider(string rootPath, LocalFilesOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
        _options = options ?? new LocalFilesOptions();
    }

    public async IAsyncEnumerable<FileEntry> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // ensure async, avoid CS1998

        var files = Directory.EnumerateFiles(_rootPath, "*", _options.SearchOption);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!MatchesExtension(path)) continue;
            if (_options.Filter is not null && !_options.Filter(path)) continue;

            var info = new FileInfo(path);
            var etag = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
            var capturedPath = path;

            yield return new FileEntry(
                Id: new EntryId(Path.GetRelativePath(_rootPath, path)),
                FileName: Path.GetFileName(path),
                OpenContentAsync: _ => Task.FromResult<Stream>(File.OpenRead(capturedPath)),
                ETag: etag);
        }
    }

    private bool MatchesExtension(string path)
    {
        if (_options.Extensions is ["*"]) return true;
        var ext = Path.GetExtension(path);
        return _options.Extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, "*", StringComparison.Ordinal));
    }
}
