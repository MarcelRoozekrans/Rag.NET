using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every package under <c>src/</c> declares how it has actually been verified, in a
/// <c>&lt;VerifiedBy&gt;</c> property in its csproj: <c>unit</c>, <c>container</c>,
/// <c>recorded</c>, <c>live</c>, or <c>none</c>.
/// </summary>
/// <remarks>
/// <para>
/// Milestone 4's old Definition of Done was fully satisfied while four real defects were live —
/// late chunking sat inert from Phase 1.1 until Phase 3.7, and OnnxReranker destroyed 26% of every
/// document as <c>[UNK]</c> — because nothing recorded what "tested" meant for each shipped
/// package. This ledger does for <c>src/</c> what <see cref="TestProjectTierTests"/> already does
/// for <c>tests/</c>, and it extends the same convention: the csproj already carries
/// <c>RequiresDocker</c>/<c>RequiresSecrets</c>/<c>RequiresLlm</c> for CI to select on, so the
/// verification level belongs beside them rather than in a side file that can drift.
/// </para>
/// <para>
/// Two gates, and the distinction is the point. <b>Declaration</b> is hard-failing: a package with
/// no value is unaccounted for. <b>Release</b> — no package at <c>none</c> — deliberately does not
/// fail the build: <c>none</c> is an honest declaration of a real state, and if declaring it broke
/// the build everyone would write <c>unit</c> instead and the ledger would become fiction. The
/// whole value here is the honesty of the values, so the mechanism must never punish honesty. The
/// release gate reports instead — the count and the list — and the Definition of Done carries the
/// requirement that the count reach zero.
/// </para>
/// </remarks>
public sealed class PackageVerificationTests
{
    /// <summary>
    /// There are 71 packages under <c>src/</c> today. A far smaller number means the scan lost
    /// the working tree and is asserting over nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausiblePackages = 60;

    /// <summary>
    /// The verification levels, weakest claim last. <c>unit</c>: fakes and fixtures only —
    /// not a failure state, and exactly what late chunking was for five phases. <c>container</c>:
    /// exercised against a real dependency in Docker. <c>recorded</c>: exercised against a
    /// recorded real-service response. <c>live</c>: exercised against the real service.
    /// <c>none</c>: no meaningful test at all — honest, and the release gate's whole subject.
    /// </summary>
    private static readonly string[] VerificationLevels =
        ["unit", "container", "recorded", "live", "none"];

    private readonly ITestOutputHelper _output;

    public PackageVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TheScanFindsEveryPackageInTheRepository()
    {
        var packages = DiscoverPackages();

        Assert.True(
            packages.Count >= FewestPlausiblePackages,
            $"Found only {packages.Count} packages under src/, expected at least " +
            $"{FewestPlausiblePackages}. A ledger that scans nothing passes for the wrong " +
            "reason, so this fails instead.");
    }

    [Fact]
    public void EveryPackageDeclaresHowItHasBeenVerified()
    {
        // The declaration gate, and it is hard-failing on purpose: a package with no VerifiedBy
        // is unaccounted for, and unaccounted-for is the state this phase exists to end. Note
        // what it does not do — it never demands a value better than `none`. Judging the values
        // is the release gate's job, precisely so that declaring the truth is always safe.
        foreach (var package in DiscoverPackages())
        {
            Assert.True(
                package.Declarations.Count > 0,
                $"{package.Name} declares no <VerifiedBy> in {package.RelativePath}. Every " +
                "package must say how it has actually been verified — one of " +
                $"{AllowedValuesList()} — in a <PropertyGroup>, beside the RequiresDocker/" +
                "RequiresSecrets/RequiresLlm convention ci.yml already selects on. If the " +
                "honest answer is 'none', declare none: that is a recordable state, not a " +
                "build failure.");

            Assert.True(
                package.Declarations.Count == 1,
                $"{package.Name} declares <VerifiedBy> {package.Declarations.Count} times in " +
                $"{package.RelativePath} ({string.Join(", ", package.Declarations)}). One " +
                "package has one verification level; several declarations make the ledger " +
                "ambiguous. Keep exactly one.");

            Assert.True(
                IsKnownLevel(package.Declarations[0]),
                $"{package.Name} declares <VerifiedBy>{package.Declarations[0]}</VerifiedBy> " +
                $"in {package.RelativePath}, which is not a verification level. Use exactly " +
                $"one of {AllowedValuesList()}, lowercase — an unknown value is a claim " +
                "nothing can interpret, and the ledger only works if its values mean the " +
                "same thing everywhere.");
        }
    }

    [Fact]
    public void NoPackageIsVerifiedByNothing()
    {
        // The release gate, not the declaration gate. It must not fail the build today: `none`
        // is the honest description of a real state, and punishing the declaration would only
        // teach people to write `unit` instead — a green ledger made of fiction, which is
        // precisely the failure mode this phase exists to end. So it reports: the distribution,
        // the count, and the list, visible in the run as a skip rather than a pass. The
        // Definition of Done carries the release requirement that this reach zero, at which
        // point this test starts passing and begins guarding against regression by itself.
        var packages = DiscoverPackages();
        var unverified = new List<string>();

        foreach (var package in packages)
        {
            if (package.Declarations.Count == 1 &&
                string.Equals(package.Declarations[0], "none", StringComparison.Ordinal))
            {
                unverified.Add(package.Name);
            }
        }

        ReportDistribution(packages);

        Assert.SkipWhen(
            unverified.Count > 0,
            $"{unverified.Count} of {packages.Count} packages declare " +
            $"<VerifiedBy>none</VerifiedBy>: {string.Join(", ", unverified)}. Recording that " +
            "honestly is this phase's deliverable; getting each above `none` is later " +
            "Milestone 4 work, and the Definition of Done requires zero before release.");
    }

    /// <summary>
    /// Prints how many packages sit at each level — this phase's headline number. Undeclared
    /// packages are counted too, so the report stays truthful while the declaration gate above
    /// is still red.
    /// </summary>
    /// <param name="packages">Every package the scan found.</param>
    private void ReportDistribution(IReadOnlyList<Package> packages)
    {
        _output.WriteLine($"Verification distribution across {packages.Count} packages:");

        foreach (var level in VerificationLevels)
        {
            var names = new List<string>();
            foreach (var package in packages)
            {
                if (package.Declarations.Count == 1 &&
                    string.Equals(package.Declarations[0], level, StringComparison.Ordinal))
                {
                    names.Add(package.Name);
                }
            }

            _output.WriteLine($"  {level}: {names.Count}" +
                (names.Count > 0 ? $" ({string.Join(", ", names)})" : string.Empty));
        }

        var undeclared = 0;
        foreach (var package in packages)
        {
            if (package.Declarations.Count == 0)
            {
                undeclared++;
            }
        }

        _output.WriteLine($"  (undeclared): {undeclared}");
    }

    private static bool IsKnownLevel(string value)
    {
        foreach (var level in VerificationLevels)
        {
            if (string.Equals(level, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string AllowedValuesList() => string.Join(", ", VerificationLevels);

    /// <summary>
    /// Discovers every package under <c>src/</c> — the same <c>src/*/*.csproj</c> shape
    /// <see cref="TestProject.SourceProjectsMissingFromTheSolution"/> scans, so the two guards
    /// never disagree about which packages exist.
    /// </summary>
    /// <returns>The packages with their <c>VerifiedBy</c> declarations, in directory order.</returns>
    private static IReadOnlyList<Package> DiscoverPackages()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var packages = new List<Package>();

        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(repositoryRoot, "src")))
        {
            foreach (var projectFile in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                var declarations = new List<string>();
                foreach (var property in XDocument.Load(projectFile).Root!
                    .Elements("PropertyGroup").Elements("VerifiedBy"))
                {
                    declarations.Add(property.Value.Trim());
                }

                packages.Add(new Package(
                    Path.GetFileName(directory),
                    Path.GetRelativePath(repositoryRoot, projectFile).Replace('\\', '/'),
                    declarations));
            }
        }

        return packages;
    }

    /// <summary>
    /// One package as it sits on disk: its <paramref name="Name"/>, the csproj's
    /// <paramref name="RelativePath"/>, and every <c>&lt;VerifiedBy&gt;</c> value it
    /// <paramref name="Declarations"/> — the well-formed case being exactly one.
    /// </summary>
    private sealed record Package(string Name, string RelativePath, IReadOnlyList<string> Declarations);
}
