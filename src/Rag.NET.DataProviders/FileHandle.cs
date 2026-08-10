using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>
/// Internal transfer record yielded by connector implementations before filtering is applied.
/// <paramref name="Metadata"/> is forwarded to <c>FileEntry.Metadata</c> (and from there to
/// <c>DocumentMetadata.Tags</c>) when the handle survives filtering; values are typed
/// (<see cref="MetadataValue"/>), so a connector can hand over a number, boolean or date and it
/// reaches chunk metadata as that type. <paramref name="CreatedAt"/> and
/// <paramref name="UpdatedAt"/> are forwarded the same way, onto
/// <c>DocumentMetadata.CreatedAt</c>/<c>UpdatedAt</c> — the typed timestamp channel, distinct
/// from any tag a connector also writes into <paramref name="Metadata"/>.
/// </summary>
public sealed record FileHandle(
    string Id,
    string FileName,
    string? ETag,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    IReadOnlyDictionary<string, MetadataValue>? Metadata = null,
    DateTime? CreatedAt = null,
    DateTime? UpdatedAt = null);
