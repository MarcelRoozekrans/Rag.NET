using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class AcademicPaperChunkingStrategyTests
{
    private static DocumentSection Section(string text, string? heading = null, int? headingLevel = null, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc"), Heading = heading, HeadingLevel = headingLevel, SectionIndex = index };

    private static async Task<List<TextChunk>> ChunkAsync(AcademicPaperChunkingStrategy strategy, IEnumerable<DocumentSection> sections)
    {
        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(ToAsync(sections), new ChunkingOptions()))
            chunks.Add(chunk);
        return chunks;
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersFrontMatter()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("John Smith, Jane Doe", index: 0),
            Section("University of Science", index: 1),
            Section("This paper examines temperature effects.", "Abstract", headingLevel: 1, index: 2),
            Section("Previous studies have shown...", "Introduction", headingLevel: 1, index: 3),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c => c.Text.Contains("University of Science", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_EmitsAbstractWithSectionTypeMetadata()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("This paper examines temperature effects.", "Abstract", headingLevel: 1, index: 0),
            Section("Previous studies...", "Introduction", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        var abstractChunk = Assert.Single(chunks, c =>
            c.Metadata.TryGetValue("section_type", out var t) && t == "abstract");
        Assert.Contains("temperature effects", abstractChunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDocumentAsync_FiltersReferencesWhenDisabled()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions { IncludeReferences = false });
        var sections = new[]
        {
            Section("This paper examines...", "Abstract", headingLevel: 1, index: 0),
            Section("Background.", "Introduction", headingLevel: 1, index: 1),
            Section("[1] Author et al.", "References", headingLevel: 1, index: 2),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c => c.Text.Contains("Author et al.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("This paper examines...", "Abstract", headingLevel: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("academic_paper", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkAsync_AddsTemplateMetadata()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var section = Section("Introduction text.", "Introduction", headingLevel: 1);

        var chunks = new List<TextChunk>();
        await foreach (var chunk in sut.ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("academic_paper", c.Metadata["template"]));
    }

    [Fact]
    public async Task ChunkDocumentAsync_SuppressesAbstractWhenDisabled()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions { IncludeAbstract = false });
        var sections = new[]
        {
            Section("This paper examines...", "Abstract", headingLevel: 1, index: 0),
            Section("Background.", "Introduction", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("section_type", out var t) &&
            t == "abstract");
    }

    [Fact]
    public async Task ChunkDocumentAsync_FallsBackToFullDocumentWhenNoAbstract()
    {
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());
        var sections = new[]
        {
            Section("Introduction text.", "Introduction", headingLevel: 1, index: 0),
            Section("Discussion text.", "Discussion", headingLevel: 1, index: 1),
        };

        var chunks = await ChunkAsync(sut, sections);

        Assert.NotEmpty(chunks);
        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("section_type", out var t) &&
            t == "abstract");
    }
}
