using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Html.Tests;

/// <summary>
/// How a link's <c>href</c> reaches the indexed text (#371).
/// </summary>
/// <remarks>
/// The reporter's case is a site-internal path — <c>/nieuws/laatste</c> — appended to the link
/// text. On its own it is noise in an embedding, and it is not resolvable by anyone reading the
/// chunk later. These pin the three answers: keep it, drop it, or make it a URL that works.
/// </remarks>
public class HtmlHrefHandlingTests
{
    private const string Markup =
        """<a data-tracking="eventName=open" class="SubNavBar-style__Anchor" href="/nieuws/laatste">Laatste nieuws</a>""";

    [Fact]
    public async Task Default_KeepsTheHrefBesideTheText()
    {
        // Unchanged from before options existed. Anyone upgrading gets what they had.
        var sections = await ParseAsync(Markup, options: null);

        Assert.Contains("Laatste nieuws (/nieuws/laatste)", Single(sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_DropsTheUrlAndKeepsTheLinkText()
    {
        var sections = await ParseAsync(Markup, new HtmlParserOptions { HrefHandling = HtmlHrefHandling.Remove });

        var text = Single(sections).Text;
        Assert.Contains("Laatste nieuws", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/nieuws/laatste", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAbsolute_ResolvesAgainstTheDocumentsUrlTag()
    {
        // Nothing configured: the crawler, sitemap and RSS providers all record the page URL under
        // "url", so web-ingested content resolves without anyone setting a base.
        var sections = await ParseAsync(
            Markup,
            new HtmlParserOptions { HrefHandling = HtmlHrefHandling.MakeAbsolute },
            tags: new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["url"] = "https://nos.nl/artikel/1" });

        Assert.Contains("Laatste nieuws (https://nos.nl/nieuws/laatste)", Single(sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAbsolute_PrefersTheDocumentsOwnBaseElement()
    {
        // HTML's own mechanism, and authoritative when present: the page is stating its base, which
        // beats what the fetcher happened to record.
        var sections = await ParseAsync(
            """<base href="https://cdn.example.com/site/">""" + Markup,
            new HtmlParserOptions { HrefHandling = HtmlHrefHandling.MakeAbsolute, BaseUri = new Uri("https://fallback.example.com/") },
            tags: new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["url"] = "https://nos.nl/artikel/1" });

        Assert.Contains("(https://cdn.example.com/nieuws/laatste)", Single(sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAbsolute_FallsBackToTheConfiguredBase()
    {
        var sections = await ParseAsync(
            Markup,
            new HtmlParserOptions { HrefHandling = HtmlHrefHandling.MakeAbsolute, BaseUri = new Uri("https://example.com/") });

        Assert.Contains("(https://example.com/nieuws/laatste)", Single(sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAbsolute_WithNoBaseAnywhere_LeavesTheHrefAlone()
    {
        // A made-up base would produce URLs pointing nowhere. A relative path is at least honest
        // about being relative.
        var sections = await ParseAsync(Markup, new HtmlParserOptions { HrefHandling = HtmlHrefHandling.MakeAbsolute });

        Assert.Contains("Laatste nieuws (/nieuws/laatste)", Single(sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAbsolute_LeavesAnAlreadyAbsoluteHrefUnchanged()
    {
        var sections = await ParseAsync(
            """<a href="https://other.example.com/page">Elsewhere</a>""",
            new HtmlParserOptions { HrefHandling = HtmlHrefHandling.MakeAbsolute, BaseUri = new Uri("https://example.com/") });

        Assert.Contains("Elsewhere (https://other.example.com/page)", Single(sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_DoesNotNeedABaseAndIgnoresOne()
    {
        var sections = await ParseAsync(
            Markup,
            new HtmlParserOptions { HrefHandling = HtmlHrefHandling.Remove, BaseUri = new Uri("https://example.com/") });

        Assert.DoesNotContain("example.com", Single(sections).Text, StringComparison.Ordinal);
    }

    private static DocumentSection Single(List<DocumentSection> sections) => Assert.Single(sections);

    private static async Task<List<DocumentSection>> ParseAsync(
        string bodyHtml,
        HtmlParserOptions? options,
        IDictionary<string, MetadataValue>? tags = null)
    {
        var parser = new HtmlDocumentParser(options);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"<html><body>{bodyHtml}</body></html>"));
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc"),
            FileName = "t.html",
            Tags = tags ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
        };

        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, metadata, TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        return sections;
    }
}
