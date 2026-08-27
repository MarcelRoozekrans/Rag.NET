namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The three properties deleted alongside the behaviour — carried by <c>GraphRagRetrievalOptions</c>
/// at the time, since renamed to <c>GraphRagGlobalSearchOptions</c> — at the values they carried
/// when the pinned figures were measured.
/// </summary>
/// <remarks>
/// <b><c>PageRankWeight</c> defaults to 0.3, not 0.</b> 0.3 was the shipped default when the
/// figures were taken; it became 0 in #296, which made the blend the identity. Defaulting to 0
/// here would make the fixture a no-op and silently turn every arm into its own control.
/// </remarks>
internal sealed class LegacyPageRankOptions
{
    /// <summary>PageRank-versus-similarity blend weight. 0.3 when the figures were measured.</summary>
    public double PageRankWeight { get; set; } = 0.3;

    /// <summary>Hop depth for local entity traversal.</summary>
    public int LocalSearchDepth { get; set; } = 1;

    /// <summary>How many entity chunks seed the traversal.</summary>
    public int LocalTopEntities { get; set; } = 10;
}
