namespace Rag.NET.Models;

/// <summary>
/// One document's entry as tracked by <c>IRagDataManager</c>'s sidecar store — the summary
/// returned by <c>GetDocumentsAsync</c>, distinct from the chunks and vectors that carry the
/// actual retrievable content.
/// </summary>
public sealed record DocumentSummary
{
    /// <summary>The document this summary describes.</summary>
    public required DocumentId DocumentId  { get; init; }

    /// <summary>The document's source file name, as supplied at ingest time.</summary>
    public required string FileName    { get; init; }

    /// <summary>
    /// The document's MIME content type, as supplied at ingest time.
    /// <see langword="null"/> when none was supplied — not the same as an unrecognised type.
    /// </summary>
    public string?         ContentType { get; init; }

    /// <summary>Number of chunks this document produced at its most recent ingest.</summary>
    public required int    ChunkCount  { get; init; }

    /// <summary>
    /// When the sidecar store recorded this document — the ingest time, not any timestamp from
    /// the source system. Re-ingesting the same document id updates this to the new ingest time.
    /// </summary>
    public required DateTimeOffset IngestedAt { get; init; }

    /// <summary>
    /// A snapshot of the document's tags (<c>DocumentMetadata.Tags</c>) as supplied at its most
    /// recent ingest, with each value's <see cref="MetadataValue.Kind"/> intact — a numeric tag
    /// reads back as a number, not its textual form. Never <see langword="null"/>; a document
    /// with no tags has an empty dictionary here, not a missing one.
    /// </summary>
    public IDictionary<string, MetadataValue> Tags { get; init; }
        = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
}
