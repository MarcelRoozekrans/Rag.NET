using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// The model revision the workflow fetches and the one the cache keys on must be the same string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Before
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/607">#607</see> the embedding
/// cache keyed on <c>all-MiniLM-L6-v2/onnx</c> — a repository name, not an export — while
/// <c>nightly.yml</c> pinned a revision and verified a SHA-256 against it, on the stated grounds
/// that a silently different model moves the parity number unattributably. Bumping the pin changed
/// no cache key, so every vector the previous export produced read as a hit.
/// </para>
/// <para>
/// <b>The re-key fixed the past; this fixes the future.</b> Putting the revision in the identity
/// helps only while the two stay equal, and they live in different files with different reasons to
/// change — a workflow bump and a benchmark constant. Nothing but this test connects them.
/// </para>
/// <para>
/// <b>Read as source text, not through a project reference.</b> The constant lives in
/// <c>Rag.NET.Benchmarks.Quality.IntegrationTests</c>, which declares <c>RequiresSecrets</c> and so
/// runs in the advisory nightly tier rather than the tier that gates a pull request. A guard that
/// ran only there would let the two drift through a merge, which is the failure it exists to catch.
/// Scanning the file keeps the check on the gating tier, as <see cref="SkipReasonWiringTests"/>
/// scans sources it does not reference.
/// </para>
/// </remarks>
public sealed partial class ModelRevisionAgreementTests
{
    /// <summary>The file holding the benchmark-side constant.</summary>
    private const string HarnessPath =
        "tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs";

    /// <summary>Matches the workflow's pinned revision.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        @"MINILM_REVISION:\s*(?<revision>[0-9a-f]{40})",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex WorkflowRevision();

    /// <summary>Matches the benchmark-side constant.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        @"PinnedModelRevision\s*=\s*""(?<revision>[0-9a-f]{40})""",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex HarnessRevision();

    /// <summary>Matches the identity the cache keys on.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        @"ModelIdentity\s*=(?<body>[^;]*);",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex IdentityAssignment();

    /// <summary>The workflow's pin and the cache's pin are one value.</summary>
    [Fact]
    public void TheWorkflowAndTheCacheAgreeOnTheModelRevision()
    {
        var workflow = WorkflowRevision().Match(
            TestProject.ReadWorkflowCommands(TestProject.WorkflowPath("nightly.yml")));

        Assert.True(
            workflow.Success,
            "nightly.yml no longer sets MINILM_REVISION to a 40-character hex revision, so this " +
            "guard cannot compare it to anything. Either the workflow stopped pinning the model — " +
            "which is its own defect, since it verifies a SHA-256 against that pin — or the name " +
            "changed and this test needs updating with it.");

        var harness = HarnessRevision().Match(ReadHarness());

        Assert.True(
            harness.Success,
            HarnessPath + " no longer declares PinnedModelRevision as a 40-character hex literal, " +
            "so the revision the embedding cache keys on cannot be read.");

        var fromWorkflow = workflow.Groups["revision"].Value;
        var fromHarness = harness.Groups["revision"].Value;

        Assert.True(
            string.Equals(fromWorkflow, fromHarness, StringComparison.Ordinal),
            "The model revision nightly.yml fetches and the one the embedding cache keys on have " +
            "drifted apart: workflow " + fromWorkflow + ", harness " + fromHarness + ". Whichever " +
            "is newer, the other is producing or reusing vectors from a different export of the " +
            "model. That is the defect #607 was filed for and the reason the revision is in the " +
            "key at all. Bump both, and expect every embedding cache to go cold: an entry stores " +
            "the digest and the floats, never the text, so nothing can be re-keyed in place.");
    }

    /// <summary>The identity uses the constant rather than merely sitting beside it.</summary>
    /// <remarks>
    /// Asserted because the check above passes perfectly well on an identity string that ignores
    /// the revision entirely. Agreement between two constants nothing consumes is not the property
    /// this file protects.
    /// </remarks>
    [Fact]
    public void TheCacheIdentityIsBuiltFromThatRevision()
    {
        var assignment = IdentityAssignment().Match(ReadHarness());

        Assert.True(
            assignment.Success,
            HarnessPath + " no longer declares ModelIdentity in a form this guard can read.");

        Assert.True(
            assignment.Groups["body"].Value.Contains("PinnedModelRevision", StringComparison.Ordinal),
            "ModelIdentity no longer incorporates PinnedModelRevision, so the revision has stopped " +
            "reaching the cache key even though the two constants still agree. EmbeddingCache " +
            "hashes this identity with the text and nothing else, so a revision the identity does " +
            "not contain cannot distinguish one export's vectors from another's — which is the " +
            "whole of #607.");
    }

    /// <summary>Reads the harness source.</summary>
    /// <returns>The file text.</returns>
    private static string ReadHarness() => File.ReadAllText(
        Path.Combine(
            TestProject.FindRepositoryRoot(),
            HarnessPath.Replace('/', Path.DirectorySeparatorChar)));
}
