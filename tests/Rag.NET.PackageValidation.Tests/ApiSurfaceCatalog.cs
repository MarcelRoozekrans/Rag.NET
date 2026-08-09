using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// The C# API-reference extractor and public-surface catalog shared by
/// <see cref="PackageReadmeTests"/> (checks each package's own README against that package's
/// dependency closure) and <see cref="DocsCodeExamplesTests"/> (checks docs/ pages against the
/// full set of produced packages, since a docs page may use anything the project ships, not just
/// one package's closure). Extracted so the two callers cannot disagree about what counts as a
/// reference or how it resolves — two extraction rules quietly diverging would be the same defect
/// this machinery exists to catch, one level up.
/// </summary>
/// <remarks>
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
/// </para>
/// <para>
/// <b>The resolvable set</b> is built from a caller-supplied list of produced packages (their own
/// closure for a README, every produced package for docs): the assemblies each ships (<c>lib/</c>
/// and <c>tools/</c>), each package's external nuspec dependencies read from the NuGet
/// global-packages cache at the declared minimum version (nearest cached version when that exact
/// one is absent — a cache miss narrows the resolvable surface and can only make the check
/// stricter), the shared runtime framework, and any <c>frameworkReference</c> shared frameworks
/// the nuspecs name. Assemblies are read with <see cref="MetadataReader"/> straight from the
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
/// fence against the package's actual assemblies.
/// </para>
/// </remarks>
internal static partial class ApiSurfaceCatalog
{
    /// <summary>
    /// Match timeout in milliseconds. The inputs are page-sized and the patterns are linear in
    /// practice; the timeout exists to satisfy MA0009's demand that no regex can run away.
    /// </summary>
    private const int RegexTimeout = 2000;

    // ---- Markdown and comment handling -------------------------------------------------------

    /// <summary>One ```csharp fence extracted from a markdown page.</summary>
    /// <param name="Code">The fence's code text.</param>
    /// <param name="StartLine">
    /// The 1-based line number, in the source markdown, of the fence's first code line — for
    /// pointing a reader at the failure, not part of the extraction rule itself.
    /// </param>
    internal readonly record struct CodeFence(string Code, int StartLine);

    internal static List<CodeFence> ExtractCsharpFences(string markdown)
    {
        var fences = new List<CodeFence>();
        StringBuilder? current = null;
        var startLine = 0;
        var lineNumber = 0;

        foreach (var rawLine in markdown.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            if (current is null)
            {
                if (line.TrimStart().StartsWith("```csharp", StringComparison.Ordinal))
                {
                    current = new StringBuilder();
                    startLine = lineNumber + 1;
                }
            }
            else if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fences.Add(new CodeFence(current.ToString(), startLine));
                current = null;
            }
            else
            {
                _ = current.Append(line).Append('\n');
            }
        }

        return fences;
    }

    internal static string StripCodeComments(string code)
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

    // ---- Reference extraction ------------------------------------------------------------------

    /// <summary>
    /// Extracts the API references of one code fence per the rule in the class remarks. Takes the
    /// catalog only to recognise namespace-qualified chains; every emitted reference is still
    /// resolved separately by <see cref="ResolveFailure"/>.
    /// </summary>
    /// <param name="fence">The fence's code text.</param>
    /// <param name="catalog">The resolvable set.</param>
    /// <returns>The extracted references.</returns>
    internal static List<ApiReference> ExtractReferences(string fence, CatalogSet catalog)
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

    internal static string? ResolveFailure(ApiReference reference, CatalogSet catalog) =>
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

    // ---- Nuspec reading ------------------------------------------------------------------------

    internal static string? ReadNuspecValue(XDocument nuspec, string localName)
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

    internal static Dictionary<string, string> MapPackagesById(IReadOnlyList<string> packages)
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

    private static readonly ConcurrentDictionary<string, ApiCatalog> PackageCatalogs =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ApiCatalog> ExternalCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ApiCatalog> FrameworkCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<ApiCatalog> RuntimeCatalog = new(() =>
        HarvestDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!));

    /// <summary>
    /// Finds the transitive closure, within the produced packages, of one package's dependencies —
    /// used to build the resolvable set for that single package's README.
    /// </summary>
    /// <param name="packagePath">The package whose closure to collect.</param>
    /// <param name="byId">Every produced package, keyed by nuspec id.</param>
    /// <returns>The closure, including <paramref name="packagePath"/> itself.</returns>
    internal static List<string> CollectProducedClosure(
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

    /// <summary>
    /// Builds the resolvable set from an explicit list of produced packages: their own shipped
    /// assemblies, their external nuspec dependencies (from the NuGet global-packages cache), the
    /// shared runtime framework, and any nuspec <c>frameworkReference</c> shared frameworks. A
    /// README passes its own package's closure (see <see cref="CollectProducedClosure"/>); docs
    /// pages pass every produced package, since a docs page may use anything the project ships.
    /// </summary>
    /// <param name="packages">The produced packages whose surface should resolve.</param>
    /// <param name="byId">Every produced package, keyed by nuspec id — distinguishes an
    /// in-repository dependency (already covered by its own entry in <paramref name="packages"/>)
    /// from an external one that must be harvested from the NuGet cache.</param>
    /// <returns>The combined, queryable resolvable set.</returns>
    internal static CatalogSet BuildCatalogFromPackages(
        IEnumerable<string> packages, Dictionary<string, string> byId)
    {
        var parts = new List<ApiCatalog>();
        var externalDependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frameworkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            parts.Add(PackageCatalogs.GetOrAdd(package, HarvestPackageAssemblies));
            var nuspec = ProducedPackageTests.ReadNuspec(package);

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

    internal static bool IsShippedAssembly(string entryName) =>
        entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        (entryName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) ||
         entryName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase));

    internal static void HarvestZipEntry(ZipArchiveEntry entry, ApiCatalog catalog)
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

    internal static void HarvestAssembly(Stream stream, ApiCatalog catalog)
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

        foreach (var handle in metadata.ExportedTypes)
        {
            HarvestExportedType(metadata, metadata.GetExportedType(handle), catalog);
        }
    }

    /// <summary>
    /// Records a type-forwarded name. Modern trimming-friendly builds (Azure.Identity's net10.0
    /// build, observed for <c>DefaultAzureCredential</c>) define almost nothing themselves —
    /// every public type is an <see cref="ExportedType"/> forwarding to another assembly. A
    /// forward carries no visibility bits of its own (<see cref="ExportedType.Attributes"/>
    /// holds only the ECMA <c>tdForwarder</c> marker, not the target's real
    /// <see cref="TypeAttributes.VisibilityMask"/> bits) — but being forwarded at all is
    /// necessarily public, so <see cref="ExportedType.IsForwarder"/> is the whole test. Only the
    /// name and namespace travel with a forward (no member list), which is enough for the
    /// <c>Type</c> and <c>Namespace</c> reference shapes; nested forwards (a forward whose
    /// <see cref="ExportedType.Implementation"/> is itself an <see cref="ExportedType"/> rather
    /// than an assembly reference) are not followed — a stated miss, never a false failure.
    /// </summary>
    private static void HarvestExportedType(
        MetadataReader metadata, ExportedType exportedType, ApiCatalog catalog)
    {
        if (!exportedType.IsForwarder || exportedType.Implementation.Kind != HandleKind.AssemblyReference)
        {
            return;
        }

        var name = StripGenericArity(metadata.GetString(exportedType.Name));
        _ = catalog.TypeNames.Add(name);
        catalog.AddNamespace(metadata.GetString(exportedType.Namespace));
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

    /// <summary>
    /// How many individual failures a message lists before summarising the rest — with dozens of
    /// packages or docs pages capable of failing at once, an uncapped message would bury the
    /// shape of the failure under its volume.
    /// </summary>
    private const int MostFailuresShown = 20;

    internal static string DescribeFailures(string headline, List<string> failures)
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

    // ---- The reference and catalog shapes ----------------------------------------------------

    internal enum ReferenceKind
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

    /// <summary>One API reference extracted from a code fence.</summary>
    /// <param name="Kind">The shape it was extracted from.</param>
    /// <param name="Name">The member, type, or namespace name.</param>
    /// <param name="DeclaringType">The head type, for <see cref="ReferenceKind.StaticMember"/>.</param>
    internal sealed record ApiReference(ReferenceKind Kind, string Name, string? DeclaringType = null);

    /// <summary>The public surface harvested from one source of assemblies.</summary>
    internal sealed class ApiCatalog
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
    /// A resolvable set's parts, queried as one. Kept as parts rather than merged so the
    /// per-source catalogs stay shareable across every caller's heavily overlapping closures.
    /// </summary>
    internal sealed class CatalogSet
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
