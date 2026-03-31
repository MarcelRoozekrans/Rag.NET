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
    /// Creation or publication timestamp. Defaults to <see cref="DateTime.UtcNow"/> (ingest time)
    /// when not set explicitly. Serialised into chunk metadata as <c>"created_at"</c> by
    /// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/> for use by
    /// <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/>.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
