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
/// <param name="Metadata">
/// Optional key/value pairs forwarded to <see cref="Rag.NET.Models.DocumentMetadata.Tags"/>.
/// Values are typed (<see cref="MetadataValue"/>): a connector can submit a number, boolean or
/// date and it survives — typed — all the way to <see cref="TextChunk.Metadata"/> and the vector
/// store, instead of being stringified at this first hop. Plain strings keep their shape via the
/// implicit conversion.
/// </param>
/// <param name="CreatedAt">
/// Optional creation/publication timestamp forwarded to
/// <see cref="Rag.NET.Models.DocumentMetadata.CreatedAt"/>. Distinct from any string timestamp
/// a connector separately writes into <paramref name="Metadata"/>.
/// </param>
/// <param name="UpdatedAt">
/// Optional last-modified timestamp forwarded to
/// <see cref="Rag.NET.Models.DocumentMetadata.UpdatedAt"/>.
/// </param>
/// <param name="ContentType">
/// This entry's media type — <c>application/pdf</c>, <c>text/markdown</c> — used to select a
/// parser. Takes precedence over the <c>baseMetadata</c> passed to
/// <c>IngestFromProviderAsync</c>, the same way <paramref name="Metadata"/> and the timestamps do.
/// <para>
/// Set it per entry when a provider yields more than one kind of file, which a batch-level
/// default cannot express. Before this existed, the only way to declare a content type at all was
/// that batch-level <c>DocumentMetadata</c> — and its <c>DocumentId</c> and <c>FileName</c> are
/// <c>required</c>, so a caller had to invent values the pipeline immediately overwrote per entry
/// just to say "these are PDFs" (issue #95).
/// </para>
/// <para>
/// <see langword="null"/> falls back to the batch-level value, and then to the pipeline's own
/// content-type resolution — so a provider that has no idea what it is serving can still say
/// nothing and let the parsers decide.
/// </para>
/// </param>
public sealed record FileEntry(
    EntryId Id,
    string FileName,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    string? ETag = null,
    IReadOnlyDictionary<string, MetadataValue>? Metadata = null,
    DateTime? CreatedAt = null,
    DateTime? UpdatedAt = null,
    string? ContentType = null);
