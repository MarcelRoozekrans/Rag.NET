namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// What a <see cref="GraphExtractionCache"/> does when a key is not on disk. The two modes exist
/// for the reason <see cref="HypotheticalCacheMode"/>'s do: the generation tool is allowed to
/// create text, the run that measures anything is only allowed to read it.
/// </summary>
public enum GraphExtractionCacheMode
{
    /// <summary>
    /// A miss calls the model and stores what comes back. The generation tool's mode, and what
    /// makes that tool resumable across thousands of calls — everything already stored is a hit.
    /// </summary>
    Fill = 0,

    /// <summary>
    /// A miss throws, naming the missing key. The guard's mode. A graph half-extracted by a
    /// recorded run and half by a fresh one is two experiments reported as one, and — unlike a
    /// missing vector, which at least has a dimension to be wrong — a freshly generated extraction
    /// is indistinguishable from a replayed one once it is in the graph.
    /// </summary>
    RefuseOnMiss = 1,
}
