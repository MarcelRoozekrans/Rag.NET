using Rag.NET.Models;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The gate and cache conventions shared by the self-query pilot and the self-query cell.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because <see cref="HasEntries"/> encodes a correction that must not
/// drift back: the cache SHARDS entries into subdirectories by key prefix, so a non-recursive
/// enumeration reports a full cache as empty. The pilot shipped with that bug for one commit — six
/// entries on disk and the replay run skipping with "nothing to replay" — and two copies of the
/// check is two chances to reintroduce it.
/// </remarks>
internal static class SelfQueryGate
{
    /// <summary>
    /// Reads the generate flag and returns the cache mode it implies.
    /// </summary>
    /// <param name="variable">The environment variable naming the gate.</param>
    /// <param name="generating">Set to whether real calls are permitted.</param>
    /// <returns>Fill when generating, RefuseOnMiss otherwise.</returns>
    /// <remarks>
    /// RefuseOnMiss is paired with a null inner client at the call sites, so a prompt outside the
    /// cache throws instead of quietly reaching the network. That is what makes a replay run free by
    /// construction rather than by intention.
    /// </remarks>
    public static GraphExtractionCacheMode Mode(string variable, out bool generating)
    {
        var flag = Environment.GetEnvironmentVariable(variable);
        generating = !string.IsNullOrWhiteSpace(flag)
            && !string.Equals(flag, "0", StringComparison.Ordinal)
            && !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);

        return generating ? GraphExtractionCacheMode.Fill : GraphExtractionCacheMode.RefuseOnMiss;
    }

    /// <summary>Reports whether the cache holds anything to replay.</summary>
    /// <remarks>
    /// <b>Recursive, and a missing directory counts as empty.</b> Both halves are corrections: the
    /// cache shards into subdirectories, so a top-level enumeration finds nothing however full it
    /// is; and enumerating a directory that does not exist yet throws
    /// <see cref="DirectoryNotFoundException"/>, turning "nothing cached yet" into a red test rather
    /// than a skip.
    /// </remarks>
    public static bool HasEntries(GraphExtractionCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        return Directory.Exists(cache.EntryDirectory)
            && Directory.EnumerateFiles(cache.EntryDirectory, "*", SearchOption.AllDirectories).Any();
    }
}
