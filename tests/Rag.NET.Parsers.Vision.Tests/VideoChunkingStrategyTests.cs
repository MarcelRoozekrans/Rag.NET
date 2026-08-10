using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers.Vision;
using Xunit;

namespace Rag.NET.Parsers.Vision.Tests;

public class VideoChunkingStrategyTests
{
    private static readonly DocumentId DocId = new("clip.mp4");

    private static async IAsyncEnumerable<DocumentSection> Sections(params DocumentSection[] items)
    {
        foreach (var s in items) yield return s;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_StampsTemplateVideo()
    {
        var strategy = new VideoChunkingStrategy();
        var sections = Sections(new DocumentSection
        {
            Text = "A scene.", Heading = "video_scene_0", DocumentId = DocId, PageNumber = 0,
        });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("video", c.Metadata["template"]));
        Assert.All(chunks, c => Assert.Equal<MetadataValue>("video", c.Metadata["source_type"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_StampsPartFromHeading()
    {
        var strategy = new VideoChunkingStrategy();
        var sections = Sections(
            new DocumentSection { Text = "Scene A.", Heading = "video_scene_0", DocumentId = DocId, PageNumber = 0 },
            new DocumentSection { Text = "Scene B.", Heading = "video_scene_1", DocumentId = DocId, PageNumber = 10 });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Equal<MetadataValue>("video_scene_0", chunks[0].Metadata["part"]);
        Assert.Equal<MetadataValue>("video_scene_1", chunks[1].Metadata["part"]);
    }

    [Fact]
    public async Task ChunkDocumentAsync_StampsTimestampFromPageNumber()
    {
        var strategy = new VideoChunkingStrategy();
        var sections = Sections(new DocumentSection
        {
            Text = "Scene.", Heading = "video_scene_0", DocumentId = DocId, PageNumber = 42,
        });

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(sections, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Equal<MetadataValue>("42", chunks[0].Metadata["timestamp_seconds"]);
    }

    [Fact]
    public async Task ChunkAsync_StampsAllMetadata()
    {
        var strategy = new VideoChunkingStrategy();
        var section = new DocumentSection
        {
            Text = "A scene.", Heading = "video_scene_0", DocumentId = DocId, PageNumber = 5,
        };

        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Single(chunks);
        Assert.Equal<MetadataValue>("video", chunks[0].Metadata["template"]);
        Assert.Equal<MetadataValue>("video", chunks[0].Metadata["source_type"]);
        Assert.Equal<MetadataValue>("video_scene_0", chunks[0].Metadata["part"]);
        Assert.Equal<MetadataValue>("5", chunks[0].Metadata["timestamp_seconds"]);
    }
}
