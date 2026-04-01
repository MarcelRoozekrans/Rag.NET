using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class BookChunkingStrategyTests
{
    private static DocumentSection Section(string text, string? heading = null, int? headingLevel = null, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc"), Heading = heading, HeadingLevel = headingLevel, SectionIndex = index };

    private static async Task<List<TextChunk>> ChunkAsync(BookChunkingStrategy strategy, IEnumerable<DocumentSection> sections)
    {
        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(ToAsync(sections), new ChunkingOptions()))
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
    public async Task ChunkDocumentAsync_FiltersTocSection()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions());
        var sections = new[]
        {
            Section("Chapter 1 ......... 1\nChapter 2 ......... 5", "Table of Contents", headingLevel: 1, index: 0),
            Section("The first chapter content.", "Chapter 1", headingLevel: 1, index: 1),
            Section("The second chapter content.", "Chapter 2", headingLevel: 1, index: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("heading", out var h) &&
            h.Contains("Contents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChunkDocumentAsync_PreservesChapterContent()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions());
        var sections = new[]
        {
            Section("Chapter 1 ......... 1", "Table of Contents", headingLevel: 1, index: 0),
            Section("Chapter 1", "Chapter 1", headingLevel: 1, index: 1),
            Section("The first chapter content.", index: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.Contains(chunks, c => c.Text.Contains("first chapter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions());
        var sections = new[] { Section("Chapter content.", "Chapter 1", headingLevel: 1) };

        var chunks = await ChunkAsync(sut, sections);

        Assert.All(chunks, c => Assert.Equal("book", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersIndexWhenDisabled()
    {
        var sut = new BookChunkingStrategy(new BookChunkingOptions { IncludeIndex = false });
        var sections = new[]
        {
            Section("Chapter content.", "Chapter 1", headingLevel: 1, index: 0),
            Section("A\n  1\nB\n  3", "Index", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("heading", out var h) &&
            h.Equals("Index", StringComparison.OrdinalIgnoreCase));
    }
}
