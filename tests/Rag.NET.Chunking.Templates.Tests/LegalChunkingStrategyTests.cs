using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class LegalChunkingStrategyTests
{
    private static DocumentSection Section(string text, string? heading = null, int? headingLevel = null, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc"), Heading = heading, HeadingLevel = headingLevel, SectionIndex = index };

    private static async Task<List<TextChunk>> ChunkAsync(LegalChunkingStrategy strategy, IEnumerable<DocumentSection> sections)
    {
        var chunks = new List<TextChunk>();
        var opts = new ChunkingOptions();
        await foreach (var chunk in strategy.ChunkDocumentAsync(ToAsync(sections), opts))
            chunks.Add(chunk);
        return chunks;
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new LegalChunkingStrategy(new LegalChunkingOptions());
        var sections = new[]
        {
            Section("General provisions apply to all parties.", "1. General Provisions", headingLevel: 1),
            Section("For the purposes of this agreement.", "1.1 Definitions", headingLevel: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("legal", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsClauseMetadata()
    {
        var sut = new LegalChunkingStrategy(new LegalChunkingOptions());
        var sections = new[]
        {
            Section("Article text here.", "1. General Provisions", headingLevel: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        var chunk = Assert.Single(chunks);
        Assert.Equal<MetadataValue>("1. General Provisions", chunk.Metadata["clause"]);
    }

    [Fact]
    public async Task ChunkDocumentAsync_ProducesChunksForEachClause()
    {
        var sut = new LegalChunkingStrategy(new LegalChunkingOptions());
        var sections = new[]
        {
            Section("General provisions text.", "1. General Provisions", headingLevel: 1),
            Section("Obligations text.", "2. Obligations", headingLevel: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.Equal(2, chunks.Count);
    }
}
