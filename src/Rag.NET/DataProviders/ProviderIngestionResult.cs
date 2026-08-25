using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>Summary of a completed <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> run.</summary>
/// <param name="Ingested">Entries that were parsed, chunked and stored.</param>
/// <param name="Skipped">
/// Entries that were <b>already up to date</b> — an ETag or content hash matched, so there was
/// nothing to do. Not a failure.
/// </param>
/// <param name="Failed">
/// Entries that threw. Each contributes one entry to <see cref="Errors"/>.
/// </param>
/// <param name="Deleted">Documents removed by the cleanup pass.</param>
/// <param name="Errors">One error per failed entry.</param>
/// <remarks>
/// <b><c>Failed</c> was split out of <c>Skipped</c> in #355.</b> A throwing entry used to be
/// counted as skipped, so a sitemap ingest against a missing index reported fifty skips and no
/// failures — the only way to tell was to read <see cref="Errors"/>. Two outcomes that mean
/// opposite things had one name, and the reassuring one was showing.
/// </remarks>
public sealed record ProviderIngestionResult(
    int Ingested,
    int Skipped,
    int Failed,
    int Deleted,
    IReadOnlyList<RagError> Errors);
