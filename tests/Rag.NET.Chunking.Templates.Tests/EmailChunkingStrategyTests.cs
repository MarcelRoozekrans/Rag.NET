using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Text;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class EmailChunkingStrategyTests
{
    private static readonly DocumentId DocId = new("email.eml");

    private static async IAsyncEnumerable<DocumentSection> Sections(params DocumentSection[] sections)
    {
        foreach (var s in sections)
            yield return s;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_StampsTemplateEmail()
    {
        var strategy = new EmailChunkingStrategy();
        var sections = Sections(new DocumentSection { Text = "Body text", Heading = "body", DocumentId = DocId });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("email", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_StampsPartFromHeading()
    {
        var strategy = new EmailChunkingStrategy();
        var sections = Sections(
            new DocumentSection { Text = "From: a@b.com", Heading = "headers", DocumentId = DocId },
            new DocumentSection { Text = "Body text", Heading = "body", DocumentId = DocId });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Equal<MetadataValue>("headers", chunks[0].Metadata["part"]);
        Assert.Equal<MetadataValue>("body", chunks[1].Metadata["part"]);
    }

    [Fact]
    public async Task ChunkDocumentAsync_AttachmentHeadingStampedAsPart()
    {
        var strategy = new EmailChunkingStrategy();
        var sections = Sections(
            new DocumentSection { Text = "attachment content", Heading = "attachment:report.txt", DocumentId = DocId });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Equal<MetadataValue>("attachment:report.txt", chunks[0].Metadata["part"]);
    }

    [Fact]
    public async Task ChunkDocumentAsync_NullHeadingDefaultsToBody()
    {
        var strategy = new EmailChunkingStrategy();
        var sections = Sections(new DocumentSection { Text = "Some text", Heading = null, DocumentId = DocId });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Equal<MetadataValue>("body", chunks[0].Metadata["part"]);
    }

    [Fact]
    public async Task ChunkAsync_StampsTemplateAndPart()
    {
        var strategy = new EmailChunkingStrategy();
        var section = new DocumentSection { Text = "Body text", Heading = "body", DocumentId = DocId };

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Single(chunks);
        Assert.Equal<MetadataValue>("email", chunks[0].Metadata["template"]);
        Assert.Equal<MetadataValue>("body", chunks[0].Metadata["part"]);
    }

    // ── EmailChunkingOptions filters (issue #108: both flags were documented but dead) ──

    private static DocumentSection[] FullEmail() =>
    [
        new DocumentSection { Text = "From: a@b.com", Heading = "headers", DocumentId = DocId },
        new DocumentSection { Text = "Body text", Heading = "body", DocumentId = DocId },
        new DocumentSection { Text = "attachment content", Heading = "attachment:report.txt", DocumentId = DocId },
    ];

    private static async Task<List<TextChunk>> ChunkAllAsync(
        EmailChunkingStrategy strategy, params DocumentSection[] sections)
    {
        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(
            Sections(sections), new ChunkingOptions(), TestContext.Current.CancellationToken))
        {
            chunks.Add(c);
        }

        return chunks;
    }

    [Fact]
    public async Task ChunkDocumentAsync_IncludeHeadersFalse_SkipsHeadersSection()
    {
        var strategy = new EmailChunkingStrategy(new EmailChunkingOptions { IncludeHeaders = false });

        var chunks = await ChunkAllAsync(strategy, FullEmail());

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks, c => c.Metadata["part"] == "headers");
        // ChunkIndex stays gapless so downstream unique-index guarantees hold.
        Assert.Equal([0, 1], chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public async Task ChunkDocumentAsync_IncludeAttachmentsFalse_SkipsAttachmentSections()
    {
        var strategy = new EmailChunkingStrategy(new EmailChunkingOptions { IncludeAttachments = false });

        var chunks = await ChunkAllAsync(strategy, FullEmail());

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks, c => c.Metadata["part"].StringValue.StartsWith("attachment:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_DefaultOptions_KeepsEverySection()
    {
        var strategy = new EmailChunkingStrategy(new EmailChunkingOptions());

        var chunks = await ChunkAllAsync(strategy, FullEmail());

        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public async Task ChunkDocumentAsync_BothFlagsFalse_KeepsOnlyBody()
    {
        var strategy = new EmailChunkingStrategy(new EmailChunkingOptions
        {
            IncludeHeaders = false,
            IncludeAttachments = false,
        });

        var chunks = await ChunkAllAsync(strategy, FullEmail());

        var chunk = Assert.Single(chunks);
        Assert.Equal<MetadataValue>("body", chunk.Metadata["part"]);
    }

    [Fact]
    public async Task ChunkAsync_IncludeHeadersFalse_HeadersSectionYieldsNothing()
    {
        var strategy = new EmailChunkingStrategy(new EmailChunkingOptions { IncludeHeaders = false });
        var section = new DocumentSection { Text = "From: a@b.com", Heading = "headers", DocumentId = DocId };

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Empty(chunks);
    }
}
