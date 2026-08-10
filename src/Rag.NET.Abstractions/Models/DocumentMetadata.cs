using ZeroAlloc.Validation;

namespace Rag.NET.Models;

/// <summary>
/// Caller-supplied information about a document, passed to <c>IIngestor.IngestAsync</c> alongside
/// its content stream. Distinct from <see cref="DocumentSummary"/>, which is what the sidecar
/// store reports back after ingestion.
/// </summary>
[Validate]
public sealed class DocumentMetadata
{
    /// <summary>The document's identifier, assigned by the caller before ingestion.</summary>
    public required DocumentId DocumentId { get; init; }

    /// <summary>The document's source file name. Validated as non-empty at ingest time.</summary>
    [NotEmpty]
    public required string FileName { get; init; }

    /// <summary>
    /// The document's MIME content type. <see langword="null"/> when the caller did not supply
    /// one — not the same as an empty or unrecognised type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Caller-supplied tags, merged into every chunk's <see cref="TextChunk.Metadata"/> at ingest
    /// time. Applied before the framework's own reserved keys (see
    /// <see cref="ReservedMetadataKeys"/>) using <c>TryAdd</c>, so a tag using a reserved key name
    /// silently shadows the framework's own value for that key rather than losing to it — unlike
    /// the data-provider connector path, nothing here rejects the collision. Never
    /// <see langword="null"/>; no tags is an empty dictionary, not a missing one.
    /// Values are typed (<see cref="MetadataValue"/>) so a number or date supplied here reaches
    /// the vector store as that type; string literals keep their shape via the implicit
    /// conversion.
    /// </summary>
    public IDictionary<string, MetadataValue> Tags { get; init; } = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

    /// <summary>
    /// Creation or publication timestamp, if known. <see langword="null"/> means the timestamp
    /// is unknown — it is <b>not</b> defaulted to ingest time, because ingest time is not when
    /// the document was created. When set, it is serialised into chunk metadata as
    /// <c>"created_at"</c> by <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/>; when
    /// absent, no <c>"created_at"</c> tag is written, and
    /// <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/> treats the document neutrally
    /// instead of ranking it as freshly created.
    /// </summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>
    /// Last-modified timestamp, if known. <see langword="null"/> means the timestamp is
    /// unknown — like <see cref="CreatedAt"/>, it is never defaulted. When set, it is serialised
    /// into chunk metadata as <c>"updated_at"</c> by
    /// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/>; when absent, no
    /// <c>"updated_at"</c> tag is written. <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/>
    /// prefers this over <see cref="CreatedAt"/> when ranking, because freshness is a
    /// last-changed question, not a creation-date one.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
}
