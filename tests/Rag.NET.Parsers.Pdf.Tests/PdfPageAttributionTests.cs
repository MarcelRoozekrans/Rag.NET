using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// End-to-end page attribution (issue #82): an actual two-page PDF, parsed by
/// <see cref="PdfDocumentParser"/> and chunked through the pipeline's
/// <see cref="ParseBehavior"/>, must produce chunks whose reserved
/// <see cref="ReservedMetadataKeys.Page"/>/<see cref="ReservedMetadataKeys.PageEnd"/> metadata
/// points back at the page the text came from, as <see cref="MetadataValueKind.Number"/> values —
/// proving the number PdfPig reads survives the chunking boundary typed rather than only
/// round-tripping as a literal.
/// </summary>
public class PdfPageAttributionTests
{
    private static byte[] GenerateTwoPagePdf()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var page1 = builder.AddPage(PageSize.Letter, isPortrait: true);
        page1.AddText("Alpha content that lives on the first page", 12, new PdfPoint(100, 700), font);

        var page2 = builder.AddPage(PageSize.Letter, isPortrait: true);
        page2.AddText("Omega content that lives on the second page", 12, new PdfPoint(100, 700), font);

        return builder.Build();
    }

    private static void AssertPages(TextChunk chunk, int page, int pageEnd)
    {
        Assert.Equal((MetadataValue)page, chunk.Metadata[ReservedMetadataKeys.Page]);
        Assert.Equal((MetadataValue)pageEnd, chunk.Metadata[ReservedMetadataKeys.PageEnd]);
    }

    [Fact]
    public async Task TwoPagePdf_ThroughParseBehavior_ChunksCarryTheirSourcePages()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(GenerateTwoPagePdf()),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("pdf-pages"),
                FileName = "two-pages.pdf",
                ContentType = "application/pdf",
            },
            GetNextBm25DocId = () => 0,
        };
        var behavior = new ParseBehavior
        {
            Parsers = [new PdfDocumentParser()],
            ChunkingStrategy = new RecursiveChunkingStrategy(),
            ChunkingOptions = new ChunkingOptions(),
        };

        await behavior.HandleAsync(ctx, ct, static (c, _) =>
            ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.NotEmpty(ctx.Chunks);
        Assert.All(ctx.Chunks, c =>
        {
            Assert.Equal(MetadataValueKind.Number, c.Metadata[ReservedMetadataKeys.Page].Kind);
            Assert.Equal(c.Metadata[ReservedMetadataKeys.Page], c.Metadata[ReservedMetadataKeys.PageEnd]);
        });

        var firstPageChunk = Assert.Single(ctx.Chunks, c => c.Text.Contains("Alpha content", StringComparison.Ordinal));
        AssertPages(firstPageChunk, 1, 1);

        var secondPageChunk = Assert.Single(ctx.Chunks, c => c.Text.Contains("Omega content", StringComparison.Ordinal));
        AssertPages(secondPageChunk, 2, 2);
    }

    [Fact]
    public async Task TwoPagePdf_HierarchicalMergerMergingBothPages_ReportsPageRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = new PdfDocumentParser();
        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(
            new MemoryStream(GenerateTwoPagePdf()),
            new DocumentMetadata
            {
                DocumentId = new DocumentId("pdf-pages"),
                FileName = "two-pages.pdf",
                ContentType = "application/pdf",
            },
            ct))
        {
            sections.Add(section);
        }

        Assert.Equal([1, 2], sections.Select(s => s.PageNumber));

        // PDF prose sections carry no headings, so the merger folds both pages into one
        // chunk — which must then span pages 1–2 rather than losing the range.
        var merger = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());
        var chunks = await merger.ChunkDocumentAsync(
            sections.ToAsyncEnumerable(), new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 1, 2);
    }
}
