namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// What a <see cref="HypotheticalCache"/> does when a key is not on disk. The two modes exist
/// because the two callers must not behave the same: the generation tool is allowed to create text,
/// the table run is only allowed to read it.
/// </summary>
public enum HypotheticalCacheMode
{
    /// <summary>
    /// A miss invokes the generator and stores what it returns. The one-time generation tool's
    /// mode — and the reason it can resume, because everything already stored is a hit.
    /// </summary>
    Fill = 0,

    /// <summary>
    /// A miss throws, naming the missing key. The table run's mode: hosted LLMs are not
    /// bit-deterministic even at temperature 0, so regenerating a missing hypothetical mid-run
    /// would blend two generations into one table with nothing to say so.
    /// </summary>
    RefuseOnMiss = 1,
}
