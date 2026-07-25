namespace Rag.NET.DataProviders;

/// <summary>
/// Internal transfer record yielded by connector implementations before filtering is applied.
/// <paramref name="Metadata"/> is forwarded to <c>FileEntry.Metadata</c> (and from there to
/// <c>DocumentMetadata.Tags</c>) when the handle survives filtering.
/// </summary>
public sealed record FileHandle(
    string Id,
    string FileName,
    string? ETag,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    IReadOnlyDictionary<string, string>? Metadata = null);
