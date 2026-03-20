using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Html.Tests;

public class HtmlDocumentParserTests
{
    private readonly HtmlDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("doc-1"),
        FileName = "test.html"
    };

    [Fact]
    public void CanParse_TextHtml_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("text/html"));
    }

    [Fact]
    public void CanParse_ApplicationPdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_SplitsByHeadings()
    {
        var html = "<html><body><h1>Title</h1><p>Intro text.</p><h2>Section 1</h2><p>Content 1.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Title", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Contains("Intro text.", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("Section 1", sections[1].Heading);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Contains("Content 1.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_NoHeadings_ReturnsSingleSection()
    {
        var html = "<html><body><p>Just some text.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Just some text.", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_StripsScriptsAndStyles()
    {
        var html = "<html><head><style>body{}</style></head><body><script>alert(1)</script><p>Visible text.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.DoesNotContain("alert", sections[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("body{}", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Visible text.", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_ConvertsLinksToTextUrl()
    {
        var html = "<html><body><p>Visit <a href=\"https://example.com\">Example</a> site.</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Example (https://example.com)", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_EmptyBody_ReturnsNoSections()
    {
        var html = "<html><body></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        var html = "<html><body><h1>A</h1><p>Text A</p><h2>B</h2><p>Text B</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }
}
