using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Pins what an unprovisioned skip message tells the reader.
/// </summary>
/// <remarks>
/// Three separate sessions recorded this corpus as "unprovisioned" while it sat at
/// <c>~/.cache/ragnet-beir</c> with an <c>env.sh</c> beside it — the third time after a note in
/// STATE.md saying two sessions had already done it. The skip was correct every time; the message
/// was a dead end. This pins the difference between "not here" and "here and unreferenced".
/// </remarks>
public sealed class SkipMessageTests
{
    /// <summary>With the variable set, there is nothing to hint about.</summary>
    /// <remarks>
    /// Passes the resolved value straight into the parameterised overload rather than setting
    /// <see cref="BeirDatasetCache.CacheDirectoryVariable"/> via
    /// <see cref="Environment.SetEnvironmentVariable(string, string)"/> — that variable is
    /// process-wide, xunit runs test classes in parallel, and roughly 30 sibling tests read it
    /// through <c>IsProvisioned</c> / <c>IsDatasetCacheProvisioned</c>. Mutating it here would risk a
    /// sibling observing the temporary value and skipping — or running — incorrectly.
    /// </remarks>
    [Fact]
    public void NoHintWhenTheEnvironmentAlreadyPointsAtACache()
    {
        Assert.Null(BeirDatasetCache.DescribeUnreferencedConventionalCache(Path.GetTempPath()));
    }

    /// <summary>The hint names the directory and the file to source.</summary>
    /// <remarks>
    /// Runs only where the conventional directory exists, because the hint is about a real
    /// directory. On a machine without one there is nothing to assert.
    /// </remarks>
    [Fact]
    public void TheHintNamesTheDirectoryWhenTheCacheIsPresentButUnreferenced()
    {
        var conventional = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "ragnet-beir");

        Assert.SkipUnless(
            Directory.Exists(conventional),
            $"No conventional cache at '{conventional}' on this machine, so there is no " +
            "present-but-unreferenced case to assert.");

        var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache(null);

        Assert.NotNull(hint);
        Assert.Contains("ragnet-beir", hint, StringComparison.Ordinal);
    }

    /// <summary>The skip reason carries the hint exactly when there is one.</summary>
    [Fact]
    public void TheSkipReasonCarriesTheHint()
    {
        var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache();

        if (hint is null)
        {
            Assert.DoesNotContain("ragnet-beir", BeirHarness.SkipReason, StringComparison.Ordinal);
            return;
        }

        Assert.Contains(hint, BeirHarness.SkipReason, StringComparison.Ordinal);
    }
}
