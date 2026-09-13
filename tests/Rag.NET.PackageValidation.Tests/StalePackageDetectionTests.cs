using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// What <see cref="ProducedPackageTests.IsAnotherBranchsPack"/> excuses, and what it must not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> The staleness allowance added for
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/587">#587</see> turns a failure
/// into a skip, and a skip is exactly how a guard stops guarding without anyone noticing. The
/// allowance is therefore pinned by cases rather than trusted: the defect
/// <c>EveryPackageCarriesTheVersionGitVersionDerives</c> exists for — a pack that silently fell
/// back to the SDK default <c>1.0.0</c> — must still fail.
/// </para>
/// <para>
/// <b>These need no packed packages</b>, because the decision is a pure function of the versions
/// and the derived string. That is the reason it was extracted rather than left inline: the inline
/// form could only have been tested by packing 73 packages twice.
/// </para>
/// </remarks>
public sealed class StalePackageDetectionTests
{
    private const string Derived = "0.1.1-fix-587-stale-package-skip.1";

    /// <summary>A consistent build of another branch is staleness.</summary>
    [Fact]
    public void AConsistentBuildOfAnotherBranchIsExcused()
    {
        string[] versions =
        [
            "0.1.1-docs-scope-560-feature-claims.1",
            "0.1.1-docs-scope-560-feature-claims.1",
        ];

        Assert.True(ProducedPackageTests.IsAnotherBranchsPack(versions, Derived));
    }

    /// <summary>The SDK default must still fail — it is the defect the guard exists for.</summary>
    /// <remarks>
    /// <c>1.0.0</c> is what a pack produces when <c>-p:Version=</c> is absent or empty, silently
    /// and with exit code 0. It differs in <c>MajorMinorPatch</c>, not merely in the prerelease
    /// label, which is the distinction the allowance turns on.
    /// </remarks>
    [Fact]
    public void TheSdkDefaultIsNotExcused()
    {
        string[] versions = ["1.0.0", "1.0.0"];

        Assert.False(ProducedPackageTests.IsAnotherBranchsPack(versions, Derived));
    }

    /// <summary>Packages disagreeing with each other is a real defect however it arose.</summary>
    [Fact]
    public void AnInconsistentSetIsNotExcused()
    {
        string[] versions =
        [
            "0.1.1-docs-scope-560-feature-claims.1",
            "0.1.1-some-other-branch.1",
        ];

        Assert.False(ProducedPackageTests.IsAnotherBranchsPack(versions, Derived));
    }

    /// <summary>A different Major.Minor.Patch is never staleness.</summary>
    /// <remarks>
    /// Same prerelease shape, different numbers: a genuinely wrong version rather than a build of
    /// another branch, so the skip must not swallow it.
    /// </remarks>
    [Fact]
    public void ADifferentMajorMinorPatchIsNotExcused()
    {
        string[] versions = ["0.2.0-fix-587-stale-package-skip.1"];

        Assert.False(ProducedPackageTests.IsAnotherBranchsPack(versions, Derived));
    }

    /// <summary>Matching versions are not staleness — there is nothing to excuse.</summary>
    [Fact]
    public void AMatchingBuildIsNotExcused()
    {
        string[] versions = [Derived, Derived];

        Assert.False(ProducedPackageTests.IsAnotherBranchsPack(versions, Derived));
    }

    /// <summary>A missing version element falls through rather than being excused.</summary>
    /// <remarks>
    /// <c>ReadNuspecElement</c> returns null for an absent element, which the caller coalesces to
    /// empty. Empty can never share a <c>MajorMinorPatch</c> with a real derived version, so the
    /// assertion below the skip is what reports it.
    /// </remarks>
    [Fact]
    public void AMissingVersionIsNotExcused()
    {
        string[] versions = [string.Empty, string.Empty];

        Assert.False(ProducedPackageTests.IsAnotherBranchsPack(versions, Derived));
    }

    /// <summary>An empty set is not staleness; the count guard elsewhere owns that case.</summary>
    [Fact]
    public void NoPackagesAtAllIsNotExcused()
    {
        Assert.False(ProducedPackageTests.IsAnotherBranchsPack([], Derived));
    }
}
