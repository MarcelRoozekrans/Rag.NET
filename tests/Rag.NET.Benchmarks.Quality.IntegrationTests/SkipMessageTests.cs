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
    /// Passes the resolved value straight into an overload rather than setting
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

    /// <summary>
    /// When the conventional directory exists and has an <c>env.sh</c> beside it, the hint names
    /// both and tells the reader to source the file.
    /// </summary>
    /// <remarks>
    /// Builds the conventional directory under a temporary root supplied to the three-parameter
    /// overload, rather than depending on the real <c>~/.cache/ragnet-beir</c>: every CI runner
    /// lacks that directory, and a test gated on its presence would leave this exact sentence — the
    /// one a human actually reads — asserted by nothing outside a machine that happens to have it.
    /// A GUID-suffixed directory name keeps this test from colliding with its siblings, which xunit
    /// may run in the same process at the same time.
    /// </remarks>
    [Fact]
    public void TheHintNamesTheDirectoryAndTheFileToSourceWhenAnEnvScriptIsPresent()
    {
        var conventional = Path.Combine(Path.GetTempPath(), $"ragnet-beir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(conventional);
        var envScript = Path.Combine(conventional, "env.sh");
        File.WriteAllText(envScript, string.Empty);

        try
        {
            var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache(null, conventional);

            Assert.Equal(
                $" A cache is already present at '{conventional}' and nothing points at it: " +
                $"source '{envScript}' to use it.",
                hint);
        }
        finally
        {
            Directory.Delete(conventional, recursive: true);
        }
    }

    /// <summary>
    /// When the conventional directory exists without an <c>env.sh</c>, the hint names the
    /// directory and tells the reader to set the variable instead.
    /// </summary>
    /// <remarks>See the sibling test for why a temporary directory is used here.</remarks>
    [Fact]
    public void TheHintNamesTheDirectoryAndTheVariableWhenNoEnvScriptIsPresent()
    {
        var conventional = Path.Combine(Path.GetTempPath(), $"ragnet-beir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(conventional);

        try
        {
            var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache(null, conventional);

            Assert.Equal(
                $" A cache directory is already present at '{conventional}' and nothing points " +
                $"at it: set {BeirDatasetCache.CacheDirectoryVariable} to it to use it.",
                hint);
        }
        finally
        {
            Directory.Delete(conventional, recursive: true);
        }
    }

    /// <summary>When the conventional directory does not exist, there is nothing to hint about.</summary>
    [Fact]
    public void NoHintWhenNoConventionalDirectoryExists()
    {
        var conventional = Path.Combine(Path.GetTempPath(), $"ragnet-beir-test-{Guid.NewGuid():N}");

        Assert.False(Directory.Exists(conventional));
        Assert.Null(BeirDatasetCache.DescribeUnreferencedConventionalCache(null, conventional));
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
