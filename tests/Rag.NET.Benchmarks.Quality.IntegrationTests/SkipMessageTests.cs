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

    /// <summary>
    /// <see cref="BeirHarness.SkipReason"/> is exactly the base sentence with the live hint
    /// appended — not merely a string that happens to contain the hint somewhere.
    /// </summary>
    /// <remarks>
    /// This is a machine-independent equality on the composition, so it fails if the hint is ever
    /// concatenated in the wrong place, duplicated, or replaced with something that merely
    /// resembles it. It provably fails if <c>+ BeirDatasetCache.DescribeUnreferencedConventionalCache()</c>
    /// is removed from <see cref="BeirHarness.SkipReason"/> WHEN the live hint is non-null — on a
    /// machine without <c>~/.cache/ragnet-beir</c> the hint is null and appending an empty string is
    /// unobservable from the outside no matter what produced it, which is exactly why
    /// <c>SkipReasonWiringTests</c> in <c>Rag.NET.RepoConventions.Tests</c> also pins the call site
    /// in source, deterministically, on any machine.
    /// </remarks>
    [Fact]
    public void TheSkipReasonIsTheBaseSentenceWithTheLiveHintAppended()
    {
        var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache();

        Assert.Equal(BeirHarness.SkipReasonBaseSentence + (hint ?? string.Empty), BeirHarness.SkipReason);

        if (hint is not null)
        {
            Assert.NotEqual(
                BeirHarness.SkipReasonBaseSentence, BeirHarness.SkipReason, StringComparer.Ordinal);
        }
    }
}
