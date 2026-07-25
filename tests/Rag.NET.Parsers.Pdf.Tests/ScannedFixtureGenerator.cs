using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Generator for the checked-in fixture
/// <c>tests/Rag.NET.Parsers.IntegrationTests/Resources/sample-scanned.pdf</c> (linked into
/// this project as an embedded resource). The fixture was produced once by writing
/// <see cref="Generate"/>'s output to disk (PdfPig 0.1.15 <c>PdfDocumentBuilder</c>);
/// rerun it and overwrite the resource to regenerate.
///
/// The page stands in for a scanned page: Letter portrait with NO text content, embedding
/// only <c>Resources/ocr-sample.png</c> (600x140 px, "Integration Test OCR Sample" /
/// "scanned page fixture" in black on white, rendered once with System.Drawing) at
/// (56, 400)-(556, 517) in PDF bottom-up coordinates.
/// </summary>
internal static class ScannedFixtureGenerator
{
    internal static byte[] Generate()
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.Letter, isPortrait: true);
        _ = page.AddPng(ReadPngResource(), new PdfRectangle(56, 400, 556, 517));
        return builder.Build();
    }

    internal static byte[] ReadPngResource()
    {
        using var stream = typeof(ScannedFixtureGenerator).Assembly.GetManifestResourceStream(
                "Rag.NET.Parsers.Pdf.Tests.Resources.ocr-sample.png")
            ?? throw new InvalidOperationException("Embedded resource 'ocr-sample.png' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
