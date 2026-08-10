using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class IngestionBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IngestionContext MakeContext(
        DocumentMetadata? metadata = null,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null)
    {
        return new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = metadata ?? new DocumentMetadata
            {
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            Options = options,
            Progress = progress,
            GetNextBm25DocId = () => 1,
        };
    }

    private static ValueTask<IngestionResult> StubNext(IngestionContext ctx, CancellationToken _)
        => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    // ── OverwriteBehavior ────────────────────────────────────────────────────

    [Fact]
    public async Task OverwriteBehavior_OverwriteFalse_DoesNotCallStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();

        var sut = new OverwriteBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = dataManager,
        };

        var ctx = MakeContext(options: new IngestionOptions { Overwrite = false });
        await sut.HandleAsync(ctx, ct, StubNext);

        await vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        bm25.DidNotReceive().Remove(Arg.Any<string>());
        dataManager.DidNotReceive().Remove(Arg.Any<string>());
    }

    [Fact]
    public async Task OverwriteBehavior_OverwriteTrue_CallsAllThreeStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();

        var sut = new OverwriteBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = dataManager,
        };

        var ctx = MakeContext(options: new IngestionOptions { Overwrite = true });
        await sut.HandleAsync(ctx, ct, StubNext);

        await vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
        bm25.Received(1).Remove("doc-1");
        dataManager.Received(1).Remove("doc-1");
    }

    [Fact]
    public async Task OverwriteBehavior_OverwriteTrue_NoDataManager_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new OverwriteBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext(options: new IngestionOptions { Overwrite = true });
        var result = await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Equal("doc-1", result.DocumentId);
    }

    // ── ChunkingBehavior ─────────────────────────────────────────────────────

    [Fact]
    public async Task ChunkingBehavior_EmptyChunks_ShortCircuitsWithZeroChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new ChunkingBehavior();
        var ctx = MakeContext();

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 99 });
        }

        var result = await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.False(nextCalled, "next should not be called when there are no chunks");
        Assert.Equal(0, result.ChunksStored);
        Assert.Equal("doc-1", result.DocumentId);
    }

    [Fact]
    public async Task ChunkingBehavior_WithChunks_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new ChunkingBehavior();
        var ctx = MakeContext();

        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "world", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 });

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 2 });
        }

        var result = await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.True(nextCalled, "next should be called when there are chunks");
        Assert.Equal(2, result.ChunksStored);
    }

    [Fact]
    public async Task ChunkingBehavior_WithChunks_ReportsProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()!));

        var ctx = MakeContext(progress: progress);
        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });

        var sut = new ChunkingBehavior();
        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Chunking, reports[0].Stage);
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(1, reports[0].Total);
    }

    // ── MetadataBehavior ─────────────────────────────────────────────────────

    [Fact]
    public async Task MetadataBehavior_AppliesTagsAndDocumentIdAndFileNameToAllChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc-42"),
            FileName = "report.pdf",
            ContentType = "application/pdf",
            Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["author"] = "Alice",
                ["category"] = "finance",
            },
        };

        var ctx = MakeContext(metadata: metadata);
        ctx.Chunks.Add(new TextChunk { Text = "chunk A", DocumentId = new DocumentId("doc-42"), ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "chunk B", DocumentId = new DocumentId("doc-42"), ChunkIndex = 1 });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, StubNext);

        foreach (var chunk in ctx.Chunks)
        {
            Assert.Equal<MetadataValue>("Alice", chunk.Metadata["author"]);
            Assert.Equal<MetadataValue>("finance", chunk.Metadata["category"]);
            Assert.Equal<MetadataValue>("doc-42", chunk.Metadata["document_id"]);
            Assert.Equal<MetadataValue>("report.pdf", chunk.Metadata["file_name"]);
        }
    }

    [Fact]
    public async Task MetadataBehavior_ExistingChunkMetadataNotOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc-1"),
            FileName = "test.txt",
            Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["document_id"] = "should-not-overwrite",
            },
        };

        var ctx = MakeContext(metadata: metadata);
        var chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 };
        // Pre-populate document_id to simulate a chunk that already has it set
        chunk.Metadata["document_id"] = "pre-existing";
        ctx.Chunks.Add(chunk);

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, StubNext);

        // TryAdd should not overwrite pre-existing values
        Assert.Equal<MetadataValue>("pre-existing", chunk.Metadata["document_id"]);
    }

    [Fact]
    public async Task MetadataBehavior_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = MakeContext();
        ctx.Chunks.Add(new TextChunk { Text = "x", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return StubNext(c, _);
        }

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.True(nextCalled);
    }
}
