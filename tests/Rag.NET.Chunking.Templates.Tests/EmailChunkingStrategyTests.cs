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

        Assert.All(chunks, c => Assert.Equal("email", c.Metadata["template"]));
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

        Assert.Equal("headers", chunks[0].Metadata["part"]);
        Assert.Equal("body", chunks[1].Metadata["part"]);
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

        Assert.Equal("attachment:report.txt", chunks[0].Metadata["part"]);
    }

    [Fact]
    public async Task ChunkDocumentAsync_NullHeadingDefaultsToBody()
    {
        var strategy = new EmailChunkingStrategy();
        var sections = Sections(new DocumentSection { Text = "Some text", Heading = null, DocumentId = DocId });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Equal("body", chunks[0].Metadata["part"]);
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
        Assert.Equal("email", chunks[0].Metadata["template"]);
        Assert.Equal("body", chunks[0].Metadata["part"]);
    }
}
