using System.IO.Compression;
using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers.Epub;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class EpubDocumentParserTests
{
    private readonly HtmlDocumentParser _htmlParser = new();
    private EpubDocumentParser CreateSut() => new(_htmlParser);

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("epub-1"),
        FileName = "test.epub",
        ContentType = "application/epub+zip",
    };

    [Fact]
    public void CanParse_EpubMimeType_ReturnsTrue()
    {
        Assert.True(CreateSut().CanParse("application/epub+zip"));
    }

    [Fact]
    public void CanParse_OtherType_ReturnsFalse()
    {
        Assert.False(CreateSut().CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_SingleChapterWithHeadings_YieldsSectionsPerHeading()
    {
        // Build an in-memory EPUB with one chapter containing two headings
        var html = "<html><body><h1>Chapter 1</h1><p>First paragraph.</p><h2>Section A</h2><p>Body A.</p></body></html>";
        using var stream = BuildEpub([html]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Chapter 1", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Section A", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_MultipleChapters_SectionIndicesAreGlobal()
    {
        var chapter1 = "<html><body><h1>Chapter 1</h1><p>Text 1.</p></body></html>";
        var chapter2 = "<html><body><h1>Chapter 2</h1><p>Text 2.</p></body></html>";
        using var stream = BuildEpub([chapter1, chapter2]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptyChapters_YieldsNoSections()
    {
        using var stream = BuildEpub(["<html><body></body></html>"]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_ChapterWithNoHeadings_YieldsSingleSection()
    {
        // A chapter with body text but no heading tags — HtmlDocumentParser returns one flat section
        var html = "<html><body><p>Just a paragraph with no headings.</p></body></html>";
        using var stream = BuildEpub([html]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Just a paragraph", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_AllSectionsCarryDocumentId()
    {
        var html = "<html><body><h1>Title</h1><p>Text.</p></body></html>";
        using var stream = BuildEpub([html]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("epub-1", s.DocumentId));
    }

    // Builds a minimal valid EPUB stream with the given HTML chapters as spine items.
    private static MemoryStream BuildEpub(string[] chapterHtmls)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // mimetype (must be first, uncompressed)
            var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mimetypeEntry.Open())) w.Write("application/epub+zip");

            // META-INF/container.xml
            var containerEntry = zip.CreateEntry("META-INF/container.xml");
            using (var w = new StreamWriter(containerEntry.Open()))
                w.Write("""
                    <?xml version="1.0"?>
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles>
                        <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                      </rootfiles>
                    </container>
                    """);

            // Build manifest and spine items
            var manifestItems = new StringBuilder();
            var spineItems = new StringBuilder();
            for (int i = 0; i < chapterHtmls.Length; i++)
            {
                manifestItems.Append(System.Globalization.CultureInfo.InvariantCulture, $"""    <item id="chapter{i + 1}" href="chapter{i + 1}.html" media-type="application/xhtml+xml"/>""").AppendLine();
                spineItems.Append(System.Globalization.CultureInfo.InvariantCulture, $"""    <itemref idref="chapter{i + 1}"/>""").AppendLine();
            }

            // OEBPS/content.opf
            var opfEntry = zip.CreateEntry("OEBPS/content.opf");
            using (var w = new StreamWriter(opfEntry.Open()))
                w.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
                      <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                        <dc:title>Test Book</dc:title>
                        <dc:identifier id="uid">test-epub-id</dc:identifier>
                        <dc:language>en</dc:language>
                      </metadata>
                      <manifest>
                    {manifestItems}  </manifest>
                      <spine>
                    {spineItems}  </spine>
                    </package>
                    """);

            // Chapter HTML files
            for (int i = 0; i < chapterHtmls.Length; i++)
            {
                var chapterEntry = zip.CreateEntry($"OEBPS/chapter{i + 1}.html");
                using var w = new StreamWriter(chapterEntry.Open());
                w.Write(chapterHtmls[i]);
            }
        }
        ms.Position = 0;
        return ms;
    }
}
