using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Pins the two security-documentation facts that can rot without anyone editing the documents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Origin.</b> Phase 6.2.38's design claimed the repo-conventions documentation guards "already
/// run against <c>docs/guide/</c>". They do not, and nothing else does either:
/// <see cref="DocumentationQualityTests"/> parses XML <c>&lt;summary&gt;</c> blocks under
/// <c>src/Rag.NET.Abstractions</c>, and <see cref="DocumentedConstraintGuardTests"/> reads
/// <c>*Options.cs</c> doc comments. Guide markdown had no gate at all, so the posture document
/// landed unprotected — a claim about tooling that nobody checked, inside a document arguing for
/// checking claims.
/// </para>
/// <para>
/// <b>What these can and cannot do.</b> No test decides whether prose is true or good; the posture
/// is verified by reading it against the code. These pin the two mechanical facts instead — that a
/// disclosure channel still exists, and that the posture's package list still covers every package
/// whose security remit it draws a boundary around. The second matters most because **it breaks
/// when somebody adds a package**, which is the one moment nobody is looking at a security
/// document.
/// </para>
/// </remarks>
public sealed class SecurityDocumentationTests
{
    private static string PostureDocument =>
        Path.Combine(TestProject.FindRepositoryRoot(), "docs", "guide", "security.md");

    /// <summary>
    /// 71 packages are published on nuget.org. A researcher with a finding needs somewhere private
    /// to send it, or the first the project hears of a vulnerability is a public issue.
    /// </summary>
    [Fact]
    public void TheRepositoryCarriesADisclosurePolicy()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), "SECURITY.md");

        Assert.True(
            File.Exists(path),
            $"SECURITY.md is missing from {path}. 71 packages are published on nuget.org; without "
            + "it a reporter has no private channel and GitHub shows no 'Report a vulnerability' "
            + "button.");

        Assert.Contains(
            "advisor",
            File.ReadAllText(path),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The posture section draws a boundary around the packages with a security remit, so the list
    /// has to be the actual list. This fails when a new <c>Rag.NET.Security*</c>,
    /// <c>Rag.NET.Mcp*</c> or <c>Rag.NET.Api*</c> package appears and the boundary is not extended
    /// to cover it.
    /// </summary>
    /// <remarks>
    /// The expected set is derived from the filesystem rather than hardcoded. A hardcoded list
    /// would be a second place to forget, and would keep passing for exactly the change this test
    /// exists to catch.
    /// </remarks>
    [Fact]
    public void ThePostureAccountsForEverySecurityAdjacentPackage()
    {
        var posture = File.ReadAllText(PostureDocument);

        var securityAdjacent = Directory
            .GetDirectories(Path.Combine(TestProject.FindRepositoryRoot(), "src"))
            .Select(Path.GetFileName)
            .Where(name =>
                name is not null
                && (name.StartsWith("Rag.NET.Security", StringComparison.Ordinal)
                    || name.StartsWith("Rag.NET.Mcp", StringComparison.Ordinal)
                    || name.StartsWith("Rag.NET.Api", StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(securityAdjacent);

        var unnamed = securityAdjacent
            .Where(name => !posture.Contains(name!, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unnamed.Count == 0,
            "docs/guide/security.md states a boundary around the packages carrying a security "
            + "remit, so its list has to be the whole list. These exist under src/ and are not "
            + "named anywhere in the document, which makes the boundary a claim about an "
            + "incomplete set: "
            + string.Join(", ", unnamed));
    }

    /// <summary>
    /// The posture's sharpest claim is that RBAC fails open, and it quotes the sentence the RBAC
    /// section already carries so the two cannot drift. If the quoted sentence stops appearing
    /// twice, either the quote or the original has been reworded and one of them is now wrong.
    /// </summary>
    [Fact]
    public void ThePostureQuotesTheRbacDefaultVerbatimFromTheSectionBelowIt()
    {
        const string Claim =
            "Chunks that do not carry the key are world-readable and pass through for every caller";

        var occurrences = File.ReadAllText(PostureDocument).Split(Claim).Length - 1;

        Assert.True(
            occurrences >= 2,
            $"Expected the RBAC fail-open sentence to appear at least twice in security.md — once "
            + $"quoted in the posture section and once in the RBAC section it summarises — but "
            + $"found {occurrences}. The posture quotes it rather than paraphrasing so a reworded "
            + "default cannot leave the summary silently stale.");
    }
}
