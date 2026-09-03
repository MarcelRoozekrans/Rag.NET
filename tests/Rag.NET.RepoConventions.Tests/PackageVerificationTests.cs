using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every package under <c>src/</c> declares how it has actually been verified, in a
/// <c>&lt;VerifiedBy&gt;</c> property in its csproj: <c>unit</c>, <c>integration</c>,
/// <c>container</c>, <c>benchmark</c>, <c>recorded</c>, <c>live</c>, or <c>none</c> — and, since
/// Milestone 6.0, a <c>&lt;VerifiedByReason&gt;</c> beside a <c>unit</c> that says why the package
/// stays there.
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
    /// There are 72 packages under <c>src/</c> today. A far smaller number means the scan lost
    /// the working tree and is asserting over nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausiblePackages = 60;

    /// <summary>
    /// The verification levels, weakest claim last. <c>unit</c>: fakes and fixtures only —
    /// not a failure state, and exactly what late chunking was for five phases.
    /// <c>integration</c>: exercised against something real that is not an external service —
    /// a real file this repository did not generate with the library that reads it, a real
    /// process, a real host over its real transport, a real storage engine surviving a reopen.
    /// <c>benchmark</c>: exercised by a measured run on a real corpus with a real model, pinned in
    /// a reproduction table — Milestone 5's method, and the level five packages earned there.
    /// <c>container</c>: exercised against a real dependency in Docker. <c>recorded</c>: exercised
    /// against a recorded real-service response. <c>live</c>: exercised against the real service.
    /// <c>none</c>: no meaningful test at all — honest, and the release gate's whole subject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>integration</c> was added 2026-08-16, in Phase 6.2, and the gap it fills is the
    /// phase's central finding.</b> §2 of the phase design defines a per-kind bar — a real file for
    /// parsers, a reopen for stores, a real transport for hosted surfaces, an observed effect for
    /// plumbing — and by the time twenty-five packages satisfied it, <b>the ledger had no way to
    /// say so</b>. Every one of them was stuck at bare <c>unit</c>, which was by then a false
    /// statement: <c>unit</c> means "fakes and fixtures only", and these packages start real
    /// processes, parse files written by Microsoft Word and CPython, and reopen real SQLite
    /// databases.
    /// </para>
    /// <para>
    /// The alternatives were both dishonest. <c>container</c> claims Docker, which none of them
    /// use. <c>benchmark</c> claims a pinned figure on a real corpus, which is Milestone 5's much
    /// higher bar. A <c>&lt;VerifiedByReason&gt;</c> beside <c>unit</c> would have said "here is
    /// why this package cannot be exercised" about twenty-five packages that <i>are</i> exercised —
    /// the escape hatch used as a filing cabinet, which is how a ledger becomes fiction.
    /// </para>
    /// <para>
    /// The level is deliberately weaker than <c>container</c> and <c>benchmark</c>, and deliberately
    /// stronger than <c>unit</c>. It does not mean "well tested". It means <b>the thing under test
    /// was not replaced by a substitute of itself</b> — which is the exact distinction every defect
    /// in this repository's record turned on.
    /// </para>
    /// </remarks>
    private static readonly string[] VerificationLevels =
        ["unit", "integration", "container", "benchmark", "recorded", "live", "none"];

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

    /// <summary>
    /// The packages allowed to sit at a bare <c>unit</c> — no better level, no
    /// <c>&lt;VerifiedByReason&gt;</c> — each with the Milestone 6 phase that owes it a real run.
    /// </summary>
    /// <remarks>
    /// <b>This list is Milestone 6's work list, written as the thing that fails.</b> Phase 6.0 (the
    /// re-plan, <c>docs/plans/2026-08-15-milestone-6-battle-tested-replan.md</c>) classified every
    /// package: five earned <c>benchmark</c> from Milestone 5's measurements and left this list on
    /// the day it was written; the rest are here, each under the phase that supplies its run —
    /// <b>6.1</b> for packages that talk to a live service (a recording, or a
    /// <c>&lt;VerifiedByReason&gt;</c> naming the service and the gap), <b>6.2</b> for packages with
    /// no external dependency (one real file, one real store, one real run), <b>6.2.1</b> for the
    /// retrieval techniques and answer engines (a pinned figure with a control). A package leaves
    /// by climbing above <c>unit</c> or by gaining a reason; either makes its entry stale and
    /// <see cref="EveryPackageAllowedToStayUnitIsStillBareUnit"/> fails until the entry is deleted.
    /// A bare <c>unit</c> not on this list fails <see cref="NoPackageStaysAtBareUnit"/> outright —
    /// so a new package cannot arrive unverified and unowned. The Definition of Done requires the
    /// list to reach empty.
    /// <para>
    /// <b>Corrected 2026-08-16, twice, at the start of Phase 6.2</b>
    /// (<c>docs/plans/2026-08-16-milestone-6-2-raise-the-floor-design.md</c> §0).
    /// <b>First correction:</b> ten entries said <i>"the E2E suite"</i>, which reads as <i>the run
    /// already exists and only the ledger is behind</i>. That names the wrong suite —
    /// <c>Rag.NET.E2ETests</c> references exactly six projects (<c>Rag.NET</c>,
    /// <c>AnswerEngines</c>, <c>Graph</c>, <c>GraphRag</c>, <c>VectorStores.PgVector</c>,
    /// <c>Testing</c>) and none of the ten is among them.
    /// <b>Second correction, to the first:</b> the conclusion drawn from that — <i>"nine of the ten
    /// have no real host coverage"</i> — was itself false. Six of them run a real
    /// <c>TestServer</c> in their own test projects, under <c>Integration/</c> subdirectories that
    /// the first check could not see because it globbed <c>tests/&lt;project&gt;/*.cs</c>, which
    /// does not recurse. <b>Four</b> genuinely owe a run: <c>Cli</c>, <c>Hosting</c>, <c>Mcp</c>,
    /// <c>Mcp.Tool</c>.
    /// <para>
    /// Both errors are the same error, and it is the one this ledger exists to catch: a claim about
    /// verification, asserted from an incomplete look. 6.0 consulted its memory; the first
    /// correction consulted one directory. <b>The guard is what caught both</b> — the allowlist
    /// forces every package to name its owner in prose that can be read and falsified, where an
    /// empty ledger field would have hidden all of it. Every entry below now cites the file that
    /// substantiates it, or the recursive search that found nothing.
    /// </para>
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> PackagesAllowedToStayUnit = new(StringComparer.Ordinal)
    {
        // ── 6.1 Recorded Responses: talks to a live service ─────────────────────────────────
        ["Rag.NET.DataProviders.Airtable"] = "6.1 — Airtable API",
        ["Rag.NET.DataProviders.Asana"] = "6.1 — Asana API",
        ["Rag.NET.DataProviders.Bitbucket"] = "6.1 — Bitbucket API",
        ["Rag.NET.DataProviders.Box"] = "6.1 — Box API",
        ["Rag.NET.DataProviders.Confluence"] = "6.1 — Confluence API",
        ["Rag.NET.DataProviders.Dropbox"] = "6.1 — Dropbox API",
        ["Rag.NET.DataProviders.GitLab"] = "6.1 — GitLab API",
        ["Rag.NET.DataProviders.Gmail"] = "6.1 — Gmail API",
        ["Rag.NET.DataProviders.GoogleDrive"] = "6.1 — Google Drive API",
        ["Rag.NET.DataProviders.Jira"] = "6.1 — Jira API",
        ["Rag.NET.DataProviders.Linear"] = "6.1 — Linear API",
        ["Rag.NET.DataProviders.Microsoft365"] = "6.1 — Microsoft Graph",
        ["Rag.NET.DataProviders.Notion"] = "6.1 — Notion API",
        ["Rag.NET.DataProviders.Slack"] = "6.1 — Slack API",
        ["Rag.NET.DataProviders.Zendesk"] = "6.1 — Zendesk API",
        ["Rag.NET.Reranking.Cohere"] = "6.1 — Cohere Rerank API",
        ["Rag.NET.WebSearch.Tavily"] = "6.1 — Tavily API",

        // ── 6.2 Raise the Floor: no external dependency; one real file / store / run ─────────
        ["Rag.NET.Chunking.Templates"] = "6.2 — a real document of each template's kind",

        // ── 6.2.1 Retrieval & Answer Sweep: a pinned figure with a control ───────────────────
    };

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
    [Fact]
    public void NoPackageStaysAtBareUnit()
    {
        // Milestone 6's release gate, and the twin of NoPackageIsVerifiedByNothing one level up. A
        // package at `unit` is honest — fakes and fixtures — and it is exactly the state
        // Rag.NET.GraphRag was in when running it once found eight defects, so `unit` alone is no
        // longer a resting state: it needs either a better level or a <VerifiedByReason> saying why
        // it cannot have one, or it needs an owner in PackagesAllowedToStayUnit. Listed packages
        // are reported as a skip with the phase that owes them; an unlisted bare `unit` fails, so
        // nothing new arrives unverified and unowned.
        var packages = DiscoverPackages();
        var bare = CollectPackagesAtBareUnit(packages);

        foreach (var package in packages)
        {
            if (package.Reason is not null)
            {
                Assert.True(
                    package.Reason.Length > 0,
                    $"{package.Name} declares an empty <VerifiedByReason> in {package.RelativePath}. " +
                    "A reason names the service or dependency and what stays unverified without " +
                    "it; an empty one is a bare `unit` wearing a hat.");
            }
        }

        foreach (var name in bare)
        {
            Assert.True(
                PackagesAllowedToStayUnit.ContainsKey(name),
                $"{name} declares <VerifiedBy>unit</VerifiedBy> with no <VerifiedByReason>, and " +
                $"{nameof(PackagesAllowedToStayUnit)} has no entry for it. Either exercise it for " +
                "real and raise the level (container, benchmark, recorded, live), or add a " +
                "<VerifiedByReason> naming what it talks to and what stays unverified, or list it " +
                "here under the Milestone 6 phase that owes it a run. What must not happen is a " +
                "package at bare `unit` with no owner and nothing going red — that is how a package " +
                "marked Done ships with eight defects.");
        }

        Assert.SkipWhen(
            bare.Count > 0,
            $"{bare.Count} of {packages.Count} packages are at bare <VerifiedBy>unit</VerifiedBy>, " +
            $"each owned by a Milestone 6 phase in {nameof(PackagesAllowedToStayUnit)}: " +
            string.Join(", ", bare) + ". The Definition of Done requires zero before v1.0.");
    }

    [Fact]
    public void EveryPackageAllowedToStayUnitIsStillBareUnit()
    {
        // The staleness guard: the moment a listed package climbs above `unit`, gains a
        // <VerifiedByReason>, or leaves src/, its entry is stale, and a stale entry is a hole in
        // the release gate, so it fails here until it is deleted. This is what turns the allowlist
        // into a work list that shrinks rather than a list that grows furniture.
        var packages = DiscoverPackages();
        var bare = new HashSet<string>(CollectPackagesAtBareUnit(packages), StringComparer.Ordinal);

        foreach (var (name, owner) in PackagesAllowedToStayUnit)
        {
            Assert.True(
                bare.Contains(name),
                $"{name} is listed in {nameof(PackagesAllowedToStayUnit)} ({owner}) but is no " +
                "longer at bare `unit` — it climbed a level, gained a <VerifiedByReason>, or left " +
                "src/. Delete its entry: the list must say only what is still true.");
        }
    }

    private static List<string> CollectPackagesAtBareUnit(IReadOnlyList<Package> packages)
    {
        var bare = new List<string>();

        foreach (var package in packages)
        {
            if (package.Declarations.Count == 1 &&
                string.Equals(package.Declarations[0], "unit", StringComparison.Ordinal) &&
                string.IsNullOrEmpty(package.Reason))
            {
                bare.Add(package.Name);
            }
        }

        return bare;
    }

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
                var root = XDocument.Load(projectFile).Root!;
                var declarations = new List<string>();
                foreach (var property in root.Elements("PropertyGroup").Elements("VerifiedBy"))
                {
                    declarations.Add(property.Value.Trim());
                }

                string? reason = null;
                foreach (var property in root.Elements("PropertyGroup").Elements("VerifiedByReason"))
                {
                    reason = property.Value.Trim();
                }

                packages.Add(new Package(
                    Path.GetFileName(directory),
                    Path.GetRelativePath(repositoryRoot, projectFile).Replace('\\', '/'),
                    declarations,
                    reason));
            }
        }

        return packages;
    }

    /// <summary>
    /// One package as it sits on disk: its <paramref name="Name"/>, the csproj's
    /// <paramref name="RelativePath"/>, and every <c>&lt;VerifiedBy&gt;</c> value it
    /// <paramref name="Declarations"/> — the well-formed case being exactly one.
    /// </summary>
    private sealed record Package(string Name, string RelativePath, IReadOnlyList<string> Declarations, string? Reason);
}
