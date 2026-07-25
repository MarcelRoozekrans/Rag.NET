using Rag.NET.Models.Options;

namespace Rag.NET.Models;

/// <summary>
/// A queued ingestion work item: one document to be parsed, chunked, embedded, and stored
/// by the background processor.
/// </summary>
/// <remarks>
/// <see cref="Content"/> is a byte array rather than a <see cref="Stream"/> deliberately:
/// jobs outlive the enqueue call (they sit in the queue until a background processor picks
/// them up), so the payload must not depend on a stream whose lifetime is owned by the
/// producer — e.g. an HTTP request body that is disposed when the webhook request completes.
/// </remarks>
public sealed record IngestionJob
{
    /// <summary>Raw document bytes. Owned by the job; never a borrowed stream.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Document identity and metadata passed to the ingestor.</summary>
    public required DocumentMetadata Metadata { get; init; }

    /// <summary>Optional per-job ingestion options; <see langword="null"/> uses pipeline defaults.</summary>
    public IngestionOptions? Options { get; init; }
}
