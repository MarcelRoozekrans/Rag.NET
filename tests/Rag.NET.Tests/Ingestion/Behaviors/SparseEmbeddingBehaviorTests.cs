using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class SparseEmbeddingBehaviorTests
{
    /// <summary>
    /// Hand-written fake: substituting <see cref="ISparseEmbeddingGenerator.GenerateAsync"/>
    /// (a <see cref="ValueTask{T}"/> member) via NSubstitute trips EPS06 (hidden struct copy).
    /// </summary>
    private sealed class FakeSparseGenerator : ISparseEmbeddingGenerator
    {
        private readonly Func<string, SparseVector> _generate;

        public FakeSparseGenerator(Func<string, SparseVector> generate) => _generate = generate;

        public int Calls { get; private set; }

        public ValueTask<SparseVector> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(_generate(text));
        }
    }

    private static IngestionContext MakeContext()
    {
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            GetNextBm25DocId = () => 42,
        };
        var chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 };
        ctx.Chunks.Add(chunk);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new float[] { 1f } });
        return ctx;
    }

    private static SparseVector Sparse(int[] indices, float[] values) =>
        new() { Indices = indices, Values = values };

    private static ValueTask<IngestionResult> Next(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task SparseCapableStoreAndGenerator_PopulatesSparseVectors()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var vector = Sparse([1, 3], [0.5f, 0.7f]);
        var generator = new FakeSparseGenerator(_ => vector);

        var sut = new SparseEmbeddingBehavior { VectorStore = store, SparseGenerator = generator };
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, Next);

        Assert.NotNull(ctx.SparseVectors);
        Assert.Single(ctx.SparseVectors);
        Assert.Same(vector, ctx.SparseVectors[0]);
    }

    [Fact]
    public async Task NoGenerator_Transparent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();

        var sut = new SparseEmbeddingBehavior { VectorStore = store, SparseGenerator = null };
        var ctx = MakeContext();

        var nextCalled = false;
        await sut.HandleAsync(ctx, ct, (c, token) =>
        {
            nextCalled = true;
            return Next(c, token);
        });

        Assert.True(nextCalled);
        Assert.Null(ctx.SparseVectors);
    }

    [Fact]
    public async Task StoreNotSparseCapable_Transparent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore>(); // no ISparseSearchable
        var generator = new FakeSparseGenerator(_ => Sparse([1], [1f]));

        var sut = new SparseEmbeddingBehavior { VectorStore = store, SparseGenerator = generator };
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct, Next);

        Assert.Null(ctx.SparseVectors);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task GeneratorFails_DenseOnlyProceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var generator = new FakeSparseGenerator(_ => throw new InvalidOperationException("encoder failure"));

        var sut = new SparseEmbeddingBehavior { VectorStore = store, SparseGenerator = generator };
        var ctx = MakeContext();

        var nextCalled = false;
        await sut.HandleAsync(ctx, ct, (c, token) =>
        {
            nextCalled = true;
            return Next(c, token);
        });

        Assert.True(nextCalled);
        Assert.Null(ctx.SparseVectors);
    }

    [Fact]
    public async Task GeneratorCancellation_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var generator = new FakeSparseGenerator(_ => throw new OperationCanceledException());

        var sut = new SparseEmbeddingBehavior { VectorStore = store, SparseGenerator = generator };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.HandleAsync(MakeContext(), ct, Next).AsTask());
    }

    // ── StorageBehavior sparse persistence ───────────────────────────────────

    [Fact]
    public async Task StorageBehavior_SparseVectorsPresent_StoresSparseAlongsideDense()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior { VectorStore = store, Bm25Index = bm25 };
        var ctx = MakeContext();
        var vector = Sparse([1], [0.5f]);
        ctx.SparseVectors = [vector];

        var result = await sut.HandleAsync(ctx, ct,
            (_, _) => throw new InvalidOperationException("terminal — next must not be called"));

        await store.Received(1).StoreAsync(ctx.EmbeddedChunks, ct);
        await ((ISparseSearchable)store).Received(1).StoreSparseAsync(
            Arg.Is<IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)>>(items =>
                items!.Count == 1 &&
                ReferenceEquals(items[0].Chunk, ctx.EmbeddedChunks[0]) &&
                ReferenceEquals(items[0].Sparse, vector)),
            ct);
        Assert.Equal(1, result.ChunksStored);
    }

    [Fact]
    public async Task StorageBehavior_SparseStoreFails_DenseResultStillReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var bm25 = Substitute.For<IBm25Index>();
        ((ISparseSearchable)store).StoreSparseAsync(
                Arg.Any<IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("sparse storage down"));

        var sut = new StorageBehavior { VectorStore = store, Bm25Index = bm25 };
        var ctx = MakeContext();
        ctx.SparseVectors = [Sparse([1], [0.5f])];

        var result = await sut.HandleAsync(ctx, ct,
            (_, _) => throw new InvalidOperationException("terminal — next must not be called"));

        Assert.Equal(1, result.ChunksStored);
        await store.Received(1).StoreAsync(ctx.EmbeddedChunks, ct);
    }

    [Fact]
    public async Task StorageBehavior_NoSparseVectors_NeverTouchesSparseStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior { VectorStore = store, Bm25Index = bm25 };
        var ctx = MakeContext();

        await sut.HandleAsync(ctx, ct,
            (_, _) => throw new InvalidOperationException("terminal — next must not be called"));

        await ((ISparseSearchable)store).DidNotReceive().StoreSparseAsync(
            Arg.Any<IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StorageBehavior_SparseCountMismatch_SkipsSparseWithoutThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<IVectorStore, ISparseSearchable>();
        var bm25 = Substitute.For<IBm25Index>();

        var sut = new StorageBehavior { VectorStore = store, Bm25Index = bm25 };
        var ctx = MakeContext();
        ctx.SparseVectors = [Sparse([1], [0.5f]), Sparse([2], [0.5f])]; // 2 vectors, 1 chunk

        var result = await sut.HandleAsync(ctx, ct,
            (_, _) => throw new InvalidOperationException("terminal — next must not be called"));

        Assert.Equal(1, result.ChunksStored);
        await ((ISparseSearchable)store).DidNotReceive().StoreSparseAsync(
            Arg.Any<IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)>>(), Arg.Any<CancellationToken>());
    }
}
