using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Rag.NET.Models;
using Xunit;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace Rag.NET.Parsers.PowerPoint.Tests;

public class PowerPointDocumentParserTests
{
    private readonly PowerPointDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.pptx"
    };

    [Fact]
    public void CanParse_Pptx_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/vnd.openxmlformats-officedocument.presentationml.presentation"));
    }

    [Fact]
    public void CanParse_Pdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_MultipleSlides_ReturnsSectionPerSlide()
    {
        using var stream = CreatePptx(["Slide One Text", "Slide Two Text"]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Slide One Text", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Slide Two Text", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_SetsPageNumber()
    {
        using var stream = CreatePptx(["Text 1", "Text 2"]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sections[0].PageNumber);
        Assert.Equal(2, sections[1].PageNumber);
    }

    [Fact]
    public async Task ParseAsync_EmptyPresentation_ReturnsNoSections()
    {
        using var stream = CreatePptx([]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        using var stream = CreatePptx(["A", "B"]);

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    private static MemoryStream CreatePptx(string[] slideTexts)
    {
        var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new Presentation(new SlideIdList());

            var slideIdList = presentationPart.Presentation.SlideIdList!;
            uint slideId = 256;

            foreach (var text in slideTexts)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new Slide(
                    new CommonSlideData(
                        new ShapeTree(
                            new NonVisualGroupShapeProperties(
                                new NonVisualDrawingProperties { Id = 1, Name = "" },
                                new NonVisualGroupShapeDrawingProperties(),
                                new ApplicationNonVisualDrawingProperties()),
                            new GroupShapeProperties(),
                            new Shape(
                                new NonVisualShapeProperties(
                                    new NonVisualDrawingProperties { Id = 2, Name = "TextBox" },
                                    new NonVisualShapeDrawingProperties(),
                                    new ApplicationNonVisualDrawingProperties()),
                                new ShapeProperties(),
                                new TextBody(
                                    new Drawing.BodyProperties(),
                                    new Drawing.Paragraph(
                                        new Drawing.Run(
                                            new Drawing.RunProperties { Language = "en-US" },
                                            new Drawing.Text(text))))))));

                slidePart.Slide.Save();

                slideIdList.AppendChild(new SlideId
                {
                    Id = slideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });
            }

            presentationPart.Presentation.Save();
        }
        stream.Position = 0;
        return stream;
    }
}
