using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Guards the per-package READMEs against this repository's dominant defect: documentation and
/// code agreeing with each other and both being wrong. Reads every README from inside the
/// produced <c>.nupkg</c> — what ships, never the working tree.
/// </summary>
/// <remarks>
/// <para>
/// Sixty-six packages each carrying a hand-written README is sixty-six new homes for the defect
/// found seven-plus times here already — most recently features.md telling consumers to set
/// <c>&lt;EnableOcr&gt;true&lt;/EnableOcr&gt;</c> in their own project file, which is impossible
/// against a compiled package. So the guard exists before any per-package README does: every
/// README written for Tasks 13–14 is written against a check that already works. Three checks:
/// the README is the package's own (not the repo-wide file every package ships today), it names
/// its own package id in its install command, and every API its C# examples reference exists as
/// a public member of what the package actually ships.
/// </para>
/// <para>
/// <b>The extraction rule</b>, stated so a future reader can judge whether the guard is real.
/// From each <c>```csharp</c> fence, after stripping <c>/* */</c> blocks and line comments whose
/// <c>//</c> is at line start or preceded by whitespace (so <c>https://</c> inside strings
/// survives), four shapes are extracted and each must resolve:
/// <list type="number">
/// <item><c>using X.Y.Z;</c> — X.Y.Z must be a namespace (or namespace prefix) some resolvable
/// assembly declares; <c>using static X.Y.T;</c> — X.Y a namespace, T a public type.</item>
/// <item><c>new T(…)</c> / <c>new T{…}</c> / <c>new T[…]</c> / <c>new T&lt;G&gt;(…)</c> — T must
/// be a public type, and each PascalCase single-identifier generic argument must be too.</item>
/// <item>Dotted chains. A chain starting with a PascalCase identifier that is a known namespace
/// prefix is skipped as a qualified name (stated leniency: its later segments go unchecked).
/// Otherwise a PascalCase head must be a public type and the next segment a public
/// <em>static</em> member of a type with that simple name. A lowercase head (a local) or a
/// leading-dot fluent continuation (<c>.UseXxx(…)</c>) checks every PascalCase segment against
/// the public member names of the resolvable set; the final segment, when followed by
/// <c>(</c>, must be a public method name — extension methods resolve here, being public
/// static methods.</item>
/// <item>Assignments <c>Name = …</c> where Name is PascalCase (not <c>==</c>/<c>=&gt;</c>) —
/// the object-initializer shape — Name must be a public member name.</item>
/// </list>
/// A README containing C# fences must additionally reference at least one public API declared in
/// the package's own assembly, so a plausible-looking example about something else cannot pass.
/// </para>
/// <para>
/// <b>The resolvable set</b> for a package: the assemblies it ships (<c>lib/</c> and
/// <c>tools/</c>), the shipped assemblies of every produced package in its nuspec dependency
/// closure, each closure member's external nuspec dependencies read from the NuGet
/// global-packages cache at the declared minimum version (nearest cached version when that exact
/// one is absent — a cache miss narrows the resolvable surface and can only make the check
/// stricter), the shared runtime framework, and any <c>frameworkReference</c> shared frameworks
/// the nuspec names. Assemblies are read with <see cref="MetadataReader"/> straight from the
/// package zip: no assembly loading, no package code execution, no file locking, and no resolver
/// closure to break on external types — the reasons it was preferred over
/// <c>MetadataLoadContext</c>, which needs every transitive reference materialized on disk.
/// </para>
/// <para>
/// <b>Deliberately unchecked</b> (they need semantic analysis, not name existence): lowercase
/// identifiers, bare PascalCase identifiers outside the shapes above (named arguments like
/// <c>Question:</c>), segments after a namespace-qualified head, nested or dotted generic
/// arguments, and whether members sit on the <em>right</em> type when a simple name exists on
/// several. Recorded as possible later strengthening, deliberately not built now: compiling each
/// fence against the package's actual assemblies. READMEs with no C# fence (a dotnet tool
/// documented in shell commands) have nothing for shape-extraction to check; the install-command
/// and own-README checks still hold them.
/// </para>
/// <para>
/// Discovery and skip behaviour are shared with <see cref="ProducedPackageTests"/>: no
/// <c>artifacts/packages</c> means nothing has packed and the tests skip, and
/// <see cref="WorkflowWiringTests"/> pins ci.yml so that skip cannot rot into permanent green.
/// </para>
/// </remarks>
public sealed partial class PackageReadmeTests
{
    /// <summary>
    /// How many individual failures a message lists before summarising the rest — with all 66
    /// packages failing at once (the expected state until Tasks 13–14 write the READMEs), an
    /// uncapped message would bury the shape of the failure under its volume.
    /// </summary>
    private const int MostFailuresShown = 20;

    [Fact]
    public void EveryPackageShipsItsOwnReadme()
    {
        var rootReadme = File.ReadAllBytes(
            Path.Combine(ProducedPackageTests.FindRepositoryRoot(), "README.md"));
        var failures = new List<string>();

        foreach (var package in ProducedPackageTests.DiscoverPackages())
        {
            var name = Path.GetFileName(package);
            var readme = ReadReadmeBytes(package);

            if (readme is null)
            {
                failures.Add($"{name}: contains no README.md entry.");
            }
            else if (BytesEqual(readme, rootReadme))
            {
                failures.Add($"{name}: ships a README byte-identical to the repository root README.md.");
            }
        }

        Assert.True(
            failures.Count == 0,
            Describe(
                "Every package must ship its own README: the repo-wide README.md shows every " +
                "nuget.org visitor the whole project instead of the package they are looking " +
                "at, which is the consumer confusion this phase exists to end. " +
                "Directory.Build.props packs the root README.md into every package today; " +
                "Tasks 13-14 of the package-decomposition plan replace it per package.",
                failures));
    }

    [Fact]
    public void EveryReadmeNamesItsOwnPackageId()
    {
        var failures = new List<string>();

        foreach (var package in ProducedPackageTests.DiscoverPackages())
        {
            var nuspec = ProducedPackageTests.ReadNuspec(package);
            var id = ReadNuspecValue(nuspec, "id");

            Assert.False(
                string.IsNullOrEmpty(id),
                $"{Path.GetFileName(package)} has a nuspec with no <id> element — not a valid package.");

            var readme = ReadReadmeText(package);
            if (readme is null)
            {
                continue; // EveryPackageShipsItsOwnReadme reports the missing file.
            }

            CheckInstallCommand(
                Path.GetFileName(package), id!, readme, IsDotnetToolPackage(nuspec), failures);
        }

        Assert.True(
            failures.Count == 0,
            Describe(
                "Every README's install command must name that package's exact id, read from " +
                "the nuspec — this is what stops one templated README being pasted 66 times " +
                "with nobody noticing that none of them installs the package it sits in.",
                failures));
    }

    [Fact]
    public void EveryReadmeExampleResolvesAgainstTheAssembly()
    {
        var packages = ProducedPackageTests.DiscoverPackages();
        var byId = MapPackagesById(packages);
        var failures = new List<string>();

        foreach (var package in packages)
        {
            CheckReadmeExamples(package, byId, failures);
        }

        Assert.True(
            failures.Count == 0,
            Describe(
                "Every C# example in a package README must resolve against what that package " +
                "actually ships (its assemblies, its dependency closure, the shared " +
                "framework) — docs referencing APIs the installed package does not have is " +
                "this repository's dominant, seven-times-found defect. The extraction rule is " +
                "in this class's remarks; judge failures against it.",
                failures));
    }

    private static void CheckInstallCommand(
        string name, string id, string readme, bool isTool, List<string> failures)
    {
        // A dotnet tool is installed with `dotnet tool install`, a library with `dotnet add
        // package` — decided by the packageTypes the nuspec declares, never by a list of ids.
        var pattern = isTool ? ToolInstallCommand() : AddPackageCommand();
        var verb = isTool ? "dotnet tool install" : "dotnet add package";
        var namedIds = new List<string>();

        foreach (Match match in pattern.Matches(readme))
        {
            namedIds.Add(match.Groups["id"].Value);
        }

        if (namedIds.Count == 0)
        {
            failures.Add(
                $"{name}: its README contains no `{verb}` line at all, so a reader cannot " +
                "install what the page describes.");
            return;
        }

        foreach (var candidate in namedIds)
        {
            if (string.Equals(candidate, id, StringComparison.Ordinal))
            {
                return;
            }
        }

        failures.Add(
            $"{name}: its README's `{verb}` line(s) name [{string.Join(", ", namedIds)}] but " +
            $"never '{id}', the id this package actually publishes under — the templated-README " +
            "shape this test exists to stop.");
    }

    private static void CheckReadmeExamples(
        string packagePath, Dictionary<string, string> byId, List<string> failures)
    {
        var name = Path.GetFileName(packagePath);
        var readme = ReadReadmeText(packagePath);
        if (readme is null)
        {
            return; // EveryPackageShipsItsOwnReadme reports the missing file.
        }

        var fences = ExtractCsharpFences(readme);
        if (fences.Count == 0)
        {
            return; // Nothing for shape-extraction to check; see the class remarks.
        }

        var catalog = ResolutionCatalogs.GetOrAdd(packagePath, path => BuildCatalog(path, byId));
        var own = OwnCatalogs.GetOrAdd(packagePath, HarvestOwnAssembly);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var touchesOwnApi = false;

        foreach (var fence in fences)
        {
            foreach (var reference in ExtractReferences(fence, catalog))
            {
                var failure = ResolveFailure(reference, catalog);
                if (failure is not null && seen.Add(failure))
                {
                    failures.Add($"{name}: {failure}.");
                }

                touchesOwnApi = touchesOwnApi || TouchesOwnApi(reference, own);
            }
        }

        if (!touchesOwnApi)
        {
            failures.Add(
                $"{name}: none of its C# examples reference any public API declared in the " +
                "package's own assembly — the examples demonstrate something other than this " +
                "package.");
        }
    }

    /// <summary>
    /// Extracts the API references of one code fence per the rule in the class remarks. Takes the
    /// catalog only to recognise namespace-qualified chains; every emitted reference is still
    /// resolved separately by <see cref="ResolveFailure"/>.
    /// </summary>
    /// <param name="fence">The fence's code text.</param>
    /// <param name="catalog">The package's resolvable set.</param>
    /// <returns>The extracted references.</returns>
    private static List<ApiReference> ExtractReferences(string fence, CatalogSet catalog)
    {
        var references = new List<ApiReference>();
        var code = StripCodeComments(fence);
        code = ExtractUsingDirectives(code, references);
        AddObjectCreationReferences(code, references);
        AddInitializerReferences(code, references);
        AddChainReferences(code, catalog, references);
        return references;
    }

    private static string ExtractUsingDirectives(string code, List<ApiReference> references)
    {
        // Matched directives are removed from the text so the chain scan below cannot re-read
        // `using Rag.NET.Models;` as a static-member chain and report the one defect twice with
        // the worse message.
        return UsingDirective().Replace(code, match =>
        {
            var target = match.Groups["target"].Value;
            if (match.Groups["static"].Success)
            {
                var split = target.LastIndexOf('.');
                references.Add(new ApiReference(
                    ReferenceKind.Type, split < 0 ? target : target[(split + 1)..]));
                if (split > 0)
                {
                    references.Add(new ApiReference(ReferenceKind.Namespace, target[..split]));
                }
            }
            else
            {
                references.Add(new ApiReference(ReferenceKind.Namespace, target));
            }

            return string.Empty;
        });
    }

    private static void AddObjectCreationReferences(string code, List<ApiReference> references)
    {
        foreach (Match match in ObjectCreation().Matches(code))
        {
            references.Add(new ApiReference(ReferenceKind.Type, match.Groups["type"].Value));
            AddGenericArgumentReferences(match.Groups["generics"].Value, references);
        }
    }

    private static void AddInitializerReferences(string code, List<ApiReference> references)
    {
        foreach (Match match in InitializerAssignment().Matches(code))
        {
            references.Add(new ApiReference(ReferenceKind.MemberAccess, match.Groups["name"].Value));
        }
    }

    private static void AddChainReferences(
        string code, CatalogSet catalog, List<ApiReference> references)
    {
        foreach (Match match in DottedChain().Matches(code))
        {
            var segments = match.Groups["chain"].Value.Split('.');
            var leadingDot = match.Groups["lead"].Success;
            AddGenericArgumentReferences(match.Groups["generics"].Value, references);

            if (!leadingDot && segments.Length == 1)
            {
                continue; // A bare identifier: a local, a keyword, a parameter — not an API shape.
            }

            var first = 0;
            if (!leadingDot && char.IsUpper(segments[0][0]))
            {
                if (catalog.HasNamespacePrefix(segments[0]))
                {
                    continue; // Namespace-qualified; stated leniency in the class remarks.
                }

                if (!char.IsUpper(segments[1][0]))
                {
                    continue; // Type-dot-lowercase is no shape C# public API takes.
                }

                references.Add(new ApiReference(ReferenceKind.StaticMember, segments[1], segments[0]));
                first = 2;
            }
            else if (!leadingDot)
            {
                first = 1; // Lowercase receiver; its own name is a local, not an API.
            }

            for (var i = first; i < segments.Length; i++)
            {
                if (!char.IsUpper(segments[i][0]))
                {
                    continue;
                }

                var isCall = match.Groups["open"].Success && i == segments.Length - 1;
                references.Add(new ApiReference(
                    isCall ? ReferenceKind.MethodCall : ReferenceKind.MemberAccess, segments[i]));
            }
        }
    }

    private static void AddGenericArgumentReferences(string generics, List<ApiReference> references)
    {
        if (generics.Length == 0)
        {
            return;
        }

        foreach (var token in generics.Split(','))
        {
            var name = token.Trim();
            if (IsSimpleCapitalizedIdentifier(name))
            {
                references.Add(new ApiReference(ReferenceKind.Type, name));
            }
        }
    }

    private static bool IsSimpleCapitalizedIdentifier(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string? ResolveFailure(ApiReference reference, CatalogSet catalog) =>
        reference.Kind switch
        {
            ReferenceKind.Namespace when catalog.HasNamespacePrefix(reference.Name) => null,
            ReferenceKind.Namespace =>
                $"`using {reference.Name};` names a namespace that no assembly the package " +
                "ships or depends on declares",
            ReferenceKind.Type when catalog.HasType(reference.Name) => null,
            ReferenceKind.Type =>
                $"'{reference.Name}' is not a public type in the package, its dependency " +
                "closure, or the shared framework",
            ReferenceKind.StaticMember when !catalog.HasType(reference.DeclaringType!) =>
                $"'{reference.DeclaringType}.{reference.Name}' starts at " +
                $"'{reference.DeclaringType}', which is neither a public type nor a namespace " +
                "in the package, its dependency closure, or the shared framework",
            ReferenceKind.StaticMember
                when catalog.HasStaticMember(reference.DeclaringType!, reference.Name) => null,
            ReferenceKind.StaticMember =>
                $"'{reference.DeclaringType}' has no public static member '{reference.Name}'",
            ReferenceKind.MethodCall when catalog.HasMethod(reference.Name) => null,
            ReferenceKind.MethodCall =>
                $"'.{reference.Name}(…)' matches no public method (extension methods included) " +
                "in the package, its dependency closure, or the shared framework",
            ReferenceKind.MemberAccess when catalog.HasMember(reference.Name) => null,
            _ =>
                $"'.{reference.Name}' matches no public member in the package, its dependency " +
                "closure, or the shared framework",
        };

    private static bool TouchesOwnApi(ApiReference reference, ApiCatalog own) => reference.Kind switch
    {
        ReferenceKind.Namespace => own.NamespacePrefixes.Contains(reference.Name),
        ReferenceKind.Type => own.TypeNames.Contains(reference.Name),
        ReferenceKind.StaticMember => own.TypeNames.Contains(reference.DeclaringType!),
        ReferenceKind.MethodCall => own.MethodNames.Contains(reference.Name),
        _ => own.MemberNames.Contains(reference.Name),
    };

    // ---- Markdown and comment handling -------------------------------------------------------

    private static List<string> ExtractCsharpFences(string markdown)
    {
        var fences = new List<string>();
        StringBuilder? current = null;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (current is null)
            {
                if (line.TrimStart().StartsWith("```csharp", StringComparison.Ordinal))
                {
                    current = new StringBuilder();
                }
            }
            else if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fences.Add(current.ToString());
                current = null;
            }
            else
            {
                _ = current.Append(line).Append('\n');
            }
        }

        return fences;
    }

    private static string StripCodeComments(string code)
    {
        var withoutBlocks = BlockComment().Replace(code, " ");
        var builder = new StringBuilder(withoutBlocks.Length);

        foreach (var line in withoutBlocks.Split('\n'))
        {
            _ = builder.Append(StripLineComment(line)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Removes a <c>//</c> comment only when the marker is at line start or preceded by
    /// whitespace, so <c>https://…</c> inside a string literal survives while ordinary trailing
    /// comments — whose prose would otherwise feed the shape scan — do not.
    /// </summary>
    /// <param name="line">One line of fence code.</param>
    /// <returns>The line with any comment removed.</returns>
    private static string StripLineComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        while (index > 0 && !char.IsWhiteSpace(line[index - 1]))
        {
            index = line.IndexOf("//", index + 1, StringComparison.Ordinal);
        }

        return index < 0 ? line : line[..index];
    }

    // ---- Package reading ---------------------------------------------------------------------

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[]? ReadReadmeBytes(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, "README.md", StringComparison.OrdinalIgnoreCase))
            {
                using var source = entry.Open();
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        return null;
    }

    private static string? ReadReadmeText(string packagePath)
    {
        var bytes = ReadReadmeBytes(packagePath);
        if (bytes is null)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes));
        return reader.ReadToEnd();
    }

    private static string? ReadNuspecValue(XDocument nuspec, string localName)
    {
        foreach (var element in nuspec.Descendants())
        {
            if (string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
            {
                return element.Value;
            }
        }

        return null;
    }

    private static bool IsDotnetToolPackage(XDocument nuspec)
    {
        foreach (var element in nuspec.Descendants())
        {
            if (string.Equals(element.Name.LocalName, "packageType", StringComparison.Ordinal) &&
                string.Equals(element.Attribute("name")?.Value, "DotnetTool", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> MapPackagesById(IReadOnlyList<string> packages)
    {
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            var id = ReadNuspecValue(ProducedPackageTests.ReadNuspec(package), "id");
            if (!string.IsNullOrEmpty(id))
            {
                byId[id!] = package;
            }
        }

        return byId;
    }

    private static List<(string Id, string Version)> ReadDependencies(XDocument nuspec)
    {
        var dependencies = new List<(string, string)>();

        foreach (var element in nuspec.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "dependency", StringComparison.Ordinal))
            {
                continue;
            }

            var id = element.Attribute("id")?.Value;
            var version = MinimumVersion(element.Attribute("version")?.Value);
            if (!string.IsNullOrEmpty(id) && version is not null)
            {
                dependencies.Add((id!, version));
            }
        }

        return dependencies;
    }

    /// <summary>
    /// The lower bound of a nuspec version range: <c>[9.7.0, )</c> and <c>9.7.0</c> both yield
    /// <c>9.7.0</c> — the version the cache most plausibly holds, since this repository pins its
    /// direct dependencies exactly.
    /// </summary>
    /// <param name="range">The nuspec <c>version</c> attribute.</param>
    /// <returns>The minimum version, or null when the range has no usable lower bound.</returns>
    private static string? MinimumVersion(string? range)
    {
        if (string.IsNullOrEmpty(range))
        {
            return null;
        }

        var text = range!.TrimStart('[', '(').Trim();
        var end = text.IndexOfAny([',', ')', ']']);
        if (end >= 0)
        {
            text = text[..end];
        }

        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static List<string> ReadFrameworkReferences(XDocument nuspec)
    {
        var names = new List<string>();

        foreach (var element in nuspec.Descendants())
        {
            if (string.Equals(element.Name.LocalName, "frameworkReference", StringComparison.Ordinal))
            {
                var name = element.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name!);
                }
            }
        }

        return names;
    }

    // ---- Catalog construction ----------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, CatalogSet> ResolutionCatalogs =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ApiCatalog> PackageCatalogs =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ApiCatalog> OwnCatalogs =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ApiCatalog> ExternalCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ApiCatalog> FrameworkCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<ApiCatalog> RuntimeCatalog = new(() =>
        HarvestDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!));

    private static CatalogSet BuildCatalog(string packagePath, Dictionary<string, string> byId)
    {
        var parts = new List<ApiCatalog>();
        var externalDependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frameworkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var closurePackage in CollectProducedClosure(packagePath, byId))
        {
            parts.Add(PackageCatalogs.GetOrAdd(closurePackage, HarvestPackageAssemblies));
            var nuspec = ProducedPackageTests.ReadNuspec(closurePackage);

            foreach (var (dependencyId, version) in ReadDependencies(nuspec))
            {
                if (!byId.ContainsKey(dependencyId))
                {
                    _ = externalDependencies.TryAdd(dependencyId, version);
                }
            }

            foreach (var frameworkName in ReadFrameworkReferences(nuspec))
            {
                _ = frameworkNames.Add(frameworkName);
            }
        }

        foreach (var (dependencyId, version) in externalDependencies)
        {
            parts.Add(ExternalCatalogs.GetOrAdd(
                $"{dependencyId}/{version}", _ => HarvestExternalDependency(dependencyId, version)));
        }

        parts.Add(RuntimeCatalog.Value);
        foreach (var frameworkName in frameworkNames)
        {
            parts.Add(FrameworkCatalogs.GetOrAdd(frameworkName, HarvestSharedFramework));
        }

        return new CatalogSet(parts);
    }

    private static List<string> CollectProducedClosure(
        string packagePath, Dictionary<string, string> byId)
    {
        var closure = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(packagePath);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            closure.Add(current);
            foreach (var (dependencyId, _) in ReadDependencies(ProducedPackageTests.ReadNuspec(current)))
            {
                if (byId.TryGetValue(dependencyId, out var dependencyPath))
                {
                    queue.Enqueue(dependencyPath);
                }
            }
        }

        return closure;
    }

    private static ApiCatalog HarvestPackageAssemblies(string packagePath)
    {
        var catalog = new ApiCatalog();
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (IsShippedAssembly(entry.FullName))
            {
                HarvestZipEntry(entry, catalog);
            }
        }

        return catalog;
    }

    /// <summary>
    /// The catalog of the one assembly that carries the package's own API — <c>{id}.dll</c>
    /// under <c>lib/</c> or <c>tools/</c> — used by the must-reference-own-API check. A dotnet
    /// tool ships its whole dependency graph in <c>tools/</c>, which is exactly why the check
    /// cannot use everything the package contains.
    /// </summary>
    /// <param name="packagePath">The absolute path of the <c>.nupkg</c>.</param>
    /// <returns>The catalog, empty when the package ships no assembly named after its id.</returns>
    private static ApiCatalog HarvestOwnAssembly(string packagePath)
    {
        var id = ReadNuspecValue(ProducedPackageTests.ReadNuspec(packagePath), "id");
        var fileName = id + ".dll";
        var catalog = new ApiCatalog();
        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (IsShippedAssembly(entry.FullName) &&
                string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase))
            {
                HarvestZipEntry(entry, catalog);
            }
        }

        return catalog;
    }

    private static bool IsShippedAssembly(string entryName) =>
        entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        (entryName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) ||
         entryName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase));

    private static void HarvestZipEntry(ZipArchiveEntry entry, ApiCatalog catalog)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        HarvestAssembly(buffer, catalog);
    }

    private static ApiCatalog HarvestExternalDependency(string id, string version)
    {
        var catalog = new ApiCatalog();
        var packageRoot = Path.Combine(GlobalPackagesRoot(), id.ToLowerInvariant());
        if (!Directory.Exists(packageRoot))
        {
            return catalog; // Not cached at all; the resolvable surface just narrows.
        }

        var directory = Path.Combine(packageRoot, version);
        if (!Directory.Exists(directory))
        {
            directory = NewestVersionDirectory(packageRoot) ?? directory;
        }

        foreach (var subdirectory in AssemblySubdirectories)
        {
            var path = Path.Combine(directory, subdirectory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories))
            {
                HarvestFile(file, catalog);
            }
        }

        return catalog;
    }

    private static readonly string[] AssemblySubdirectories = ["lib", "ref"];

    private static string GlobalPackagesRoot() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    private static ApiCatalog HarvestSharedFramework(string frameworkName)
    {
        // The runtime directory is .../shared/Microsoft.NETCore.App/{version}; sibling shared
        // frameworks (Microsoft.AspNetCore.App, for nuspec frameworkReferences) sit two levels up.
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory));
        if (sharedRoot is null)
        {
            return new ApiCatalog();
        }

        var frameworkRoot = Path.Combine(sharedRoot, frameworkName);
        if (!Directory.Exists(frameworkRoot))
        {
            return new ApiCatalog();
        }

        var newest = NewestVersionDirectory(frameworkRoot);
        return newest is null ? new ApiCatalog() : HarvestDirectory(newest);
    }

    private static string? NewestVersionDirectory(string root)
    {
        string? best = null;
        Version? bestVersion = null;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            var dash = name.IndexOf('-');
            var core = dash < 0 ? name : name[..dash];
            if (Version.TryParse(core, out var version) &&
                (bestVersion is null || version > bestVersion))
            {
                bestVersion = version;
                best = directory;
            }
        }

        return best;
    }

    private static ApiCatalog HarvestDirectory(string directory)
    {
        var catalog = new ApiCatalog();

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            HarvestFile(file, catalog);
        }

        return catalog;
    }

    private static void HarvestFile(string path, ApiCatalog catalog)
    {
        using var stream = File.OpenRead(path);
        HarvestAssembly(stream, catalog);
    }

    // ---- Metadata harvesting -----------------------------------------------------------------

    private static void HarvestAssembly(Stream stream, ApiCatalog catalog)
    {
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata)
        {
            return; // A native library; nothing to read.
        }

        var metadata = reader.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            HarvestType(metadata, metadata.GetTypeDefinition(handle), catalog);
        }
    }

    private static void HarvestType(MetadataReader metadata, TypeDefinition type, ApiCatalog catalog)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility != TypeAttributes.Public && visibility != TypeAttributes.NestedPublic)
        {
            return;
        }

        var name = StripGenericArity(metadata.GetString(type.Name));
        _ = catalog.TypeNames.Add(name);

        var declaring = type.GetDeclaringType();
        if (declaring.IsNil)
        {
            catalog.AddNamespace(metadata.GetString(type.Namespace));
        }
        else
        {
            // A nested public type is reachable as Parent.Nested — a static-member shape.
            catalog.AddStaticMember(
                StripGenericArity(metadata.GetString(metadata.GetTypeDefinition(declaring).Name)), name);
        }

        HarvestMethods(metadata, type, name, catalog);
        HarvestFields(metadata, type, name, catalog);
        HarvestProperties(metadata, type, name, catalog);
    }

    private static void HarvestMethods(
        MetadataReader metadata, TypeDefinition type, string typeName, ApiCatalog catalog)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
            {
                continue;
            }

            var name = metadata.GetString(method.Name);
            if (IsAccessorOrOperator(name))
            {
                continue;
            }

            _ = catalog.MethodNames.Add(name);
            _ = catalog.MemberNames.Add(name);
            if (method.Attributes.HasFlag(MethodAttributes.Static))
            {
                catalog.AddStaticMember(typeName, name);
            }
        }
    }

    private static void HarvestFields(
        MetadataReader metadata, TypeDefinition type, string typeName, ApiCatalog catalog)
    {
        foreach (var handle in type.GetFields())
        {
            var field = metadata.GetFieldDefinition(handle);
            if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
            {
                continue;
            }

            var name = metadata.GetString(field.Name);
            _ = catalog.MemberNames.Add(name);

            // Literal covers enum values, which read as static members (RagProvider.OpenAI).
            if (field.Attributes.HasFlag(FieldAttributes.Static) ||
                field.Attributes.HasFlag(FieldAttributes.Literal))
            {
                catalog.AddStaticMember(typeName, name);
            }
        }
    }

    private static void HarvestProperties(
        MetadataReader metadata, TypeDefinition type, string typeName, ApiCatalog catalog)
    {
        foreach (var handle in type.GetProperties())
        {
            var property = metadata.GetPropertyDefinition(handle);
            var accessors = property.GetAccessors();
            var accessorHandle = accessors.Getter.IsNil ? accessors.Setter : accessors.Getter;
            if (accessorHandle.IsNil)
            {
                continue;
            }

            var accessor = metadata.GetMethodDefinition(accessorHandle);
            if ((accessor.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
            {
                continue;
            }

            var name = metadata.GetString(property.Name);
            _ = catalog.MemberNames.Add(name);
            if (accessor.Attributes.HasFlag(MethodAttributes.Static))
            {
                catalog.AddStaticMember(typeName, name);
            }
        }
    }

    private static bool IsAccessorOrOperator(string name) =>
        name.StartsWith("get_", StringComparison.Ordinal) ||
        name.StartsWith("set_", StringComparison.Ordinal) ||
        name.StartsWith("add_", StringComparison.Ordinal) ||
        name.StartsWith("remove_", StringComparison.Ordinal) ||
        name.StartsWith("op_", StringComparison.Ordinal) ||
        name.StartsWith(".", StringComparison.Ordinal);

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    // ---- Failure reporting -------------------------------------------------------------------

    private static string Describe(string headline, List<string> failures)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine(headline);

        var shown = Math.Min(failures.Count, MostFailuresShown);
        for (var i = 0; i < shown; i++)
        {
            _ = builder.Append("  - ").AppendLine(failures[i]);
        }

        if (failures.Count > shown)
        {
            _ = builder.Append("  … and ").Append(failures.Count - shown).Append(" more.");
        }

        return builder.ToString();
    }

    // ---- Regexes (the shapes; see the extraction rule in the class remarks) ------------------

    /// <summary>
    /// Match timeout in milliseconds. The inputs are README-sized and the patterns are linear in
    /// practice; the timeout exists to satisfy MA0009's demand that no regex can run away.
    /// </summary>
    private const int RegexTimeout = 2000;

    [GeneratedRegex(
        @"^[ \t]*using\s+(?<static>static\s+)?(?<target>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;\s*$",
        RegexOptions.ExplicitCapture | RegexOptions.Multiline,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex UsingDirective();

    [GeneratedRegex(
        @"\bnew\s+(?<type>[A-Z]\w*)\s*(?:<(?<generics>[\w.,\s]+)>)?\s*[({\[]",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex ObjectCreation();

    /// <summary>
    /// One dotted chain: an optional leading dot (a fluent continuation), dot-joined
    /// identifiers, optional simple generic arguments, and an optional call-open parenthesis.
    /// The generic group's character class excludes comparison operands (<c>a &lt; b &amp;&amp;
    /// c &gt; d</c> cannot match, because <c>&amp;</c> is excluded), and nested generics simply
    /// fail the group and are skipped — a stated miss, never a false failure.
    /// </summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"(?<lead>\.)?(?<chain>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)(?:<(?<generics>[\w.,\s]+)>)?(?<open>\s*\()?",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex DottedChain();

    [GeneratedRegex(
        @"\b(?<name>[A-Z]\w*)\s*=(?![=>])",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex InitializerAssignment();

    [GeneratedRegex(
        @"/\*.*?\*/",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex BlockComment();

    [GeneratedRegex(
        @"dotnet\s+add\s+package\s+(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex AddPackageCommand();

    [GeneratedRegex(
        @"dotnet\s+tool\s+install\s+(?:-{1,2}[A-Za-z-]+\s+)*(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex ToolInstallCommand();

    // ---- The reference and catalog shapes ----------------------------------------------------

    private enum ReferenceKind
    {
        /// <summary>A <c>using</c> directive's namespace.</summary>
        Namespace,

        /// <summary>A type usage: object creation or a generic argument.</summary>
        Type,

        /// <summary>A <c>Type.Member</c> access on a PascalCase head.</summary>
        StaticMember,

        /// <summary>A <c>.Method(…)</c> invocation.</summary>
        MethodCall,

        /// <summary>A <c>.Member</c> access or an initializer assignment target.</summary>
        MemberAccess,
    }

    /// <summary>One API reference extracted from a README code fence.</summary>
    /// <param name="Kind">The shape it was extracted from.</param>
    /// <param name="Name">The member, type, or namespace name.</param>
    /// <param name="DeclaringType">The head type, for <see cref="ReferenceKind.StaticMember"/>.</param>
    private sealed record ApiReference(ReferenceKind Kind, string Name, string? DeclaringType = null);

    /// <summary>The public surface harvested from one source of assemblies.</summary>
    private sealed class ApiCatalog
    {
        /// <summary>Gets the public type simple names, generic arity stripped.</summary>
        public HashSet<string> TypeNames { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets every declared namespace and every dotted prefix of one.</summary>
        public HashSet<string> NamespacePrefixes { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the public method names, static and instance, accessors excluded.</summary>
        public HashSet<string> MethodNames { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets every public member name: methods, fields, properties.</summary>
        public HashSet<string> MemberNames { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the public static member names per declaring type simple name.</summary>
        public Dictionary<string, HashSet<string>> StaticMembersByType { get; } =
            new(StringComparer.Ordinal);

        public void AddNamespace(string ns)
        {
            var index = ns.Length;
            while (index > 0)
            {
                _ = NamespacePrefixes.Add(ns[..index]);
                index = ns.LastIndexOf('.', index - 1);
            }
        }

        public void AddStaticMember(string typeName, string memberName)
        {
            if (!StaticMembersByType.TryGetValue(typeName, out var members))
            {
                members = new HashSet<string>(StringComparer.Ordinal);
                StaticMembersByType[typeName] = members;
            }

            _ = members.Add(memberName);
        }
    }

    /// <summary>
    /// A package's whole resolvable set: its own catalogs plus its closure's, queried as one.
    /// Kept as parts rather than merged so the per-source catalogs stay shareable across the 66
    /// packages' heavily overlapping closures.
    /// </summary>
    private sealed class CatalogSet
    {
        private readonly List<ApiCatalog> parts;

        public CatalogSet(List<ApiCatalog> parts) => this.parts = parts;

        public bool HasType(string name)
        {
            foreach (var part in parts)
            {
                if (part.TypeNames.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasNamespacePrefix(string name)
        {
            foreach (var part in parts)
            {
                if (part.NamespacePrefixes.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasMethod(string name)
        {
            foreach (var part in parts)
            {
                if (part.MethodNames.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasMember(string name)
        {
            foreach (var part in parts)
            {
                if (part.MemberNames.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasStaticMember(string typeName, string memberName)
        {
            foreach (var part in parts)
            {
                if (part.StaticMembersByType.TryGetValue(typeName, out var members) &&
                    members.Contains(memberName))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
