using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Word.Tests;

public class WordDocumentParserTests
{
    private readonly WordDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.docx"
    };

    [Fact]
    public void CanParse_Docx_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Fact]
    public void CanParse_Pdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_ParagraphsWithHeadings_SplitsByHeading()
    {
        using var stream = CreateDocx(body =>
        {
            AddParagraph(body, "Introduction", "Heading1");
            AddParagraph(body, "This is the intro text.");
            AddParagraph(body, "Details", "Heading2");
            AddParagraph(body, "Some detail content.");
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Introduction", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);
        Assert.Contains("intro text", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("Details", sections[1].Heading);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Contains("detail content", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_NoHeadings_ReturnsSingleSection()
    {
        using var stream = CreateDocx(body =>
        {
            AddParagraph(body, "Just a normal paragraph.");
            AddParagraph(body, "Another paragraph.");
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("normal paragraph", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_EmptyDocument_ReturnsNoSections()
    {
        using var stream = CreateDocx(_ => { });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        using var stream = CreateDocx(body =>
        {
            AddParagraph(body, "H1", "Heading1");
            AddParagraph(body, "Text 1");
            AddParagraph(body, "H2", "Heading1");
            AddParagraph(body, "Text 2");
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    private static MemoryStream CreateDocx(Action<Body> configure)
    {
        var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            configure(mainPart.Document.Body!);
            mainPart.Document.Save();
        }
        stream.Position = 0;
        return stream;
    }

    private static void AddParagraph(Body body, string text, string? styleId = null)
    {
        var para = new Paragraph();
        if (styleId is not null)
        {
            para.ParagraphProperties = new ParagraphProperties(
                new ParagraphStyleId { Val = styleId });
        }
        para.AppendChild(new Run(new Text(text)));
        body.AppendChild(para);
    }
}
