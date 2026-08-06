namespace Rag.NET.Models;

/// <summary>Aggregate counts reported by <c>IRagDataManager.GetStatsAsync</c>.</summary>
public sealed record DataManagerStats
{
    /// <summary>Number of documents currently tracked in the sidecar store.</summary>
    public required int DocumentCount   { get; init; }

    /// <summary>Total chunks across every tracked document.</summary>
    public required int TotalChunkCount { get; init; }
}
