namespace Rag.NET.DataProviders;

/// <summary>
/// Internal transfer record yielded by connector implementations before filtering is applied.
/// </summary>
public sealed record FileHandle(
    string Id,
    string FileName,
    string? ETag,
    Func<CancellationToken, Task<Stream>> OpenAsync);
