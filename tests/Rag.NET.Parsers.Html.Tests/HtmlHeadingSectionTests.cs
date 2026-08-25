using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Html.Tests;

/// <summary>
/// Sectioning by heading, over markup whose headings are nested for layout (#375).
/// </summary>
/// <remarks>
/// The parser used to collect a heading's content by walking <c>NextElementSibling</c>. That is a
/// sibling relation, not a document-order one, so a heading wrapped in layout containers had no
/// next sibling and every following paragraph was <b>dropped</b> — never emitted anywhere. These
/// cases pin the document-order contract the reporter asked for: <i>text between one heading and
/// the next, or to the end when there is no next</i>.
/// </remarks>
public class HtmlHeadingSectionTests
{
    private readonly HtmlDocumentParser _sut = new();

    [Fact]
    public async Task SingleHeadingNestedForLayout_KeepsTheBodyText()
    {
        // The reporter's second example in shape: one heading, wrapped three divs deep, with the
        // content in a sibling container of the heading's grandparent. Called out specifically as
        // the case an earlier attempt still missed.
        var sections = await ParseAsync("""
            <div>
                <div><div><h1>Lorem Ipsum Dolor Sit Amet</h1></div></div>
                <div><div><p><b>Body paragraph one.</b></p></div></div>
            </div>
            """);

        var section = Assert.Single(sections);
        Assert.Equal("Lorem Ipsum Dolor Sit Amet", section.Heading);
        Assert.Contains("Body paragraph one.", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadingNestedForLayout_KeepsEveryFollowingParagraph()
    {
        var sections = await ParseAsync("""
            <div>
                <div><div><h1>Title</h1></div></div>
                <div><div><p><b>First paragraph.</b></p><p>Second paragraph.</p></div></div>
            </div>
            """);

        var section = Assert.Single(sections);
        Assert.Contains("First paragraph.", section.Text, StringComparison.Ordinal);
        Assert.Contains("Second paragraph.", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextBeforeTheFirstHeading_BecomesItsOwnSection()
    {
        // The half of #375 that was not reported: only headings produced sections, so anything
        // above the first one was discarded outright.
        var sections = await ParseAsync("<p>Lead paragraph.</p><h1>Title</h1><p>Body.</p>");

        Assert.Equal(2, sections.Count);
        Assert.Null(sections[0].Heading);
        Assert.Null(sections[0].HeadingLevel);
        Assert.Contains("Lead paragraph.", sections[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Body.", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("Title", sections[1].Heading);
        Assert.Contains("Body.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentIsCutAtTheNextHeading_EvenAcrossContainers()
    {
        // "Take all text between" — the boundary must hold when the next heading is nested
        // somewhere else entirely, which is exactly where a sibling walk stopped working.
        var sections = await ParseAsync("""
            <div><div><h1>First</h1></div><p>Belongs to first.</p></div>
            <div><div><h2>Second</h2></div><p>Belongs to second.</p></div>
            """);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Belongs to first.", sections[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Belongs to second.", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Belongs to second.", sections[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Belongs to first.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LastHeading_TakesEverythingToTheEnd()
    {
        // "If no next heading is found, just take all the text."
        var sections = await ParseAsync("""
            <h1>First</h1><p>One.</p>
            <h2>Last</h2><div><p>Two.</p></div><div><div><p>Three.</p></div></div>
            """);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Two.", sections[1].Text, StringComparison.Ordinal);
        Assert.Contains("Three.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadingTextAppearsOnceEvenWhenItIsMarkedUp()
    {
        // The heading's own text nodes are visited by the same walk that gathers body text. If they
        // were not excluded, a heading containing inline markup would carry its title twice over.
        var sections = await ParseAsync("<h1><span>Marked </span><em>up</em></h1><p>Body.</p>");

        var section = Assert.Single(sections);
        Assert.Equal("Marked up", section.Heading);
        Assert.Equal(1, CountOccurrences(section.Text, "Marked up"));
    }

    [Fact]
    public async Task NestedContainers_DoNotDuplicateTheirText()
    {
        // Accumulating elements' TextContent would count a container and each child; accumulating
        // text nodes cannot, because they are leaves. This is that guarantee, asserted.
        var sections = await ParseAsync("<h1>T</h1><div><div><div><p>Once only.</p></div></div></div>");

        var section = Assert.Single(sections);
        Assert.Equal(1, CountOccurrences(section.Text, "Once only."));
    }

    [Fact]
    public async Task ParagraphsStaySeparated()
    {
        // Block structure survives into Text; the chunkers downstream split on it.
        var sections = await ParseAsync("<h1>T</h1><p>Alpha.</p><p>Beta.</p>");

        var section = Assert.Single(sections);
        Assert.Contains("Alpha.\nBeta.", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadingWithNoContent_StillYieldsItsHeading()
    {
        // Pre-#366 this was a chunk of nothing but its heading; ParseBehavior now skips those. The
        // section must still exist, because BuildHeadingMetadata reads it for the breadcrumb.
        var sections = await ParseAsync("<h1>Alone</h1><h2>Second</h2><p>Body.</p>");

        Assert.Equal(2, sections.Count);
        Assert.Equal("Alone", sections[0].Heading);
        Assert.Equal("Alone", sections[0].Text);
    }

    [Fact]
    public async Task DeeplyNestedHeadingAndBody_AreStillOneSection()
    {
        var sections = await ParseAsync("""
            <main><section><article><div><div><h2>Deep</h2></div></div>
            <div><div><div><p>Deep body.</p></div></div></div></article></section></main>
            """);

        var section = Assert.Single(sections);
        Assert.Equal("Deep", section.Heading);
        Assert.Equal(2, section.HeadingLevel);
        Assert.Contains("Deep body.", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhitespaceInsideAParagraph_CollapsesToSingleSpaces()
    {
        var sections = await ParseAsync("<h1>T</h1><p>one\n\n   two\t\tthree</p>");

        var section = Assert.Single(sections);
        Assert.Contains("one two three", section.Text, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private async Task<List<DocumentSection>> ParseAsync(string bodyHtml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"<html><body>{bodyHtml}</body></html>"));
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc"), FileName = "t.html" };

        var sections = new List<DocumentSection>();
        await foreach (var section in _sut.ParseAsync(stream, metadata, TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        return sections;
    }
}
