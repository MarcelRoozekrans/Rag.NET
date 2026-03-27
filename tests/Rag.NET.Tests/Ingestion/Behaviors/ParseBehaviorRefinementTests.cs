using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class ParseBehaviorRefinementTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IngestionContext MakeContext(string contentType = "text/plain")
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

    private static TextChunk MakeChunk(int index = 0)
        => new TextChunk { Text = $"chunk-{index}", DocumentId = new DocumentId("doc-1"), ChunkIndex = index };

    private static ParseBehavior MakeSut(
        IDocumentParser parser,
        IChunkingStrategy? chunkingStrategy = null,
        IChunkRefinementStrategy? refinementStrategy = null)
    {
        var strategy = chunkingStrategy ?? Substitute.For<IChunkingStrategy>();
        return new ParseBehavior
        {
            Parsers = [parser],
            ChunkingStrategy = strategy,
            ChunkingOptions = new ChunkingOptions(),
            RefinementStrategy = refinementStrategy,
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenRefinementStrategyIsSet_RefineAsyncIsCalledAndOutputReplacesChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection { Text = "Hello world", DocumentId = new DocumentId("doc-1") };
        var rawChunk = MakeChunk(0);
        var refinedChunk = MakeChunk(99);

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(rawChunk));

        var refinementStrategy = Substitute.For<IChunkRefinementStrategy>();
        refinementStrategy
            .RefineAsync(Arg.Any<IAsyncEnumerable<TextChunk>>(), Arg.Any<CancellationToken>())
            .Returns(Yield(refinedChunk));

        var sut = MakeSut(parser, strategy, refinementStrategy);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        // RefineAsync should have been called once
        refinementStrategy.Received(1)
            .RefineAsync(Arg.Any<IAsyncEnumerable<TextChunk>>(), Arg.Any<CancellationToken>());

        // ctx.Chunks should contain the refined output, not the raw chunk
        Assert.Single(ctx.Chunks);
        Assert.Same(refinedChunk, ctx.Chunks[0]);
    }

    [Fact]
    public async Task WhenRefinementStrategyIsNull_ChunksPassThroughUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var section = new DocumentSection { Text = "Hello world", DocumentId = new DocumentId("doc-1") };
        var rawChunk = MakeChunk(0);

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Yield(section));

        var strategy = Substitute.For<IChunkingStrategy>();
        strategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Yield(rawChunk));

        // No refinement strategy — pass null explicitly
        var sut = MakeSut(parser, strategy, refinementStrategy: null);
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, StubNext);

        // ctx.Chunks should contain the raw chunk unchanged
        Assert.Single(ctx.Chunks);
        Assert.Same(rawChunk, ctx.Chunks[0]);
    }
}
