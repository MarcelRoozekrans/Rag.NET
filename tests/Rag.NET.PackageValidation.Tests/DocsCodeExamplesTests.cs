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
/// <c>docs/plans/</c> is excluded: those are dated historical design records, not live
/// documentation, and their snippets can describe code that never shipped or was later removed.
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
/// a different and much larger tool; it is worth building only if wrong-type references turn out
/// to be common, and one instance is not evidence of that. Recorded so the next person reads this
/// guard's green as the narrower claim it is.
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

        var relativePath = Path.GetRelativePath(repositoryRoot, filePath).Replace('\\', '/');

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
    /// Finds every markdown file under <c>docs/</c>, <c>docs/plans/</c> excluded — see the class
    /// remarks for why.
    /// </summary>
    /// <param name="repositoryRoot">The repository root, as found by
    /// <see cref="ProducedPackageTests.FindRepositoryRoot"/>.</param>
    /// <returns>The absolute paths of the markdown files to check, sorted for stable output.</returns>
    private static List<string> DiscoverDocFiles(string repositoryRoot)
    {
        var docsRoot = Path.Combine(repositoryRoot, "docs");
        var excludedRoot = $"{Path.Combine(docsRoot, ExcludedDirectoryName)}{Path.DirectorySeparatorChar}";
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories))
        {
            if (!file.StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
