using ZeroAlloc.Validation;

namespace Rag.NET.Models;

[Validate]
public sealed class DocumentMetadata
{
    public required DocumentId DocumentId { get; init; }

    [NotEmpty]
    public required string FileName { get; init; }

    public string? ContentType { get; init; }
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

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
