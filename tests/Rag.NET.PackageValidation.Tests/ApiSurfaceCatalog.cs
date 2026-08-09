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
/// From each <c>```csharp</c> fence, after stripping <c>/* */</c> blocks, line comments whose
/// <c>//</c> is at line start or preceded by whitespace (so <c>https://</c> inside strings
/// survives), and string-literal text (see below), four shapes are extracted and each must
/// resolve:
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
/// static methods. (Docs-only exception: see below.)</item>
/// <item>Assignments <c>Name = …</c> where Name is PascalCase (not <c>==</c>/<c>=&gt;</c>) —
/// the object-initializer shape — Name must be a public member name.</item>
/// </list>
/// A type (or generic argument, or static-member declaring type) is also considered resolved when
/// its simple name was declared by a <c>class</c>/<c>record</c>/<c>struct</c>/<c>interface</c>
/// elsewhere on the same page (a README's every fence; a docs page's every fence) — a
/// "here's how to implement your own X" tutorial that declares the type it then registers is
/// correct documentation, and is the dominant shape of the extending/ingestion guides. Only the
/// name travels; the declared type's own members are not verified (the same trust already
/// extended to a namespace-qualified chain's later segments).
/// </para>
/// <para>
/// <b>String literals</b> are blanked before the shapes above ever see them — quoted
/// (<c>"..."</c>), verbatim (<c>@"..."</c>, a doubled <c>""</c> an escaped quote), and raw
/// (<c>"""..."""</c>, three or more opening quotes) alike, plain or interpolated. Without this a
/// connection string's <c>AccountKey=...</c> matches the object-initializer assignment shape —
/// the same false positive stripping comments already exists to prevent, just for a different
/// piece of the lexical grammar. An interpolated string's <c>{expr}</c> holes are copied through
/// unblanked, because they are real code
/// (<c>$"Stored {result.Value.ChunksStored} chunks"</c> is exactly how getting-started.md's own
/// example reads) — with <c>{{</c>/<c>}}</c> recognised as an escaped literal brace, not a hole.
/// A hole containing its own nested string literal is not specially handled: a stated
/// simplification, since none of this repository's docs do that.
/// </para>
/// <para>
/// <b>The resolvable set</b> is built from a caller-supplied list of produced packages (their own
/// closure for a README, every produced package for docs): the assemblies each ships (<c>lib/</c>
/// and <c>tools/</c>), the <em>full transitive closure</em> of each package's external nuspec
/// dependencies — not just the one hop a produced package's own nuspec lists, but recursively
/// walking each external dependency's own cached nuspec too, which is what makes
/// <c>AzureKeyCredential</c> (Azure.Core, reached only via Azure.Search.Documents) and Polly's
/// resilience types (Polly.Core, reached only via Microsoft.Extensions.Resilience) resolvable —
/// read from the NuGet global-packages cache at the declared minimum version (nearest cached
/// version when that exact one is absent — a cache miss narrows the resolvable surface and can
/// only make the check stricter), the shared runtime framework, and any
/// <c>frameworkReference</c> shared frameworks the nuspecs name. Assemblies are read with
/// <see cref="MetadataReader"/> straight from the package zip: no assembly loading, no package
/// code execution, no file locking, and no resolver closure to break on external types — the
/// reasons it was preferred over <c>MetadataLoadContext</c>, which needs every transitive
/// reference materialized on disk.
/// </para>
/// <para>
/// <b>Docs-only exception.</b> A <em>read</em> through a dotted chain off a lowercase local
/// (<c>settings.SharePointDeltaToken</c>) — including the same shape written as an assignment
/// statement rather than a read (<c>settings.ExchangeDeltaToken = token;</c>, indistinguishable
/// from an object-initializer assignment without knowing <c>settings</c> is not being constructed
/// here) — is, by the rule above, checked against the resolvable set's member names regardless of
/// what the local's real type is. Sound for a README's terser examples, but docs are full of
/// chains off a reader's own, undeclared configuration object, which the type-declaration
/// leniency above cannot help with (nothing declares <c>settings</c>'s type at all).
/// <see cref="DocsCodeExamplesTests"/> disables both via <c>checkLowercaseLocalChains: false</c>;
/// <see cref="PackageReadmeTests"/> leaves them on. Deliberately narrow: only a non-call member
/// <em>read</em> is skipped — a trailing <em>call</em> through the same kind of chain
/// (<c>services.AddStackExchangeRedisCache(…)</c>, compact rather than a fluent leading-dot
/// continuation) stays checked, because that shape is how most single-statement registration
/// calls in these docs are written at all; disabling it would have stopped checking far more than
/// the settings-object pattern this exists for. What this gives up, stated plainly: a typo'd
/// property/field <em>read</em> through a lowercase local — a hypothetical
/// <c>result.Vlaue.ChunksStored</c>, or <c>options.MaxChunkSize = 600</c> misspelled — would no
/// longer be flagged in docs. Builder fluent chains (<c>.UseXxx(…)</c>, a leading-dot
/// continuation) are a different shape and are unaffected regardless, including a
/// lowercase-named builder parameter's own leading-dot continuations
/// (<c>b => b.UseHybridSearch()</c>) — which is the shape every defect this guard has actually
/// found so far took.
/// </para>
/// <para>
/// <b>Deliberately unchecked</b> (they need semantic analysis, not name existence): lowercase
/// identifiers, bare PascalCase identifiers outside the shapes above (named arguments like
/// <c>Question:</c>), segments after a namespace-qualified or locally-declared-type head, nested
/// or dotted generic arguments, and whether members sit on the <em>right</em> type when a simple
/// name exists on several. Recorded as possible later strengthening, deliberately not built now:
/// compiling each fence against the package's actual assemblies.
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

    /// <summary>
    /// Blanks the literal text of every string literal — see the class remarks for why and for
    /// what an interpolation hole keeps. Runs after <see cref="StripCodeComments"/>, not before:
    /// that order lets the comment stripper's existing <c>https://</c> carve-out do its job
    /// first, before a string's content is touched, rather than needing a second copy of the
    /// same reasoning here.
    /// </summary>
    /// <param name="code">Comment-stripped fence code.</param>
    /// <returns>The code with string-literal text blanked.</returns>
    internal static string StripStringLiterals(string code)
    {
        var builder = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var header = ReadStringHeader(code, i);
            if (header is null)
            {
                _ = builder.Append(code[i]);
                i++;
                continue;
            }

            i = header.Value.Interpolated
                ? AppendInterpolatedString(code, i, header.Value, builder)
                : AppendPlainString(code, i, header.Value, builder);
        }

        return builder.ToString();
    }

    /// <summary>A string literal's opening marker, already consumed.</summary>
    /// <param name="PrefixLength">Characters from the literal's first character (a <c>$</c>,
    /// <c>@</c>, or <c>"</c>) through its last opening quote, inclusive.</param>
    /// <param name="Verbatim">Whether an <c>@</c> prefix was present (doubled-quote escaping).</param>
    /// <param name="Interpolated">Whether a <c>$</c> prefix was present (has <c>{expr}</c> holes).</param>
    /// <param name="QuoteRunLength">1 for a quoted or verbatim string; 3+ for a raw string, the
    /// number of quotes that closes it.</param>
    private readonly record struct StringHeader(
        int PrefixLength, bool Verbatim, bool Interpolated, int QuoteRunLength);

    private static StringHeader? ReadStringHeader(string code, int i)
    {
        var j = i;
        var interpolated = false;
        var verbatim = false;
        while (j < code.Length && (code[j] == '$' || code[j] == '@'))
        {
            interpolated |= code[j] == '$';
            verbatim |= code[j] == '@';
            j++;
        }

        if (j >= code.Length || code[j] != '"')
        {
            return null; // A bare `@` (a verbatim identifier, `@class`) or `$` — not a string.
        }

        var run = 0;
        while (j + run < code.Length && code[j + run] == '"')
        {
            run++;
        }

        return run >= 3
            ? new StringHeader(j + run - i, verbatim, interpolated, run)
            : new StringHeader(j + 1 - i, verbatim, interpolated, 1);
    }

    private static int AppendPlainString(string code, int start, StringHeader header, StringBuilder builder)
    {
        _ = builder.Append(' ');
        return FindStringEnd(code, start + header.PrefixLength, header);
    }

    private static int FindStringEnd(string code, int i, StringHeader header)
    {
        while (i < code.Length)
        {
            if (IsClosingQuoteRun(code, i, header, out var length))
            {
                return i + length;
            }

            i += LiteralCharacterLength(code, i, header);
        }

        return i; // Unterminated (a malformed fence); stop at end rather than loop forever.
    }

    /// <summary>
    /// Same scan as <see cref="FindStringEnd"/>, except an interpolation hole (<c>{expr}</c>, not
    /// a <c>{{</c>/<c>}}</c> escape) is copied through unblanked and brace-depth tracked so a hole
    /// containing its own <c>{</c>/<c>}</c> (an object initializer, say) does not end the hole
    /// early — see the class remarks for what this does not attempt.
    /// </summary>
    private static int AppendInterpolatedString(
        string code, int start, StringHeader header, StringBuilder builder)
    {
        var i = start + header.PrefixLength;
        _ = builder.Append(' ');
        var holeDepth = 0;

        while (i < code.Length)
        {
            if (holeDepth > 0)
            {
                holeDepth += code[i] switch { '{' => 1, '}' => -1, _ => 0 };
                _ = builder.Append(code[i]);
                i++;
                continue;
            }

            if (IsClosingQuoteRun(code, i, header, out var length))
            {
                return i + length;
            }

            if (Matches(code, i, "{{") || Matches(code, i, "}}"))
            {
                i += 2; // An escaped literal brace, not a hole.
                continue;
            }

            if (code[i] == '{')
            {
                holeDepth = 1;
                _ = builder.Append('{');
                i++;
                continue;
            }

            i += LiteralCharacterLength(code, i, header);
        }

        return i;
    }

    private static bool IsClosingQuoteRun(string code, int i, StringHeader header, out int length)
    {
        length = 0;
        if (code[i] != '"')
        {
            return false;
        }

        if (header.QuoteRunLength >= 3)
        {
            var run = 0;
            while (i + run < code.Length && code[i + run] == '"')
            {
                run++;
            }

            length = run;
            return run >= header.QuoteRunLength;
        }

        if (header.Verbatim && Matches(code, i, "\"\""))
        {
            return false; // A doubled quote is an escaped literal quote, not the terminator.
        }

        length = 1;
        return true;
    }

    private static int LiteralCharacterLength(string code, int i, StringHeader header)
    {
        if (header.Verbatim && Matches(code, i, "\"\""))
        {
            return 2;
        }

        if (!header.Verbatim && header.QuoteRunLength < 3 && code[i] == '\\')
        {
            return Math.Min(2, code.Length - i);
        }

        return 1;
    }

    private static bool Matches(string code, int i, string token) =>
        i + token.Length <= code.Length &&
        string.CompareOrdinal(code, i, token, 0, token.Length) == 0;

    // ---- Reference extraction ------------------------------------------------------------------

    /// <summary>
    /// Extracts the API references of one code fence per the rule in the class remarks. Takes the
    /// catalog only to recognise namespace-qualified chains; every emitted reference is still
    /// resolved separately by <see cref="ResolveFailure"/>.
    /// </summary>
    /// <param name="fence">The fence's code text.</param>
    /// <param name="catalog">The resolvable set.</param>
    /// <param name="locallyDeclaredTypes">Type names declared by a <c>class</c>/<c>record</c>/
    /// <c>struct</c>/<c>interface</c> elsewhere on the same page — see the class remarks. Null or
    /// empty checks every type reference against <paramref name="catalog"/> only.</param>
    /// <param name="checkLowercaseLocalChains">Whether a dotted chain off a lowercase local is
    /// checked at all — see the docs-only exception in the class remarks. True (the README
    /// behaviour) for every caller except <see cref="DocsCodeExamplesTests"/>.</param>
    /// <returns>The extracted references.</returns>
    internal static List<ApiReference> ExtractReferences(
        string fence,
        CatalogSet catalog,
        IReadOnlySet<string>? locallyDeclaredTypes = null,
        bool checkLowercaseLocalChains = true)
    {
        var references = new List<ApiReference>();
        var code = StripCommentsAndStringLiterals(fence);
        code = ExtractUsingDirectives(code, references);
        AddObjectCreationReferences(code, references);
        AddInitializerReferences(code, references, checkLowercaseLocalChains);
        AddChainReferences(code, catalog, references, checkLowercaseLocalChains);
        return FilterLocallyDeclaredTypes(references, locallyDeclaredTypes);
    }

    /// <summary>
    /// Collects the simple names of every <c>class</c>/<c>record</c>/<c>struct</c>/<c>interface</c>
    /// declared in the given code — a caller concatenates every fence on a page first, since a
    /// page commonly declares a type in one fence (an implementation walkthrough) and uses it in
    /// a later one (its registration) — see the class remarks.
    /// </summary>
    /// <param name="code">One or more fences' code text.</param>
    /// <returns>The declared simple type names.</returns>
    internal static HashSet<string> ExtractDeclaredTypeNames(string code)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TypeDeclaration().Matches(StripCommentsAndStringLiterals(code)))
        {
            _ = names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    private static string StripCommentsAndStringLiterals(string code) =>
        StripStringLiterals(StripCodeComments(code));

    /// <summary>
    /// Drops a <see cref="ReferenceKind.Type"/> or <see cref="ReferenceKind.StaticMember"/>
    /// reference whose type name was declared locally — see the class remarks. Only these two
    /// kinds name a type directly; a <see cref="ReferenceKind.MemberAccess"/> or
    /// <see cref="ReferenceKind.MethodCall"/> off a locally-declared type's instance is not
    /// recognisable as such without knowing which local variables hold one, so it is still
    /// checked against the resolvable set — a stated miss matching the class's other
    /// segments-after-a-known-head leniencies, never a false failure.
    /// </summary>
    private static List<ApiReference> FilterLocallyDeclaredTypes(
        List<ApiReference> references, IReadOnlySet<string>? locallyDeclaredTypes)
    {
        if (locallyDeclaredTypes is null || locallyDeclaredTypes.Count == 0)
        {
            return references;
        }

        var kept = new List<ApiReference>(references.Count);
        foreach (var reference in references)
        {
            if (reference.Kind == ReferenceKind.Type && locallyDeclaredTypes.Contains(reference.Name))
            {
                continue;
            }

            if (reference.Kind == ReferenceKind.StaticMember &&
                locallyDeclaredTypes.Contains(reference.DeclaringType!))
            {
                continue;
            }

            kept.Add(reference);
        }

        return kept;
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

    private static void AddInitializerReferences(
        string code, List<ApiReference> references, bool checkLowercaseLocalChains)
    {
        foreach (Match match in InitializerAssignment().Matches(code))
        {
            var receiver = match.Groups["receiver"].Value;
            if (!checkLowercaseLocalChains && receiver.Length > 0 && char.IsLower(receiver[0]))
            {
                // Docs-only exception (see the class remarks): `settings.ExchangeDeltaToken =
                // token;` is the same lowercase-local-chain shape as a read
                // (`settings.SharePointDeltaToken`), just at the assignment statement level
                // rather than inside a `new T { ... }` initializer — this regex cannot tell the
                // two apart without the receiver check.
                continue;
            }

            references.Add(new ApiReference(ReferenceKind.MemberAccess, match.Groups["name"].Value));
        }
    }

    private static void AddChainReferences(
        string code, CatalogSet catalog, List<ApiReference> references, bool checkLowercaseLocalChains)
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
            var skipReadsOnLowercaseReceiver = false;
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

                // Docs-only exception (see the class remarks): a *read* through a lowercase
                // local (settings.SharePointDeltaToken) goes unchecked, since a docs page is
                // full of chains off a reader's own, undeclared object. A trailing *call*
                // (services.AddStackExchangeRedisCache(…)) is a different, much more common
                // shape — it is how a compact (non-fluent, no leading-dot) registration call
                // reads at all — and stays checked either way.
                skipReadsOnLowercaseReceiver = !checkLowercaseLocalChains;
            }

            for (var i = first; i < segments.Length; i++)
            {
                if (!char.IsUpper(segments[i][0]))
                {
                    continue;
                }

                var isCall = match.Groups["open"].Success && i == segments.Length - 1;
                if (!isCall && skipReadsOnLowercaseReceiver)
                {
                    continue;
                }

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
        var packageList = packages as IReadOnlyCollection<string> ?? packages.ToList();
        var parts = new List<ApiCatalog>();
        var frameworkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packageList)
        {
            parts.Add(PackageCatalogs.GetOrAdd(package, HarvestPackageAssemblies));
            foreach (var frameworkName in ReadFrameworkReferences(ProducedPackageTests.ReadNuspec(package)))
            {
                _ = frameworkNames.Add(frameworkName);
            }
        }

        foreach (var (dependencyId, version) in CollectExternalDependencyClosure(packageList, byId))
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

    /// <summary>
    /// Walks the full transitive closure of external (non-produced) dependencies: the given
    /// produced packages' own nuspec dependencies, then each external dependency's own cached
    /// nuspec for further dependencies, recursively — not the one hop a produced package's own
    /// nuspec lists. See the class remarks for why (<c>AzureKeyCredential</c>, Polly's resilience
    /// types).
    /// </summary>
    /// <param name="packages">The produced packages to start from.</param>
    /// <param name="byId">Every produced package, keyed by nuspec id — so a dependency that is
    /// itself a produced package is not walked as though it were external; it is already covered
    /// by its own entry in <paramref name="packages"/>.</param>
    /// <returns>Every external dependency id reached, at the first version encountered for it.</returns>
    private static List<(string Id, string Version)> CollectExternalDependencyClosure(
        IEnumerable<string> packages, Dictionary<string, string> byId)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string Version)>();

        foreach (var package in packages)
        {
            EnqueueExternalDependencies(ProducedPackageTests.ReadNuspec(package), byId, versions, queue);
        }

        while (queue.Count > 0)
        {
            var (id, version) = queue.Dequeue();
            var nuspec = ReadCachedNuspec(id, version);
            if (nuspec is not null)
            {
                EnqueueExternalDependencies(nuspec, byId, versions, queue);
            }
        }

        return versions.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    private static void EnqueueExternalDependencies(
        XDocument nuspec,
        Dictionary<string, string> byId,
        Dictionary<string, string> versions,
        Queue<(string Id, string Version)> queue)
    {
        foreach (var (id, version) in ReadDependencies(nuspec))
        {
            if (byId.ContainsKey(id))
            {
                continue; // An in-repository package; already covered by its own entry.
            }

            if (versions.TryAdd(id, version))
            {
                queue.Enqueue((id, version));
            }
        }
    }

    /// <summary>
    /// Reads an external package's own nuspec from the NuGet global-packages cache, at the
    /// declared version or (a cache miss narrowing the resolvable surface, never wrongly
    /// widening it) the nearest cached one — the same fallback <see cref="HarvestExternalDependency"/>
    /// uses for the package's assemblies, so the dependency graph and the assemblies it names come
    /// from the same on-disk version.
    /// </summary>
    private static XDocument? ReadCachedNuspec(string id, string version)
    {
        var packageRoot = Path.Combine(GlobalPackagesRoot(), id.ToLowerInvariant());
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var directory = Path.Combine(packageRoot, version);
        if (!Directory.Exists(directory))
        {
            directory = NewestVersionDirectory(packageRoot) ?? directory;
        }

        var nuspecPath = Path.Combine(directory, $"{id.ToLowerInvariant()}.nuspec");
        return File.Exists(nuspecPath) ? XDocument.Load(nuspecPath) : null;
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
        @"(?:(?<receiver>[A-Za-z_]\w*)\.)?\b(?<name>[A-Z]\w*)\s*=(?![=>])",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex InitializerAssignment();

    [GeneratedRegex(
        @"/\*.*?\*/",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex BlockComment();

    /// <summary>
    /// A type declaration header: <c>class</c>/<c>interface</c>/<c>struct</c>/<c>record</c>,
    /// optionally <c>record class</c>/<c>record struct</c>, then the declared simple name. Modifiers
    /// (<c>public</c>, <c>sealed</c>, attributes, …) are not matched — the keyword anchor alone is
    /// enough since nothing else in a fence legitimately follows one of these keywords with a
    /// PascalCase identifier.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:class|interface|struct|record)(?:\s+(?:class|struct))?\s+(?<name>[A-Z]\w*)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: RegexTimeout)]
    private static partial Regex TypeDeclaration();

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
