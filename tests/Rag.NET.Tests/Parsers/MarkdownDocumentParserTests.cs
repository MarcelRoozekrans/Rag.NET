using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class MarkdownDocumentParserTests
{
    private readonly MarkdownDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.md"
    };

    [Theory]
    [InlineData("text/markdown")]
    [InlineData("text/x-markdown")]
    public void CanParse_MarkdownTypes_ReturnsTrue(string contentType)
    {
        Assert.True(_sut.CanParse(contentType));
    }

    [Fact]
    public async Task ParseAsync_SplitsByHeadings()
    {
        var md = "# Title\n\nIntro text.\n\n## Section 1\n\nContent 1.\n\n## Section 2\n\nContent 2.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, sections.Count);
        Assert.Contains("Title", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Contains("Content 1", sections[1].Text, StringComparison.Ordinal);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Contains("Content 2", sections[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_NoHeadings_ReturnsSingleSection()
    {
        var md = "Just some plain text in markdown.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_PreservesDocumentId()
    {
        var md = "# Heading\n\nText.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
    }
}
