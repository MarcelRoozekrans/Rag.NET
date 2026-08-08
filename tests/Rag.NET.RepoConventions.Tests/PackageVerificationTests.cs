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
/// no value is unaccounted for. <b>Release</b> — no package at <c>none</c> — works like the
/// <c>FeatureClaimTests.KnownFalseClaims</c> and <c>TestGateTests</c> ledgers: every package
/// currently at <c>none</c> is pinned by name in
/// <see cref="PackagesAllowedToDeclareNone"/> with its reason and owning phase, a package
/// declaring <c>none</c> without an entry fails, and a staleness test fails the moment a listed
/// package climbs above <c>none</c> — so an entry cannot outlive the state it records.
/// Declaring <c>none</c> honestly stays possible and stays safe: add the entry with the reason
/// and the owner, which is one commented line, not a punishment. What is <i>not</i> possible is
/// declaring it silently — if declaring <c>none</c> broke the build outright everyone would
/// write <c>unit</c> instead and the ledger would become fiction, but an unreviewable
/// <c>none</c> is the other way a ledger becomes fiction. The listed packages are reported as a
/// skip — the count and the list, visible in every run — and the Definition of Done carries the
/// requirement that the list reach empty.
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

    /// <summary>
    /// The packages currently allowed to declare <c>none</c>, each with its reason and owning
    /// phase. A package declaring <c>none</c> without an entry here fails
    /// <see cref="NoPackageIsVerifiedByNothing"/>; an entry whose package stops declaring
    /// <c>none</c> fails <see cref="EveryPackageAllowedToDeclareNoneStillDeclaresNone"/> until
    /// it is deleted. Adding an entry is how a new package declares <c>none</c> honestly — one
    /// commented line with a reason and an owner, reviewable in the diff.
    /// </summary>
    private static readonly Dictionary<string, string> PackagesAllowedToDeclareNone =
        new(StringComparer.Ordinal);

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
        // The release gate, not the declaration gate. Every package at `none` must be pinned in
        // PackagesAllowedToDeclareNone with its reason and owner: a listed `none` is honest and
        // reported as a skip — punishing it would only teach people to write `unit` instead, a
        // green ledger made of fiction — but an unlisted `none` fails, so a package cannot
        // regress to `none` (or arrive at it) without a reviewable ledger entry. When the list
        // is empty this skip's condition is never true — any `none` fails the allow-list
        // assertion first — so the test becomes a plain, passing "no package declares none"
        // assertion with no code change. The Definition of Done requires exactly that state
        // before release.
        var packages = DiscoverPackages();
        var unverified = CollectPackagesDeclaringNone(packages);

        ReportDistribution(packages);

        foreach (var name in unverified)
        {
            Assert.True(
                PackagesAllowedToDeclareNone.ContainsKey(name),
                $"{name} declares <VerifiedBy>none</VerifiedBy> and " +
                $"{nameof(PackagesAllowedToDeclareNone)} has no entry for it. If `none` is the " +
                "honest state, record it there with the reason and the owning phase — that is " +
                "one commented line, and honesty must stay that cheap. What must not happen is " +
                "a package sitting at `none` with no owner and nothing going red, which is how " +
                "unverified code becomes furniture.");
        }

        Assert.SkipWhen(
            unverified.Count > 0,
            $"{unverified.Count} of {packages.Count} packages declare " +
            $"<VerifiedBy>none</VerifiedBy>: {string.Join(", ", unverified)}. Each is recorded " +
            $"in {nameof(PackagesAllowedToDeclareNone)} with its owner; getting each above " +
            "`none` is later Milestone 4 work, and the Definition of Done requires zero before " +
            "release.");
    }

    [Fact]
    public void EveryPackageAllowedToDeclareNoneStillDeclaresNone()
    {
        // The staleness guard, same discipline as the sibling ledgers: an allow-list nothing
        // re-checks is how a known defect becomes furniture. The moment a listed package climbs
        // above `none` — or leaves src/ — its entry is stale, and a stale entry is a hole in
        // the release gate, so it fails here until it is deleted.
        var unverified = CollectPackagesDeclaringNone(DiscoverPackages());

        foreach (var (name, reason) in PackagesAllowedToDeclareNone)
        {
            Assert.True(
                unverified.Contains(name),
                $"{name} no longer declares <VerifiedBy>none</VerifiedBy>, so its entry in " +
                $"{nameof(PackagesAllowedToDeclareNone)} is stale. Delete the entry so " +
                $"{nameof(NoPackageIsVerifiedByNothing)} guards against the package regressing " +
                $"to none. It was recorded because: {reason}");
        }
    }

    /// <summary>
    /// Collects the names of the packages whose single declaration is <c>none</c> — malformed
    /// declarations are the declaration gate's subject, not the release gate's.
    /// </summary>
    /// <param name="packages">Every package the scan found.</param>
    /// <returns>The package names, in directory order.</returns>
    private static List<string> CollectPackagesDeclaringNone(IReadOnlyList<Package> packages)
    {
        var unverified = new List<string>();

        foreach (var package in packages)
        {
            if (package.Declarations.Count == 1 &&
                string.Equals(package.Declarations[0], "none", StringComparison.Ordinal))
            {
                unverified.Add(package.Name);
            }
        }

        return unverified;
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
