using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class HierarchicalMergerChunkingStrategyTests
{
    private static readonly ChunkingOptions DefaultOptions = new();
    private static readonly DocumentId DocId = new("doc-1");

    private static DocumentSection Heading(string text, int level) => new()
    {
        Text = text, DocumentId = DocId, SectionIndex = 0, Heading = text, HeadingLevel = level
    };

    private static DocumentSection Body(string text) => new()
    {
        Text = text, DocumentId = DocId, SectionIndex = 0
    };

    private static async IAsyncEnumerable<DocumentSection> Sections(
        params DocumentSection[] sections)
    {
        foreach (var s in sections)
            yield return s;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_ThreeH1Sections_ProducesThreeChunks()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 1 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Heading("Section A", 1), Body("Body A"),
                Heading("Section B", 1), Body("Body B"),
                Heading("Section C", 1), Body("Body C")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, chunks.Count);
        Assert.Contains("Section A", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Body A", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Section B", chunks[1].Text, StringComparison.Ordinal);
        Assert.Contains("Section C", chunks[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDocumentAsync_H1ThenH2ThenH3_MaxDepth2_MergesH3IntoH2()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions { MaxDepth = 2 });

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Heading("Chapter", 1), Body("Intro"),
                Heading("Section", 2), Body("Section body"),
                Heading("Subsection", 3), Body("Sub body")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // H1 chunk: "Chapter\n\nIntro"
        // H2 chunk: "Section\n\nSection body\n\nSubsection\n\nSub body" (H3 folded in)
        Assert.Equal(2, chunks.Count);
        Assert.Contains("Chapter", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Section", chunks[1].Text, StringComparison.Ordinal);
        Assert.Contains("Sub body", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDocumentAsync_BodyBeforeFirstHeading_EmittedAsChunkWithNoPrefix()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkDocumentAsync(
            Sections(
                Body("Preamble text"),
                Heading("First heading", 1), Body("Under heading")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Preamble text", chunks[0].Text);
        Assert.DoesNotContain("\n\n", chunks[0].Text, StringComparison.Ordinal); // no heading prefix
    }

    [Fact]
    public async Task ChunkDocumentAsync_EmptySectionStream_ProducesNoChunks()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkDocumentAsync(
            AsyncEnumerable.Empty<DocumentSection>(),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkDocumentAsync_RegexFallback_DetectsHeadingsWhenLevelIsNull()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions
        {
            MaxDepth = 1,
            HeadingPatterns = [["^# "]]  // level-1 regex
        });

        // Sections have no HeadingLevel set — regex must detect them
        var plain1 = new DocumentSection { Text = "# Alpha", DocumentId = DocId, SectionIndex = 0 };
        var body1  = new DocumentSection { Text = "Content A", DocumentId = DocId, SectionIndex = 1 };
        var plain2 = new DocumentSection { Text = "# Beta",  DocumentId = DocId, SectionIndex = 2 };
        var body2  = new DocumentSection { Text = "Content B", DocumentId = DocId, SectionIndex = 3 };

        var chunks = await sut.ChunkDocumentAsync(
            Sections(plain1, body1, plain2, body2),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("Content A", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Content B", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDocumentAsync_SetsHeadingMetadata()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());

        var chunks = await sut.ChunkDocumentAsync(
            Sections(Heading("My Heading", 1), Body("body")),
            DefaultOptions,
            TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.True(chunks[0].Metadata.TryGetValue("heading", out var h));
        Assert.Equal("My Heading", h);
        Assert.True(chunks[0].Metadata.TryGetValue("heading_level", out var level));
        Assert.Equal("1", level);
    }

    [Fact]
    public async Task ChunkAsync_PerSectionFallback_ReturnsEachSectionAsOneChunk()
    {
        var sut = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions());
        var section = new DocumentSection
        {
            Text = "some text",
            DocumentId = DocId,
            SectionIndex = 3,
        };

        var chunks = await sut.ChunkAsync(section, DefaultOptions, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("some text", chunks[0].Text);
        Assert.Equal(3, chunks[0].ChunkIndex);
    }
}
