using System.Reflection;
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Holds Task 3's opt-in <see cref="IDeclaresContentTypes"/> declarations to the behaviour they
/// describe, in both directions, over every parser that implements it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test exists at all.</b> <c>RagBuilder.AddParser&lt;TParser&gt;()</c> now turns a
/// parser's declared <see cref="IDeclaresContentTypes.ContentTypes"/> into a
/// <see cref="ParserClaim"/> per type, automatically. That is only safe if the declared list and
/// <c>CanParse</c> agree — a claim that misdescribes behaviour is worse than no claim at all,
/// because the startup guard would then confidently assert something false. This test is what
/// makes that risk acceptable; see <see cref="ParserClaim"/>'s and
/// <see cref="IDeclaresContentTypes"/>'s own remarks for the same point made from the declaration
/// side.
/// </para>
/// <para>
/// <b>How <c>CanParse</c> is reached.</b> Several opted-in parsers take constructor dependencies
/// (<c>IChatClient</c>, options, a logger) that are not available here. All nine implementations
/// this test currently finds decide <c>CanParse</c> purely from a <see langword="const"/> or a
/// <see langword="static readonly"/> set — none read an instance field — so
/// <see cref="RuntimeHelpers.GetUninitializedObject(Type)"/> produces a usable receiver without
/// calling any constructor. If a future parser's <c>CanParse</c> ever reads instance state, calling
/// it on an uninitialised instance would surface as a <see cref="NullReferenceException"/> naming
/// that parser — a loud failure demanding a real fixture, not a silent gap.
/// </para>
/// <para>
/// <b>What this cannot see.</b> A parser that implements only <see cref="IDocumentParser"/> — every
/// parser that does not opt in — is out of scope by design; it is the documented, accepted
/// invisible case <see cref="ParserClaim"/>'s remarks describe, and forcing it into this test would
/// mean guessing its accepted set, which is exactly the worse mechanism <see cref="ParserClaim"/>
/// rejects. <see cref="TheScanFindsEveryKnownOptedInParser"/> guards the scan itself, so an opted-in
/// parser silently falling out of it (a typo in the assembly list, a build that stops finding
/// implementers) fails loudly instead of shrinking the coverage without comment.
/// </para>
/// <para>
/// <b>Discovery, not a hardcoded list.</b> The nine parsers below are reached by scanning every
/// type in the six referenced parser assemblies for <see cref="IDeclaresContentTypes"/>
/// implementers, rather than by naming <c>AudioDocumentParser</c>, <c>PdfDocumentParser</c> and so
/// on directly. A parser added to one of these packages later is covered automatically; only a
/// wholly new parser package would need a line added to <c>s_parserAssemblies</c>, and
/// <see cref="TheScanFindsEveryKnownOptedInParser"/> would fail first if that were forgotten.
/// </para>
/// </remarks>
public sealed class ParserClaimCoverageTests
{
    private const string OctetStream = "application/octet-stream";

    /// <summary>
    /// Nine parsers implement <see cref="IDeclaresContentTypes"/> today: Audio, Epub, Html, Word,
    /// PowerPoint, Excel, Pdf, Image, Video. Fewer means the scan lost the assemblies and would
    /// pass over nothing — silently, forever.
    /// </summary>
    private const int FewestPlausibleOptedInParsers = 9;

    /// <summary>
    /// The parser packages that implement <see cref="IDeclaresContentTypes"/> as of Phase 4.2. Word,
    /// PowerPoint and Excel all live in the same assembly (<c>Rag.NET.Parsers.Office</c>); listing
    /// one is enough to reach the other two, but all three are named so a reader does not have to
    /// know that to find them here.
    /// </summary>
    private static readonly Assembly[] s_parserAssemblies =
    [
        typeof(Rag.NET.Parsers.Audio.AudioDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Epub.EpubDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Html.HtmlDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Word.WordDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.PowerPoint.PowerPointDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Excel.ExcelDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Pdf.PdfDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Vision.ImageDocumentParser).Assembly,
        typeof(Rag.NET.Parsers.Vision.VideoDocumentParser).Assembly,
    ];

    [Fact]
    public void TheScanFindsEveryKnownOptedInParser()
    {
        var parsers = DiscoverOptedInParsers();

        Assert.True(
            parsers.Count >= FewestPlausibleOptedInParsers,
            $"Found only {parsers.Count} IDeclaresContentTypes implementers across " +
            $"{s_parserAssemblies.Distinct().Count()} assemblies, expected at least " +
            $"{FewestPlausibleOptedInParsers}. A coverage test that scans nothing passes for the " +
            "wrong reason, so this fails instead.");
    }

    /// <summary>
    /// Direction one: declaration implies behaviour. Everything a parser lists in
    /// <see cref="IDeclaresContentTypes.ContentTypes"/> must be something its own <c>CanParse</c>
    /// accepts — otherwise <c>AddParser&lt;T&gt;()</c> declares a <see cref="ParserClaim"/> that
    /// asserts a false thing about the parser.
    /// </summary>
    [Fact]
    public void EveryDeclaredContentTypeIsAcceptedByItsOwnCanParse()
    {
        var violations = new List<string>();

        foreach (var parser in DiscoverOptedInParsers())
        {
            var instance = CreateInstance(parser);
            foreach (var contentType in DeclaredContentTypes(parser))
            {
                if (!instance.CanParse(contentType))
                {
                    violations.Add(
                        $"{parser.FullName} declares '{contentType}' via " +
                        "IDeclaresContentTypes.ContentTypes, but its own CanParse " +
                        $"('{contentType}') returns false.");
                }
            }
        }

        Assert.True(violations.Count == 0, Report(
            "Declared content types that the parser's own CanParse rejects. A claim built from " +
            "this would tell AddParser<T>() something false about the parser:",
            violations));
    }

    /// <summary>
    /// Direction two: behaviour implies declaration, scoped to <see cref="ContentTypeMap"/> — this
    /// library's own extension-to-MIME map, documented as covering "the content types handled by
    /// the Rag.NET parser packages". It is not the "guessed list" <see cref="ParserClaim"/>'s
    /// remarks warn against probing <c>CanParse</c> with; it is a fixed, first-party list this
    /// repository already maintains for an unrelated reason (typing an <c>.msg</c> attachment by
    /// extension), which makes it a safe, bounded set to check declarations against rather than an
    /// attempt to enumerate every content type in existence. A parser that does not implement
    /// <see cref="IDeclaresContentTypes"/> at all is out of scope here — that is the documented,
    /// accepted invisible case, not a gap this assertion should catch.
    /// </summary>
    [Fact]
    public void EveryContentTypeMapEntryItsCanParseAcceptsIsDeclared()
    {
        var mapContentTypes = ContentTypesInMap();
        var violations = new List<string>();

        foreach (var parser in DiscoverOptedInParsers())
        {
            var instance = CreateInstance(parser);
            var declared = new HashSet<string>(DeclaredContentTypes(parser), StringComparer.OrdinalIgnoreCase);

            foreach (var contentType in mapContentTypes)
            {
                if (instance.CanParse(contentType) && !declared.Contains(contentType))
                {
                    violations.Add(
                        $"{parser.FullName}.CanParse('{contentType}') returns true and " +
                        "ContentTypeMap covers it, but IDeclaresContentTypes.ContentTypes does " +
                        "not include it.");
                }
            }
        }

        Assert.True(violations.Count == 0, Report(
            "Content types a parser's CanParse accepts and ContentTypeMap covers, but the " +
            "parser's declared list omits. The guard would stay silent about a real collision " +
            "over one of these types:",
            violations));
    }

    /// <summary>
    /// The octet-stream rule. <see cref="ContentTypeMap"/>'s remarks state the unknown-binary
    /// fallback assumes nothing claims it — an unclaimed attachment degrades to a warn-and-skip,
    /// and a parser that claims <c>application/octet-stream</c> is guessing, which costs the
    /// attachment a wrong parse instead of a clean skip.
    /// </summary>
    [Fact]
    public void NoParserDeclaresOctetStream()
    {
        var violations = new List<string>();

        foreach (var parser in DiscoverOptedInParsers())
        {
            var declaresIt = DeclaredContentTypes(parser)
                .Contains(OctetStream, StringComparer.OrdinalIgnoreCase);

            if (declaresIt)
            {
                violations.Add(parser.FullName ?? parser.Name);
            }
        }

        Assert.True(violations.Count == 0, Report(
            "Parsers declaring 'application/octet-stream'. ContentTypeMap's fallback assumes no " +
            "parser claims it — a claim here means an unknown-binary attachment could be routed " +
            "to a parser that only guesses at its content, instead of being skipped with a " +
            "warning:",
            violations));
    }

    private static IReadOnlyList<Type> DiscoverOptedInParsers() =>
        s_parserAssemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                typeof(IDeclaresContentTypes).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Builds a receiver for <c>CanParse</c> without running any constructor — see the type
    /// remarks for why that is safe for every parser this scan finds today, and how a future
    /// parser that reads instance state from <c>CanParse</c> would announce itself instead of
    /// being silently skipped.
    /// </summary>
    private static IDocumentParser CreateInstance(Type parser)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(parser);
        return instance as IDocumentParser ?? throw new InvalidOperationException(
            $"{parser.FullName} implements IDeclaresContentTypes without implementing " +
            "IDocumentParser. IDeclaresContentTypes's own remarks say it is meant to sit " +
            "alongside IDocumentParser; this scan cannot check CanParse against a type that has " +
            "none.");
    }

    private static IReadOnlyList<string> DeclaredContentTypes(Type parser)
    {
        var property = parser.GetProperty("ContentTypes", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"{parser.FullName} implements IDeclaresContentTypes but exposes no public " +
                "static 'ContentTypes' property reflection can read directly — an explicit " +
                "interface implementation, perhaps. This scan cannot see it and would silently " +
                "skip it, which is exactly what this test exists to prevent; give it a plain " +
                "public static implementation instead.");

        return [.. (IReadOnlyCollection<string>)property.GetValue(obj: null)!];
    }

    /// <summary>
    /// Reads <see cref="ContentTypeMap"/>'s private extension table directly, because the map
    /// exposes no public enumeration of the MIME types it covers — only <c>FromFileName</c>, which
    /// needs an extension this test does not otherwise have a reason to know. Renaming the backing
    /// field requires updating this method too.
    /// </summary>
    private static IReadOnlyList<string> ContentTypesInMap()
    {
        var field = typeof(ContentTypeMap).GetField("s_map", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "ContentTypeMap's private 's_map' field was not found by reflection. This test " +
                "reads it directly because the map has no public enumeration surface; either it " +
                "was renamed (update this test) or the map itself is gone.");

        var map = (Dictionary<string, string>)field.GetValue(obj: null)!;
        return [.. map.Values.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string Report(string heading, List<string> violations) =>
        heading + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", violations);
}
