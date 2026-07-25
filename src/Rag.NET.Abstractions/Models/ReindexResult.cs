namespace Rag.NET.Models;

/// <summary>Outcome of a <c>ReindexStaleAsync</c> run.</summary>
public sealed record ReindexResult
{
    /// <summary>Documents whose vectors were re-embedded, re-stored, and re-stamped.</summary>
    public required IReadOnlyList<string> Reindexed { get; init; }

    /// <summary>
    /// Stale documents that could only be reported, not re-indexed, because no
    /// <c>IRagDataManager</c> is available to supply the stored chunk text.
    /// Re-ingest these from their original source.
    /// </summary>
    public required IReadOnlyList<string> ReportedStale { get; init; }

    /// <summary>Documents whose re-indexing failed, with the failure message.</summary>
    public required IReadOnlyList<(string DocumentId, string Error)> Failed { get; init; }
}
