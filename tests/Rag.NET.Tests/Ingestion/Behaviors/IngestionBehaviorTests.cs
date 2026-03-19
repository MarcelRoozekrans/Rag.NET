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
                DocumentId = "doc-1",
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

        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "world", DocumentId = "doc-1", ChunkIndex = 1 });

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
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()));

        var ctx = MakeContext(progress: progress);
        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 });

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
            DocumentId = "doc-42",
            FileName = "report.pdf",
            ContentType = "application/pdf",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["author"] = "Alice",
                ["category"] = "finance",
            },
        };

        var ctx = MakeContext(metadata: metadata);
        ctx.Chunks.Add(new TextChunk { Text = "chunk A", DocumentId = "doc-42", ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "chunk B", DocumentId = "doc-42", ChunkIndex = 1 });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, StubNext);

        foreach (var chunk in ctx.Chunks)
        {
            Assert.Equal("Alice", chunk.Metadata["author"]);
            Assert.Equal("finance", chunk.Metadata["category"]);
            Assert.Equal("doc-42", chunk.Metadata["document_id"]);
            Assert.Equal("report.pdf", chunk.Metadata["file_name"]);
        }
    }

    [Fact]
    public async Task MetadataBehavior_ExistingChunkMetadataNotOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc-1",
            FileName = "test.txt",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["document_id"] = "should-not-overwrite",
            },
        };

        var ctx = MakeContext(metadata: metadata);
        var chunk = new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 };
        // Pre-populate document_id to simulate a chunk that already has it set
        chunk.Metadata["document_id"] = "pre-existing";
        ctx.Chunks.Add(chunk);

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, StubNext);

        // TryAdd should not overwrite pre-existing values
        Assert.Equal("pre-existing", chunk.Metadata["document_id"]);
    }

    [Fact]
    public async Task MetadataBehavior_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = MakeContext();
        ctx.Chunks.Add(new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 });

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

public class StorageAndEmbeddingBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IngestionContext MakeContext(
        DocumentMetadata? metadata = null,
        IProgress<IngestionProgress>? progress = null)
    {
        return new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = metadata ?? new DocumentMetadata
            {
                DocumentId = "doc-1",
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            Progress = progress,
            GetNextBm25DocId = () => 42,
        };
    }

    private static ValueTask<IngestionResult> NeverCalledNext(IngestionContext ctx, CancellationToken _)
        => throw new InvalidOperationException("next should not have been called");

    // ── EmbeddingBehavior ────────────────────────────────────────────────────

    [Fact]
    public async Task EmbeddingBehavior_EmbedderCalledWithChunkTexts_AndEmbeddedChunksPopulated()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var chunk1 = new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "world", DocumentId = "doc-1", ChunkIndex = 1 };

        var vec1 = new float[] { 0.1f, 0.2f };
        var vec2 = new float[] { 0.3f, 0.4f };

        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(vec1),
                new Embedding<float>(vec2),
            ]));

        var sut = new EmbeddingBehavior { Embedder = embedder };

        var ctx = MakeContext();
        ctx.Chunks.Add(chunk1);
        ctx.Chunks.Add(chunk2);

        await sut.HandleAsync(ctx, ct, (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await embedder.Received(1).GenerateAsync(
            Arg.Is<IEnumerable<string>>(texts => texts.SequenceEqual(new[] { "hello", "world" })),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.Equal(2, ctx.EmbeddedChunks.Count);
        Assert.Same(chunk1, ctx.EmbeddedChunks[0].Chunk);
        Assert.Same(chunk2, ctx.EmbeddedChunks[1].Chunk);
        Assert.Equal(vec1, ctx.EmbeddedChunks[0].Embedding.ToArray());
        Assert.Equal(vec2, ctx.EmbeddedChunks[1].Embedding.ToArray());
    }

    [Fact]
    public async Task EmbeddingBehavior_ReportsEmbeddingProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(new float[] { 1f }),
            ]));

        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(progress: progress);
        ctx.Chunks.Add(new TextChunk { Text = "hi", DocumentId = "doc-1", ChunkIndex = 0 });

        await sut.HandleAsync(ctx, ct, (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Embedding, reports[0].Stage);
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(1, reports[0].Total);
    }

    // ── StorageBehavior ───────────────────────────────────────────────────────

    [Fact]
    public async Task StorageBehavior_CallsVectorStore_Bm25_And_DataManager()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = dataManager,
        };

        var chunk = new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 };
        var embeddedChunk = new EmbeddedChunk { Chunk = chunk, Embedding = new float[] { 0.5f } };

        var ctx = MakeContext();
        ctx.Chunks.Add(chunk);
        ctx.EmbeddedChunks.Add(embeddedChunk);

        var result = await sut.HandleAsync(ctx, ct, NeverCalledNext);

        await vectorStore.Received(1).StoreAsync(ctx.EmbeddedChunks, ct);
        bm25.Received(1).Add(42, chunk);
        dataManager.Received(1).Add(ctx.Metadata, ctx.Chunks);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
    }

    [Fact]
    public async Task StorageBehavior_IsTerminal_NextIsNeverCalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext();
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        // NeverCalledNext will throw if called
        var result = await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Assert.Equal("doc-1", result.DocumentId);
    }

    [Fact]
    public async Task StorageBehavior_NullDataManager_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext();
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        var ex = await Record.ExceptionAsync(() =>
            sut.HandleAsync(ctx, ct, NeverCalledNext).AsTask());

        Assert.Null(ex);
    }

    [Fact]
    public async Task StorageBehavior_ReportsStoringProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();

        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()));

        var sut = new StorageBehavior
        {
            VectorStore = vectorStore,
            Bm25Index = bm25,
            DataManager = null,
        };

        var ctx = MakeContext(progress: progress);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        });

        await sut.HandleAsync(ctx, ct, NeverCalledNext);

        Assert.Single(reports);
        Assert.Equal(IngestionProgressStage.Storing, reports[0].Stage);
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(1, reports[0].Total);
    }
}

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
