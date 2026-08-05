using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>
/// Represents a single file from an <see cref="IFileContentProvider"/>.
/// Content is loaded lazily — <see cref="OpenContentAsync"/> is only called when the file needs to be ingested.
/// </summary>
/// <param name="Id">Stable identifier for this file (absolute path, URL, or GitHub path).</param>
/// <param name="FileName">File name used for MIME/parser detection (e.g. <c>"report.pdf"</c>).</param>
/// <param name="OpenContentAsync">Opens a stream of the file's content. Caller is responsible for disposal.</param>
/// <param name="ETag">
/// Optional cheap provider-supplied fingerprint (last-modified+size, <c>&lt;lastmod&gt;</c>, blob SHA, etc.).
/// When the stored ETag matches, content is not fetched at all.
/// </param>
/// <param name="Metadata">Optional key/value pairs forwarded to <see cref="Rag.NET.Models.DocumentMetadata.Tags"/>.</param>
/// <param name="CreatedAt">
/// Optional creation/publication timestamp forwarded to
/// <see cref="Rag.NET.Models.DocumentMetadata.CreatedAt"/>. Distinct from any string timestamp
/// a connector separately writes into <paramref name="Metadata"/>.
/// </param>
/// <param name="UpdatedAt">
/// Optional last-modified timestamp forwarded to
/// <see cref="Rag.NET.Models.DocumentMetadata.UpdatedAt"/>.
/// </param>
public sealed record FileEntry(
    EntryId Id,
    string FileName,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    string? ETag = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTime? CreatedAt = null,
    DateTime? UpdatedAt = null);
