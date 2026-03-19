using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class ParentDocumentIngestionBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    private static IngestionContext MakeContext(Stream? stream = null)
    {
        return new IngestionContext
        {
            Stream = stream ?? new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = "doc-1",
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            GetNextBm25DocId = () => 1,
        };
    }

    private static ValueTask<IngestionResult> StubNext(IngestionContext ctx, CancellationToken _)
        => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Passthrough_WhenParentOptionsIsNull_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentStore = Substitute.For<IParentChunkStore>();

        var sut = new ParentDocumentIngestionBehavior
        {
            ParentStore = parentStore,
            ParentOptions = null,
            Parsers = [],
            ChunkingStrategy = Substitute.For<IChunkingStrategy>(),
        };

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return StubNext(c, _);
        }

        var ctx = MakeContext();
        await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.True(nextCalled);
        parentStore.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Passthrough_WhenParentStoreIsNull_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = new ParentDocumentIngestionBehavior
        {
            ParentStore = null,
            ParentOptions = new ParentDocumentOptions(),
            Parsers = [],
            ChunkingStrategy = Substitute.For<IChunkingStrategy>(),
        };

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return StubNext(c, _);
        }

        var ctx = MakeContext();
        await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Throws_InvalidOperationException_WhenStreamIsNotSeekable()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentStore = Substitute.For<IParentChunkStore>();

        var sut = new ParentDocumentIngestionBehavior
        {
            ParentStore = parentStore,
            ParentOptions = new ParentDocumentOptions(),
            Parsers = [],
            ChunkingStrategy = Substitute.For<IChunkingStrategy>(),
        };

        var ctx = MakeContext(stream: new NonSeekableStream());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.HandleAsync(ctx, ct, StubNext).AsTask());
    }

    [Fact]
    public async Task ParentKeys_AreSetOnChildChunks_WhenBothOptionsAndStoreAreProvided()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentStore = Substitute.For<IParentChunkStore>();

        var section = new DocumentSection
        {
            Text = "Hello world",
            DocumentId = "doc-1",
            SectionIndex = 0,
        };

        var parentChunk = new TextChunk
        {
            Text = "Hello world",
            DocumentId = "doc-1",
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = 10,
        };

        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse("text/plain").Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(section));

        var chunkingStrategy = Substitute.For<IChunkingStrategy>();
        chunkingStrategy.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(parentChunk));

        var sut = new ParentDocumentIngestionBehavior
        {
            ParentStore = parentStore,
            ParentOptions = new ParentDocumentOptions(),
            Parsers = [parser],
            ChunkingStrategy = chunkingStrategy,
        };

        var childChunk = new TextChunk
        {
            Text = "Hello",
            DocumentId = "doc-1",
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = 4,
        };

        var ctx = MakeContext();
        ctx.Chunks.Add(childChunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.True(ctx.Chunks[0].Metadata.ContainsKey(ParentChunkKeyHelper.ParentKeyMetadata),
            "Child chunk should have _parentKey metadata set");
        Assert.Equal(
            ParentChunkKeyHelper.GetParentKey("doc-1", 0),
            ctx.Chunks[0].Metadata[ParentChunkKeyHelper.ParentKeyMetadata]);
    }

    private static async IAsyncEnumerable<T> AsyncEnumerableOf<T>(T item)
    {
        await Task.CompletedTask;
        yield return item;
    }
}
