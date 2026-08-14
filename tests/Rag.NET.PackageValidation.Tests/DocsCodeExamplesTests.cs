using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Guards docs/ against the same defect <see cref="PackageReadmeTests"/> guards package READMEs
/// against: documentation and code agreeing with each other and both being wrong. Phase 4.5 found
/// three of these on <c>docs/getting-started.md</c> alone — a missing package in the install list,
/// two methods that do not exist on the pinned <c>Microsoft.Extensions.AI.OpenAI</c>, and a
/// namespace confused with its package id — on the one page every new user reads first, in a
/// repository that already had a working extractor for exactly this shape of defect sitting
/// unused one directory over. Nothing had ever checked the other docs pages.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="ApiSurfaceCatalog"/> verbatim: the same four extraction shapes, the same
/// <see cref="MetadataReader"/>-based harvesting of what a package actually ships, read straight
/// from the produced <c>.nupkg</c> files — never the working tree, never assembly loading. See
/// that class's remarks for the extraction rule stated precisely, including the two leniencies
/// docs need that a README rarely does (locally-declared example types; string-literal
/// blanking) and the one shape disabled for docs only (a lowercase-local member chain), and what
/// is deliberately left unchecked regardless.
/// </para>
/// <para>
/// <b>What differs from the README case.</b> A package README may only use what that package
/// ships (plus its dependency closure), so <see cref="PackageReadmeTests"/> resolves each README
/// against its own package's closure. A docs page can legitimately use anything the project
/// ships — <c>docs/getting-started.md</c> alone spans <c>Rag.NET</c>,
/// <c>Rag.NET.VectorStores.PgVector</c>, and <c>Rag.NET.Parsers.Pdf</c> in one walkthrough — so
/// this class resolves every fence against <em>every</em> produced package's surface at once, via
/// <see cref="ApiSurfaceCatalog.BuildCatalogFromPackages"/> given the whole discovered set rather
/// than one package's closure. There is no must-touch-its-own-API requirement here, because there
/// is no single "its own" package for a docs page to belong to.
/// </para>
/// <para>
/// <b>The allow-list.</b> Even a full transitive dependency closure and locally-declared-type
/// leniency leave a handful of references that are correct code the resolvable set will never
/// contain on principle: a package a consumer adds themselves that no produced package depends on
/// even transitively (xUnit's <c>Assert</c> in a "verify it this way" snippet; a provider SDK
/// type in a provider-agnostic example, mirroring how getting-started.md itself has the reader
/// add <c>Microsoft.Extensions.AI.OpenAI</c>). Each is named in
/// <see cref="AllowedExternalReferences"/> with its reason, following the
/// <c>PackagesAllowedToDeclareNone</c> precedent in <c>PackageVerificationTests</c>: a listed
/// reference is skipped by the main gate, and
/// <see cref="EveryAllowedExternalReferenceStillFails"/> independently re-derives the raw failure
/// set and fails the moment a listed entry stops appearing in it — resolved because the catalog
/// widened, or because the docs changed — so a stale entry cannot sit unnoticed. Keeping the list
/// short is deliberate: growth past a handful is a signal the resolvable set needs widening
/// again, not that the list needs to be longer.
/// </para>
/// <para>
/// <b>What is in the set, and why each is.</b> Every <c>.md</c> <em>and</em> <c>.mdx</c> under
/// <c>docs/</c>, plus the repository root <c>README.md</c>. Both additions close holes this
/// guard shipped with, found in issue #194 and both the same mistake — deciding what to check by
/// where a file sits rather than by who reads it:
/// <list type="bullet">
/// <item><c>.mdx</c> was excluded by extension alone. The walk asked for <c>*.md</c>, so
/// <c>docs/guide/mcp.mdx</c> — Docusaurus's JSX flavour, an ordinary published page whose
/// <c>```csharp</c> fences this extractor reads exactly as it reads any other page's — was
/// checked by nothing, while <c>src/Rag.NET.Mcp/README.md</c> pointed readers at it as "a
/// complete, working HTTP host".</item>
/// <item>The root <c>README.md</c> was checked by nothing at all, and uniquely so:
/// <see cref="PackageReadmeTests"/> reads each README from inside the produced <c>.nupkg</c> and
/// <see cref="PackageReadmeTests.EveryPackageShipsItsOwnReadme"/> <em>forbids</em> a package
/// shipping the root one, so no package ever carries it into that guard's reach; and this class
/// walked only <c>docs/</c>. The project's most-read page fell through the gap between the two
/// guards precisely because each was doing its job.</item>
/// </list>
/// </para>
/// <para>
/// <b>Not in the set: <c>src/*/README.md</c>.</b> Already covered, and covered harder.
/// <c>Directory.Build.props</c> packs each project's own README into its own package, so every
/// one of them is read by
/// <see cref="PackageReadmeTests.EveryReadmeExampleResolvesAgainstTheAssembly"/> — against that
/// package's own dependency closure, which is a strictly narrower resolvable set than the
/// every-produced-package catalog this class uses. Walking them here as well would add a second,
/// weaker check of the same bytes and a second place for an allow-list entry to have to live.
/// The correspondence is exact in both directions, not assumed: every <c>src/*/README.md</c>
/// belongs to a packed package and every packed package has one.
/// </para>
/// <para>
/// <c>docs/plans/</c> is excluded: those are dated historical design records, not live
/// documentation, and their snippets can describe code that never shipped or was later removed.
/// </para>
/// <para>
/// <b>The covered set is itself asserted.</b> Widening the walk closes the two holes #194 found;
/// it does nothing about the third one nobody has opened yet, because until now no test said
/// which files were supposed to be checked — a walk that quietly narrows goes green exactly as
/// convincingly as a walk that covers everything, which is how both holes survived every review
/// this guard has had. <see cref="EveryPublishedDocumentationFileIsCheckedBySomething"/> states
/// the covered set as a fact and fails on anything outside it, deriving "ought to be checked"
/// from a deliberately wider markdown-family extension list than the walk's own, so the two
/// cannot agree with each other by construction.
/// </para>
/// <para>
/// Discovery and skip behaviour are shared with <see cref="ProducedPackageTests"/>: no
/// <c>artifacts/packages</c> means nothing has packed and the test skips, and
/// <see cref="WorkflowWiringTests"/> pins ci.yml so that skip cannot rot into permanent green.
/// </para>
/// <para>
/// <b>What this does not catch, demonstrated rather than theorised.</b> Resolution is by
/// <i>name</i>: a member reference passes when something in the shipped surface declares that
/// name. It does not know which type an expression has, because that needs a compiler rather than
/// a catalogue. So <c>response.Text</c> passed for months in <c>docs/guide/memory.md</c> —
/// <c>IRagPipeline.AskAsync</c> returns <see cref="Rag.NET.Models.RagResponse"/>, whose property
/// is <c>Answer</c>, but <c>Text</c> exists on <c>TextChunk</c> and <c>DocumentSection</c>, so the
/// name resolved and the snippet still would not compile. It was found by a user in issue #56,
/// after this guard had already run green over that exact file.
/// </para>
/// <para>
/// The honest summary is that this catches <i>names that exist nowhere</i> — a renamed or deleted
/// API, a package id used as a namespace, a method invented for a tutorial — and not <i>names on
/// the wrong type</i>. Closing that gap means compiling each fence against the packages, which is
/// a different and much larger tool.
/// </para>
/// <para>
/// <b>That gap is not hypothetical, and the count is now known.</b> This paragraph used to end
/// "it is worth building only if wrong-type references turn out to be common, and one instance is
/// not evidence of that". They turned out to be common. Widening the walk for #194 was paired
/// with an audit of every live page against the current API, and it found <b>ten</b> snippets
/// that name only real members and still would not compile — the one <c>response.Text</c> above
/// (issue #56's defect, fixed in <c>docs/guide/memory.md</c> and never on the front page: #193),
/// and nine sites where the <c>IDictionary&lt;string, string&gt;</c> →
/// <c>IDictionary&lt;string, MetadataValue&gt;</c> migration stopped short (#192). Every one of
/// the nine writes <c>new Dictionary&lt;string, string&gt;</c> into a <c>Tags</c> or
/// <c>MetadataFilter</c> that is now typed on <see cref="Rag.NET.Models.MetadataValue"/>. Nothing
/// in the shape scan can see it: <c>Dictionary</c> is a real type, <c>Tags</c> and
/// <c>MetadataFilter</c> are real members, and the generic arguments are the keyword
/// <c>string</c>, which the extractor skips as not-PascalCase. So the guard reads the fence,
/// resolves every name in it, and passes — which is why the migration could stall in nine
/// published places without one red run.
/// </para>
/// <para>
/// <b>Why type checking is still not built here, stated as a decision rather than an
/// oversight.</b> Not because the gap is small — it is the larger half — but because a catalogue
/// cannot be grown into a compiler by degrees. These fences are snippets, not compilation units:
/// <c>var response = await pipeline.AskAsync(…)</c> never declares <c>pipeline</c>, and the
/// dominant docs shape is a fragment whose surrounding context the reader supplies. Type-checking
/// them means a Roslyn compilation per fence, a synthesized enclosing context per fence, a
/// resolved <c>MetadataReference</c> set built from the same packages, and a policy for the
/// undeclared identifiers that are correct documentation today — a tool of a different order from
/// <see cref="ApiSurfaceCatalog"/>, and one whose false-positive budget would have to be argued
/// out on its own terms. It is the right next tool; it is not a widening of this one, and pairing
/// it with the widening would have buried both. Recorded here, with its evidence and its size, so
/// the next person reads this guard's green as the narrower claim it is and has the case for
/// building the other half already made.
/// </para>
/// </remarks>
public sealed class DocsCodeExamplesTests
{
    /// <summary>The docs subdirectory excluded from the check — see the class remarks.</summary>
    private const string ExcludedDirectoryName = "plans";

    /// <summary>
    /// References that are correct code but can never resolve against the produced packages'
    /// surface on principle. Two shapes, both a page choosing not to fully specify something on
    /// purpose rather than a defect: (1) a package the reader adds themselves, exactly as
    /// getting-started.md has them add <c>Microsoft.Extensions.AI.OpenAI</c>, that no produced
    /// package references even transitively (xUnit's <c>Assert</c>, an OpenTelemetry exporter
    /// package, a provider SDK type); (2) a reader-implemented type a page deliberately leaves
    /// abstract — never declared anywhere on the page, unlike the fully-declared examples the
    /// locally-declared-type leniency resolves — because a full implementation would be
    /// disproportionate to the point being made (a quickstart keeping to six steps; one more
    /// content-type example reusing a pattern already shown in full; a whole custom
    /// <c>IRagPipeline</c> a shadow-mode page has no reason to write out). Keyed by
    /// <c>"{relativePath}: {failure message}."</c> (copy the tail of a failed run's line, minus
    /// its line number) so an entry is traceable to exactly the fact it records. See the class
    /// remarks for the staleness guard this list is checked against.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedExternalReferences = new(StringComparer.Ordinal)
    {
        ["docs/getting-started.md: 'MyCustomParser' is not a public type in the package, its dependency closure, or the shared framework."] =
            "A quickstart generic-argument placeholder for 'add any IDocumentParser' — never declared on this deliberately short page; extending.md carries the full implementation walkthrough (XmlDocumentParser, resolved via the locally-declared-type leniency).",

        ["docs/guide/evaluation.md: 'Assert.True' starts at 'Assert', which is neither a public type nor a namespace in the package, its dependency closure, or the shared framework."] =
            "xUnit's Assert, in a 'using it in a CI gate' example — xUnit is a test-only dependency of this repository's own test projects, never a dependency of any produced package.",

        ["docs/guide/extending.md: 'MyCustomChunkingStrategy' is not a public type in the package, its dependency closure, or the shared framework."] =
            "The RagBuilder.Services advanced-registration example's placeholder for a constructor-injected chunking strategy, distinct from the page's fully-declared SentenceChunkingStrategy example (whose parameterless constructor would not fit this factory pattern) — writing out a second full IChunkingStrategy here would dilute a section that is about the DI-factory pattern, not chunking algorithms.",

        ["docs/guide/ingestion.md: 'ContentTypes.Contains' starts at 'ContentTypes', which is neither a public type nor a namespace in the package, its dependency closure, or the shared framework."] =
            "MyXmlParser's own static ContentTypes property, referenced unqualified within that same class — a locally-declared *member*, not a locally-declared *type*, so the type-declaration leniency (class/record/struct/interface only) does not reach it.",

        ["docs/guide/ingestion.md: 'MyCsvParser' is not a public type in the package, its dependency closure, or the shared framework."] =
            "A one-line replaces: example reusing the custom-parser pattern MyXmlParser already demonstrates in full a few paragraphs earlier on this page; not redeclared here to avoid repeating that implementation twice.",

        ["docs/guide/ingestion.md: 'MyExcelParser' is not a public type in the package, its dependency closure, or the shared framework."] =
            "Same as MyCsvParser immediately above: a one-line replacesTypeNames: example reusing MyXmlParser's already-shown pattern.",

        ["docs/guide/observability.md: '.AddConsoleExporter(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "OpenTelemetry.Exporter.Console's extension method — an exporter package the reader chooses and adds themselves; Rag.NET.Telemetry depends only on OpenTelemetry + OpenTelemetry.Extensions.Hosting, no specific exporter.",

        ["docs/guide/observability.md: '.AddApplicationInsightsTelemetry(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "Microsoft.ApplicationInsights.AspNetCore's extension method — same reasoning as AddConsoleExporter above: an optional exporter the reader adds themselves.",

        ["docs/guide/observability.md: '.AddOtlpExporter(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "OpenTelemetry.Exporter.OpenTelemetryProtocol's extension method — same reasoning as AddConsoleExporter above.",

        ["docs/guide/resilience.md: 'OpenAIChatClient' is not a public type in the package, its dependency closure, or the shared framework."] =
            "A provider-agnostic fallback-chain example's illustrative client type, from Microsoft.Extensions.AI.OpenAI — a package the reader adds themselves, exactly as getting-started.md's own step 1 does, and no produced package depends on.",

        ["docs/guide/resilience.md: 'AnthropicChatClient' is not a public type in the package, its dependency closure, or the shared framework."] =
            "Same example as OpenAIChatClient immediately above: a second provider's illustrative client type from a third-party Anthropic SDK package this repository does not depend on.",

        ["docs/guide/resilience.md: 'AnthropicClient' is not a public type in the package, its dependency closure, or the shared framework."] =
            "The constructor argument for AnthropicChatClient immediately above — same third-party Anthropic SDK package.",

        ["docs/guide/retrieval.md: '.AddStackExchangeRedisCache(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "Microsoft.Extensions.Caching.StackExchangeRedis's extension method — an IDistributedCache backend the reader chooses; Rag.NET.Caching depends only on Microsoft.Extensions.Caching.Hybrid, no specific backend.",

        ["docs/guide/shadow-mode.md: 'CandidatePipeline' is not a public type in the package, its dependency closure, or the shared framework."] =
            "UseShadow<TSecondary>'s placeholder — 'a whole pipeline, built however the variant under evaluation differs' per the surrounding prose; a real implementation would be a full IRagPipeline, disproportionate to a page about the shadow-mode wrapper itself.",

        ["docs/reference/opentelemetry.md: '.AddOtlpExporter(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "Same OpenTelemetry.Exporter.OpenTelemetryProtocol method as observability.md's entry above, on this page's own two examples of it.",

        ["docs/reference/opentelemetry.md: '.AddPrometheusExporter(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "OpenTelemetry.Exporter.Prometheus.AspNetCore's extension method — an optional exporter the reader adds themselves.",

        ["docs/reference/opentelemetry.md: '.MapPrometheusScrapingEndpoint(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "Same OpenTelemetry.Exporter.Prometheus.AspNetCore package as AddPrometheusExporter immediately above.",

        ["docs/reference/opentelemetry.md: '.AddConsoleExporter(…)' matches no public method (extension methods included) in the package, its dependency closure, or the shared framework."] =
            "Same OpenTelemetry.Exporter.Console package as observability.md's entry above, on this page's own example.",

        ["docs/reference/opentelemetry.md: 'Assert.Equal' starts at 'Assert', which is neither a public type nor a namespace in the package, its dependency closure, or the shared framework."] =
            "xUnit's Assert, in an ActivityListener unit-test example — same reasoning as evaluation.md's Assert.True entry above.",
    };

    [Fact]
    public void EveryDocsCodeExampleResolvesAgainstTheProducedPackages()
    {
        var raw = CollectRawFailures();
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (file, line, message) in raw)
        {
            if (AllowedExternalReferences.ContainsKey(AllowListKey(file, message)))
            {
                continue;
            }

            if (seen.Add($"{file}:{line}:{message}"))
            {
                failures.Add($"{file}:{line}: {message}.");
            }
        }

        Assert.True(
            failures.Count == 0,
            ApiSurfaceCatalog.DescribeFailures(
                "Every C# example on a docs page must resolve against what the produced " +
                "packages actually ship (their assemblies, their transitive dependency " +
                "closures, the shared framework) — docs referencing APIs nothing ships is this " +
                "repository's dominant, repeatedly-found defect, and getting-started.md is the " +
                "page every new user follows first. The extraction rule is in " +
                "ApiSurfaceCatalog's remarks; judge failures against it. A reference that is " +
                "correct but structurally unresolvable (a package the reader installs " +
                "separately) belongs in AllowedExternalReferences, not a docs rewrite.",
                failures));
    }

    [Fact]
    public void EveryAllowedExternalReferenceStillFails()
    {
        // The staleness guard, same discipline as PackageVerificationTests' allow-list: recomputes
        // the raw failure set independently (never trusts state from the other Fact) and fails the
        // moment a listed entry no longer appears in it — the catalog widened enough to resolve it,
        // or the docs no longer say it, either way the entry is now fiction and must be deleted.
        var rawKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, _, message) in CollectRawFailures())
        {
            _ = rawKeys.Add(AllowListKey(file, message));
        }

        foreach (var (key, reason) in AllowedExternalReferences)
        {
            Assert.True(
                rawKeys.Contains(key),
                $"'{key}' is listed in {nameof(AllowedExternalReferences)} ({reason}) but no " +
                "longer appears in the raw failure set — resolved, by a widened catalog or an " +
                "edited doc. Delete the entry: an allow-list nothing re-checks is how a defect " +
                "that got fixed becomes a defect nobody notices came back.");
        }
    }

    /// <summary>
    /// Every extension the markdown family is written under, deliberately wider than
    /// <see cref="DocumentationExtensions"/> and deliberately not derived from it. This is the
    /// list <see cref="EveryPublishedDocumentationFileIsCheckedBySomething"/> uses to decide what
    /// <em>ought</em> to be checked; if the two were the same list, that test would be asking the
    /// walk to agree with itself and would have passed just as green through #194's holes.
    /// </summary>
    private static readonly HashSet<string> MarkdownFamilyExtensions =
        new([".md", ".mdx", ".markdown", ".mdown"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryPublishedDocumentationFileIsCheckedBySomething()
    {
        // The guard for the guards, and the one #194 actually needed. Widening the walk to take
        // in .mdx and the root README fixes two holes; it does nothing to stop a third opening,
        // because nothing anywhere asserted which files are checked — a walk that quietly narrows
        // reports success exactly as loudly as a walk that covers everything. So the covered set
        // is stated here as a fact about the repository rather than left implicit in an
        // enumeration pattern: every markdown-family file a reader can reach must be checked by
        // this class or, for a package README, by PackageReadmeTests via the package it ships in.
        var repositoryRoot = ProducedPackageTests.FindRepositoryRoot();
        var packagedIds = ApiSurfaceCatalog.MapPackagesById(ProducedPackageTests.DiscoverPackages());

        var walked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in DiscoverDocFiles(repositoryRoot))
        {
            _ = walked.Add(RelativePath(repositoryRoot, file));
        }

        var failures = new List<string>();
        CheckDocsTreeIsWalked(repositoryRoot, walked, failures);
        CheckRootReadmeIsWalked(repositoryRoot, walked, failures);
        CheckPackageReadmesArePacked(repositoryRoot, packagedIds, failures);

        Assert.True(
            failures.Count == 0,
            ApiSurfaceCatalog.DescribeFailures(
                "Every markdown-family file a reader can reach must be checked by some guard: " +
                "the docs/ tree and the repository root README.md by this class, each " +
                "src/*/README.md by PackageReadmeTests via the package that ships it. A file " +
                "reachable by neither is documentation nothing verifies — the state " +
                "docs/guide/mcp.mdx and README.md were both in until #194, each for its own " +
                "reason, neither of them announced. Add the file to the walk, or pack the " +
                "project that owns it; do not narrow this test.",
                failures));
    }

    /// <summary>
    /// Asserts every markdown-family file under <c>docs/</c> outside <c>docs/plans/</c> is walked.
    /// Uses <see cref="MarkdownFamilyExtensions"/>, not the walk's own extension list, so an
    /// extension the walk stops asking for fails here instead of silently dropping its pages —
    /// which is exactly how <c>.mdx</c> went unchecked.
    /// </summary>
    private static void CheckDocsTreeIsWalked(
        string repositoryRoot, HashSet<string> walked, List<string> failures)
    {
        var docsRoot = Path.Combine(repositoryRoot, "docs");
        var excludedRoot = $"{Path.Combine(docsRoot, ExcludedDirectoryName)}{Path.DirectorySeparatorChar}";

        foreach (var file in Directory.EnumerateFiles(docsRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase) ||
                !MarkdownFamilyExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            var relativePath = RelativePath(repositoryRoot, file);
            if (!walked.Contains(relativePath))
            {
                failures.Add(
                    $"{relativePath}: a documentation page under docs/ that this class's walk " +
                    "does not reach, so none of its C# examples is checked against anything.");
            }
        }
    }

    private static void CheckRootReadmeIsWalked(
        string repositoryRoot, HashSet<string> walked, List<string> failures)
    {
        if (!walked.Contains("README.md"))
        {
            failures.Add(
                "README.md: the repository root README is not in the walk. No package may ship " +
                "it (PackageReadmeTests.EveryPackageShipsItsOwnReadme forbids exactly that), so " +
                "if this class does not read it from the working tree, nothing reads it at all — " +
                "which is how the project's most-read page carried issue #56's defect on line " +
                "104 long after the same defect was fixed in docs/.");
        }
        else if (!File.Exists(Path.Combine(repositoryRoot, "README.md")))
        {
            failures.Add("README.md: the walk names it but the repository has no such file.");
        }
    }

    /// <summary>
    /// Asserts every <c>src/*/README.md</c> is reachable by <see cref="PackageReadmeTests"/> —
    /// which means its project packs, since that class reads READMEs from inside the produced
    /// <c>.nupkg</c> and never from the working tree. A README whose project stopped packing is
    /// still a page on GitHub and would be checked by nothing at all, the same shape of hole as
    /// the root README's, and this class deliberately does not paper over it by walking
    /// <c>src/</c> too: package READMEs resolve against their own package's closure there, which
    /// is stricter than the every-package catalog here.
    /// </summary>
    private static void CheckPackageReadmesArePacked(
        string repositoryRoot, Dictionary<string, string> packagedIds, List<string> failures)
    {
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        foreach (var projectDirectory in Directory.EnumerateDirectories(sourceRoot))
        {
            var readme = Path.Combine(projectDirectory, "README.md");
            if (!File.Exists(readme))
            {
                continue;
            }

            var id = Path.GetFileName(projectDirectory);
            if (!packagedIds.ContainsKey(id))
            {
                failures.Add(
                    $"{RelativePath(repositoryRoot, readme)}: no produced package has the id " +
                    $"'{id}', so PackageReadmeTests never sees this README and nothing checks " +
                    "its examples.");
            }
        }
    }

    private static string RelativePath(string repositoryRoot, string filePath) =>
        Path.GetRelativePath(repositoryRoot, filePath).Replace('\\', '/');

    /// <summary>
    /// Runs the full scan — every non-excluded docs file, every fence, every extracted reference,
    /// resolved against every produced package — without applying the allow-list. Both
    /// <see cref="EveryDocsCodeExampleResolvesAgainstTheProducedPackages"/> and
    /// <see cref="EveryAllowedExternalReferenceStillFails"/> call this independently so neither
    /// trusts state the other computed.
    /// </summary>
    /// <returns>Every unresolved reference: its page, its fence's starting line, and the failure
    /// message <see cref="ApiSurfaceCatalog.ResolveFailure"/> produced for it.</returns>
    private static List<(string File, int Line, string Message)> CollectRawFailures()
    {
        var packages = ProducedPackageTests.DiscoverPackages();
        var byId = ApiSurfaceCatalog.MapPackagesById(packages);
        var catalog = ApiSurfaceCatalog.BuildCatalogFromPackages(packages, byId);
        var repositoryRoot = ProducedPackageTests.FindRepositoryRoot();

        var raw = new List<(string, int, string)>();
        foreach (var file in DiscoverDocFiles(repositoryRoot))
        {
            CollectFileFailures(file, repositoryRoot, catalog, raw);
        }

        return raw;
    }

    private static void CollectFileFailures(
        string filePath,
        string repositoryRoot,
        ApiSurfaceCatalog.CatalogSet catalog,
        List<(string File, int Line, string Message)> raw)
    {
        var markdown = File.ReadAllText(filePath);
        var fences = ApiSurfaceCatalog.ExtractCsharpFences(markdown);
        if (fences.Count == 0)
        {
            return; // Nothing for shape-extraction to check; see ApiSurfaceCatalog's remarks.
        }

        var relativePath = RelativePath(repositoryRoot, filePath);

        // Declared types accumulate across every fence on the page: extending.md's pattern
        // throughout is "implement in one fence, register in the next" — see ApiSurfaceCatalog's
        // remarks.
        var declaredTypes = ApiSurfaceCatalog.ExtractDeclaredTypeNames(
            string.Concat(fences.Select(fence => fence.Code)));

        foreach (var fence in fences)
        {
            var references = ApiSurfaceCatalog.ExtractReferences(
                fence.Code, catalog, declaredTypes, checkLowercaseLocalChains: false);

            foreach (var reference in references)
            {
                var failure = ApiSurfaceCatalog.ResolveFailure(reference, catalog);
                if (failure is not null)
                {
                    raw.Add((relativePath, fence.StartLine, failure));
                }
            }
        }
    }

    private static string AllowListKey(string relativePath, string message) =>
        $"{relativePath}: {message}.";

    /// <summary>
    /// The documentation extensions walked under <c>docs/</c>. <c>.mdx</c> is Docusaurus's
    /// JSX-in-markdown flavour — its <c>```csharp</c> fences are ordinary fences and its pages are
    /// ordinary published documentation, so extension is no reason to check one and not the other.
    /// </summary>
    private static readonly string[] DocumentationExtensions = ["*.md", "*.mdx"];

    /// <summary>
    /// Finds every documentation file to check: every <c>.md</c> and <c>.mdx</c> under
    /// <c>docs/</c> with <c>docs/plans/</c> excluded, plus the repository root
    /// <c>README.md</c> — see the class remarks for why each is in the set and why
    /// <c>src/*/README.md</c> is not.
    /// </summary>
    /// <param name="repositoryRoot">The repository root, as found by
    /// <see cref="ProducedPackageTests.FindRepositoryRoot"/>.</param>
    /// <returns>The absolute paths of the files to check, sorted for stable output.</returns>
    private static List<string> DiscoverDocFiles(string repositoryRoot)
    {
        var docsRoot = Path.Combine(repositoryRoot, "docs");
        var excludedRoot = $"{Path.Combine(docsRoot, ExcludedDirectoryName)}{Path.DirectorySeparatorChar}";
        var files = new List<string>();

        foreach (var pattern in DocumentationExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(docsRoot, pattern, SearchOption.AllDirectories))
            {
                if (!file.StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }
        }

        // The repository root README: the project's front page, read by more people than any
        // docs/ page, and — because PackageReadmeTests forbids a package shipping it — the one
        // documentation file no guard reached before.
        files.Add(Path.Combine(repositoryRoot, "README.md"));

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
