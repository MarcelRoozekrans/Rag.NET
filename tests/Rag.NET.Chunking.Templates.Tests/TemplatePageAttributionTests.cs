using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// Page attribution (issue #82) through the template strategies: pass-through templates stamp
/// each chunk with its section's page as the reserved <see cref="ReservedMetadataKeys.Page"/>/
/// <see cref="ReservedMetadataKeys.PageEnd"/> number pair, merger-backed templates report min/max
/// across the merged sections, and the resume template's LLM-extracted chunks keep the keys
/// absent (no source span to attribute) while its full-text fallback carries the document-wide
/// range.
/// </summary>
public class TemplatePageAttributionTests
{
    private static DocumentSection Section(
        string text, int? page = null, string? heading = null, int? headingLevel = null) =>
        new()
        {
            Text = text,
            DocumentId = new DocumentId("doc"),
            PageNumber = page,
            Heading = heading,
            HeadingLevel = headingLevel,
        };

    private static async IAsyncEnumerable<T> ToAsync<T>(
        IEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
            yield return item;
        await Task.CompletedTask;
    }

    private static void AssertPages(TextChunk chunk, int page, int pageEnd)
    {
        Assert.Equal((MetadataValue)page, chunk.Metadata[ReservedMetadataKeys.Page]);
        Assert.Equal((MetadataValue)pageEnd, chunk.Metadata[ReservedMetadataKeys.PageEnd]);
    }

    private static void AssertNoPages(TextChunk chunk)
    {
        Assert.False(chunk.Metadata.ContainsKey(ReservedMetadataKeys.Page));
        Assert.False(chunk.Metadata.ContainsKey(ReservedMetadataKeys.PageEnd));
    }

    [Fact]
    public async Task QAPairs_StampsEachChunkWithItsSectionPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new QAPairsChunkingStrategy();

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section("Q1 text", page: 1, heading: "A1"), Section("Q2 text", page: 2, heading: "A2")], ct),
            new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        AssertPages(chunks[0], 1, 1);
        AssertPages(chunks[1], 2, 2);
    }

    [Fact]
    public async Task Email_StampsChunkWithItsSectionPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EmailChunkingStrategy();

        var chunks = await sut.ChunkAsync(Section("Body text", page: 2, heading: "body"), new ChunkingOptions(), ct)
            .ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 2, 2);
    }

    [Fact]
    public async Task AcademicPaper_AbstractAndBodyCarrySourcePages()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new AcademicPaperChunkingStrategy(new AcademicPaperChunkingOptions());

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync(
            [
                Section("This paper examines things.", page: 1, heading: "Abstract", headingLevel: 1),
                Section("Introduction", page: 2, heading: "Introduction", headingLevel: 1),
                Section("Intro body spilling over.", page: 3),
            ], ct),
            new ChunkingOptions(), ct).ToListAsync(ct);

        var abstractChunk = Assert.Single(chunks, c =>
            c.Metadata.TryGetValue("section_type", out var t) && t == (MetadataValue)"abstract");
        AssertPages(abstractChunk, 1, 1);

        var bodyChunk = Assert.Single(chunks, c => c.Text.Contains("Intro body", StringComparison.Ordinal));
        AssertPages(bodyChunk, 2, 3);
    }

    [Fact]
    public async Task Book_ChapterSpanningPages_ReportsMinAndMax()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BookChunkingStrategy(new BookChunkingOptions());

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync(
            [
                Section("Chapter One", page: 5, heading: "Chapter One", headingLevel: 1),
                Section("Story on page 5.", page: 5),
                Section("Story continuing on page 6.", page: 6),
            ], ct),
            new ChunkingOptions(), ct).ToListAsync(ct);

        var chunk = Assert.Single(chunks);
        AssertPages(chunk, 5, 6);
    }

    [Fact]
    public async Task Resume_LlmFailureFallback_CarriesDocumentWideRange_ExtractedChunksOmitPages()
    {
        var ct = TestContext.Current.CancellationToken;

        var failingChat = Substitute.For<IChatClient>();
        failingChat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new InvalidOperationException("LLM unavailable"));

        var fallbackChunks = await new ResumeChunkingStrategy(failingChat, new ResumeChunkingOptions())
            .ChunkDocumentAsync(
                ToAsync([Section("Jane Doe", page: 1), Section("Work history", page: 2)], ct),
                new ChunkingOptions(), ct).ToListAsync(ct);

        var fallback = Assert.Single(fallbackChunks);
        AssertPages(fallback, 1, 2);

        var parsingChat = Substitute.For<IChatClient>();
        parsingChat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(
                ChatRole.Assistant, """{"contact_info":"Jane Doe, jane@example.com"}""")]));

        var extractedChunks = await new ResumeChunkingStrategy(parsingChat, new ResumeChunkingOptions())
            .ChunkDocumentAsync(
                ToAsync([Section("Jane Doe", page: 1), Section("Work history", page: 2)], ct),
                new ChunkingOptions(), ct).ToListAsync(ct);

        var extracted = Assert.Single(extractedChunks);
        AssertNoPages(extracted);
    }
}
