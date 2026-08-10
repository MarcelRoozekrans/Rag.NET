using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers.Vision;
using Xunit;

namespace Rag.NET.Parsers.Vision.Tests;

public class ImageChunkingStrategyTests
{
    private static readonly DocumentId DocId = new("img.png");

    private static async IAsyncEnumerable<DocumentSection> Sections(params DocumentSection[] items)
    {
        foreach (var s in items) yield return s;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_StampsTemplateImage()
    {
        var strategy = new ImageChunkingStrategy();
        var sections = Sections(new DocumentSection
        {
            Text = "A bar chart.", Heading = "image_description", DocumentId = DocId,
        });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("image", c.Metadata["template"]));
        Assert.All(chunks, c => Assert.Equal<MetadataValue>("image", c.Metadata["source_type"]));
        Assert.All(chunks, c => Assert.Equal<MetadataValue>("image_description", c.Metadata["part"]));
    }

    [Fact]
    public async Task ChunkAsync_StampsTemplateAndPart()
    {
        var strategy = new ImageChunkingStrategy();
        var section = new DocumentSection
        {
            Text = "A pie chart.", Heading = "image_description", DocumentId = DocId,
        };

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Single(chunks);
        Assert.Equal<MetadataValue>("image", chunks[0].Metadata["template"]);
        Assert.Equal<MetadataValue>("image", chunks[0].Metadata["source_type"]);
        Assert.Equal<MetadataValue>("image_description", chunks[0].Metadata["part"]);
        Assert.Equal("A pie chart.", chunks[0].Text);
    }
}
