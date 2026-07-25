using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Parsers.Epub.Tests;

public class EpubDocumentParserTests
{
    private readonly EpubDocumentParser _sut = new(new HtmlDocumentParser());

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("epub-1"),
        FileName = "test.epub",
        ContentType = "application/epub+zip",
    };

    // ── CanParse ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/epub+zip", true)]
    [InlineData("APPLICATION/EPUB+ZIP", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/html", false)]
    public void CanParse_Matrix(string contentType, bool expected)
    {
        Assert.Equal(expected, _sut.CanParse(contentType));
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_TwoChapters_EmitsSectionsInSpineOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = CreateEpub(
            ("Chapter One", "<p>First chapter body text.</p>"),
            ("Chapter Two", "<p>Second chapter body text.</p>"));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Contains("First chapter body text.", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Second chapter body text.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_SectionIndexes_AreSequentialAcrossChapters()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = CreateEpub(
            ("One", "<h1>Alpha</h1><p>a</p><h1>Beta</h1><p>b</p>"),
            ("Two", "<h1>Gamma</h1><p>c</p>"));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.Equal(3, sections.Count);
        Assert.Equal([0, 1, 2], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task Parse_HtmlDelegation_StripsMarkup()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = CreateEpub(
            ("Styled", "<h1>Heading Text</h1><p>Paragraph <em>with</em> markup.</p>"));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        var section = Assert.Single(sections);
        Assert.Equal("Heading Text", section.Heading);
        Assert.Equal(1, section.HeadingLevel);
        Assert.Contains("Paragraph with markup.", section.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_NonEpubStream_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);

        // VersOne surfaces the underlying ZipArchive failure for non-zip input.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => _sut.ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct).AsTask());
    }

    // ── DI ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AddEpubParser_RegistersParserAndHtmlDependency()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddEpubParser();

        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IDocumentParser>(), p => p is EpubDocumentParser);
        Assert.NotNull(provider.GetService<HtmlDocumentParser>());
    }

    // ── EPUB fixture builder ─────────────────────────────────────────────────

    internal static MemoryStream CreateEpub(params (string Title, string BodyXhtml)[] chapters)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The OCF spec requires "mimetype" to be the first entry, stored uncompressed.
            AddEntry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            AddEntry(zip, "META-INF/container.xml", ContainerXml, CompressionLevel.Optimal);
            AddEntry(zip, "OEBPS/content.opf", BuildOpf(chapters.Length), CompressionLevel.Optimal);

            for (int i = 0; i < chapters.Length; i++)
            {
                AddEntry(zip, $"OEBPS/ch{i}.xhtml", BuildChapterXhtml(chapters[i].Title, chapters[i].BodyXhtml), CompressionLevel.Optimal);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private const string ContainerXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string BuildOpf(int chapterCount)
    {
        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (int i = 0; i < chapterCount; i++)
        {
            manifest.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"""    <item id="ch{i}" href="ch{i}.xhtml" media-type="application/xhtml+xml"/>{'\n'}""");
            spine.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"""    <itemref idref="ch{i}"/>{'\n'}""");
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">urn:uuid:rag-net-epub-test</dc:identifier>
                <dc:title>Test Book</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
            {manifest}  </manifest>
              <spine>
            {spine}  </spine>
            </package>
            """;
    }

    private static string BuildChapterXhtml(string title, string bodyXhtml) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>{title}</title></head>
          <body>{bodyXhtml}</body>
        </html>
        """;

    private static void AddEntry(ZipArchive zip, string path, string content, CompressionLevel level)
    {
        var entry = zip.CreateEntry(path, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
