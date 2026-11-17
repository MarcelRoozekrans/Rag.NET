using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// What <see cref="EmbeddedPdbInspector"/> accepts and what it must reject when it reads a Source
/// Link map (#1030).
/// </summary>
/// <remarks>
/// <b>These need no packed packages.</b> The Source Link checks in
/// <c>ProducedPackageTests.EveryPackageShipsItsSymbolsInsideItsAssemblies</c> run only on a CI
/// build, so on a developer machine they never execute. These cases pin the parsing and the
/// resolution by input, so a detector that stopped detecting would fail here on every run instead
/// of only after a push.
/// </remarks>
public sealed class EmbeddedPdbInspectorTests
{
    [Fact]
    public void ParseAcceptsTheWildcardMapSourceLinkWrites()
    {
        var problems = new List<string>();

        var map = EmbeddedPdbInspector.ParseSourceLinkMap(
            """{"documents":{"/_/*":"https://raw.githubusercontent.com/MarcelRoozekrans/Rag.NET/0123abcd/*"}}""",
            problems);

        Assert.Empty(problems);
        Assert.NotNull(map);
        var entry = Assert.Single(map);
        Assert.Equal("/_/*", entry.Key);
    }

    [Fact]
    public void ParseRejectsAWildcardOnOneSideOnly()
    {
        var problems = new List<string>();

        var map = EmbeddedPdbInspector.ParseSourceLinkMap(
            """{"documents":{"/_/*":"https://example.test/file.cs"}}""",
            problems);

        Assert.Null(map);
        Assert.Contains(problems, static p => p.Contains("wildcard rule", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseRejectsAKeyWhoseWildcardIsNotLast()
    {
        var problems = new List<string>();

        var map = EmbeddedPdbInspector.ParseSourceLinkMap(
            """{"documents":{"/_/*/x.cs":"https://example.test/*/x.cs"}}""",
            problems);

        Assert.Null(map);
        Assert.Contains(problems, static p => p.Contains("wildcard rule", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseRejectsAMapWithNoDocuments()
    {
        var problems = new List<string>();

        var map = EmbeddedPdbInspector.ParseSourceLinkMap("""{"documents":{}}""", problems);

        Assert.Null(map);
        Assert.Contains(problems, static p => p.Contains("no documents", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseRejectsAMapWithoutADocumentsObject()
    {
        var problems = new List<string>();

        var map = EmbeddedPdbInspector.ParseSourceLinkMap("{}", problems);

        Assert.Null(map);
        Assert.Contains(problems, static p => p.Contains("without a documents object", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseRejectsJsonThatIsNotJson()
    {
        var problems = new List<string>();

        var map = EmbeddedPdbInspector.ParseSourceLinkMap("not json", problems);

        Assert.Null(map);
        Assert.Contains(problems, static p => p.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveReplacesTheWildcardWithTheRestOfThePath()
    {
        List<(string Key, string Url)> map = [("/_/*", "https://example.test/sha/*")];

        var url = EmbeddedPdbInspector.ResolveSourceLink(map, "/_/src/Rag.NET.Core/Chunk.cs");

        Assert.Equal("https://example.test/sha/src/Rag.NET.Core/Chunk.cs", url);
    }

    [Fact]
    public void ResolvePrefersTheLongestWildcard()
    {
        List<(string Key, string Url)> map = [("/_/*", "https://a.test/*"), ("/_/src/*", "https://b.test/*")];

        var url = EmbeddedPdbInspector.ResolveSourceLink(map, "/_/src/File.cs");

        Assert.Equal("https://b.test/File.cs", url);
    }

    [Fact]
    public void ResolvePrefersAnExactKeyOverAWildcard()
    {
        List<(string Key, string Url)> map = [("/_/*", "https://a.test/*"), ("/_/Special.cs", "https://c.test/Special.cs")];

        var url = EmbeddedPdbInspector.ResolveSourceLink(map, "/_/Special.cs");

        Assert.Equal("https://c.test/Special.cs", url);
    }

    [Fact]
    public void ResolveMatchesCaseInsensitivelyAndKeepsTheOriginalCase()
    {
        List<(string Key, string Url)> map = [("/_/SRC/*", "https://a.test/*")];

        var url = EmbeddedPdbInspector.ResolveSourceLink(map, "/_/src/File.cs");

        Assert.Equal("https://a.test/File.cs", url);
    }

    [Fact]
    public void ResolveReturnsNullWhenNoEntryMatches()
    {
        List<(string Key, string Url)> map = [("/_/src/*", "https://a.test/*")];

        var url = EmbeddedPdbInspector.ResolveSourceLink(map, "/_/tests/File.cs");

        Assert.Null(url);
    }
}
