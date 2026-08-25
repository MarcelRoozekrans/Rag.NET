using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class ParseBehaviorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IngestionContext MakeContext(
        string contentType = "text/plain",
        IProgress<IngestionProgress>? progress = null)
    {
        return new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
                ContentType = contentType,
            },
            Progress = progress,
            GetNextBm25DocId = () => 0,
        };
    }

    private static ValueTask<IngestionResult> StubNext(IngestionContext ctx, CancellationToken _)
        => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    private static async IAsyncEnumerable<T> Yield<T>(params T[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<T> EmptyAsync<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static TextChunk MakeChunk(int index = 0)
        => new TextChunk { Text = "chunk", DocumentId = new DocumentId("doc-1"), ChunkIndex = index };

    private static ParseBehavior MakeSut(
        IDocumentParser parser,
        IChunkingStrategy? chunkingStrategy = null)
    {
        var strategy = chunkingStrategy ?? Substitute.For<IChunkingStrategy>();
        return new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = strategy,
            ChunkingOptions = new ChunkingOptions(),
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoMatchingParser_ThrowsInvalidOperationException_WithContentTypeInMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse(Arg.Any<string>()).Returns(false);

        var sut = MakeSut(parser);
        var ctx = MakeContext(contentType: "application/pdf");

        var ex = await Assert.ThrowsAsync<NoParserFoundException>(
            () => sut.HandleAsync(ctx, ct, StubNext).AsTask());

        Assert.Contains("application/pdf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleSectionSingleChunk_PopulatesChunksAndSections()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection { Text = "Hello world", DocumentId = new DocumentId("doc-1") };
        var chunk = MakeChunk();

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(chunk));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Single(ctx.Sections);
        Assert.Same(section, ctx.Sections[0]);
        Assert.Single(ctx.Chunks);
        Assert.Same(chunk, ctx.Chunks[0]);
    }

    [Fact]
    public async Task SectionWithoutHeading_ChunkHasNoHeadingMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection { Text = "No heading here", DocumentId = new DocumentId("doc-1") };
        var chunk = MakeChunk();

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(chunk));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.DoesNotContain("heading", chunk.Metadata.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("heading_level", chunk.Metadata.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("heading_breadcrumb", chunk.Metadata.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SectionWithHeadingLevel2_ChunkGetsHeadingMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection
        {
            Text = "Section body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "My Section",
        };
        var chunk = MakeChunk();

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(chunk));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Equal<MetadataValue>("My Section", chunk.Metadata["heading"]);
        Assert.Equal<MetadataValue>("2", chunk.Metadata["heading_level"]);
        Assert.Equal<MetadataValue>("My Section", chunk.Metadata["heading_breadcrumb"]);
    }

    [Fact]
    public async Task NestedHeadings_BuildCorrectBreadcrumb()
    {
        var ct = TestContext.Current.CancellationToken;
        var h1Section = new DocumentSection
        {
            Text = "Intro body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 1,
            Heading = "Intro",
        };
        var h2Section = new DocumentSection
        {
            Text = "Details body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "Details",
        };

        var h1Chunk = MakeChunk(0);
        var h2Chunk = MakeChunk(1);

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(h1Section, h2Section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(h1Section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(h1Chunk));
        strategy.ChunkAsync(h2Section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(h2Chunk));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Equal<MetadataValue>("Intro", h1Chunk.Metadata["heading_breadcrumb"]);
        Assert.Equal<MetadataValue>("Intro > Details", h2Chunk.Metadata["heading_breadcrumb"]);
    }

    [Fact]
    public async Task DeeperHeadingResetsSiblings_StaleHeadingNotCarriedForward()
    {
        var ct = TestContext.Current.CancellationToken;
        // h2 "A", then h1 "B" → the h2 slot is cleared; next h2 "C" breadcrumb is "B > C" not "A > C"
        var h2A = new DocumentSection
        {
            Text = "A body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "A",
        };
        var h1B = new DocumentSection
        {
            Text = "B body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 1,
            Heading = "B",
        };
        var h2C = new DocumentSection
        {
            Text = "C body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "C",
        };

        var chunkA = MakeChunk(0);
        var chunkB = MakeChunk(1);
        var chunkC = MakeChunk(2);

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(h2A, h1B, h2C));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(h2A, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(chunkA));
        strategy.ChunkAsync(h1B, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(chunkB));
        strategy.ChunkAsync(h2C, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(chunkC));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        // chunkC should have "B > C", not "A > C"
        Assert.Equal<MetadataValue>("B > C", chunkC.Metadata["heading_breadcrumb"]);
        Assert.Equal<MetadataValue>("C", chunkC.Metadata["heading"]);
    }

    [Fact]
    public async Task ProgressReported_WhenProgressIsNonNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()!));

        var section = new DocumentSection { Text = "body", DocumentId = new DocumentId("doc-1") };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsync<TextChunk>());

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext(progress: progress);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Parsing, reports[0].Stage);
    }

    [Fact]
    public async Task CallsNextAfterProcessing()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection { Text = "body", DocumentId = new DocumentId("doc-1") };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsync<TextChunk>());

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return StubNext(c, _);
        }

        await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.True(nextCalled, "next delegate should be invoked after parsing");
    }

    /// <summary>
    /// A section whose whole body is its own heading produces <b>no chunk</b> (#366).
    /// </summary>
    /// <remarks>
    /// <c>HtmlDocumentParser.BuildHeadingSection</c> prepends the heading to the section body, so a
    /// heading with nothing under it before the next heading yields a section whose text <i>is</i>
    /// the heading. Its <c>IsNullOrWhiteSpace</c> guard cannot catch that — a heading is not
    /// whitespace. The reporter saw the result indexed as <c>"text": "Section 2"</c>: a chunk that
    /// scores on heading-shaped queries, occupies a retrieval slot, and carries nothing an answer
    /// can use, since <c>heading</c>, <c>heading_level</c> and <c>heading_breadcrumb</c> are already
    /// on every sibling chunk as metadata.
    /// </remarks>
    [Fact]
    public async Task SectionWhoseBodyIsOnlyItsHeading_ProducesNoChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection
        {
            Text = "Section 2",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "Section 2",
        };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(MakeChunk()));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(ctx.Chunks);
    }

    /// <summary>
    /// The empty section is still <i>seen</i>, so the headings under it keep their breadcrumb.
    /// </summary>
    /// <remarks>
    /// This is the trap in the obvious fix. Dropping the heading-only section at the parser, or
    /// skipping it before <c>BuildHeadingMetadata</c> runs, would leave its level unset in the
    /// breadcrumb array — and a child of "Section 2" would then be labelled
    /// <c>Chapter 1 &gt; Subsection</c> rather than <c>Chapter 1 &gt; Section 2 &gt; Subsection</c>.
    /// The chunk is what gets dropped; the section is still walked.
    /// </remarks>
    [Fact]
    public async Task HeadingOnlySection_StillContributesItsBreadcrumbToChildren()
    {
        var ct = TestContext.Current.CancellationToken;
        var h1 = new DocumentSection
        {
            Text = "Chapter 1\nIntro body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 1,
            Heading = "Chapter 1",
        };
        var emptyH2 = new DocumentSection
        {
            Text = "Section 2",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "Section 2",
        };
        var h3 = new DocumentSection
        {
            Text = "Subsection body",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 3,
            Heading = "Subsection",
        };

        var h1Chunk = MakeChunk();
        var h3Chunk = MakeChunk();

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(h1, emptyH2, h3));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(h1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>()).Returns(Yield(h1Chunk));
        strategy.ChunkAsync(h3, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>()).Returns(Yield(h3Chunk));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Equal(2, ctx.Chunks.Count);
        Assert.Equal<MetadataValue>(
            "Chapter 1 > Section 2 > Subsection", h3Chunk.Metadata["heading_breadcrumb"]);
    }

    /// <summary>A section with real content under its heading is untouched.</summary>
    [Fact]
    public async Task SectionWithBodyBeyondItsHeading_StillProducesItsChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection
        {
            Text = "Section 2\nA sentence of actual content.",
            DocumentId = new DocumentId("doc-1"),
            HeadingLevel = 2,
            Heading = "Section 2",
        };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(MakeChunk()));

        var sut = MakeSut(parser, strategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        _ = Assert.Single(ctx.Chunks);
    }
}
