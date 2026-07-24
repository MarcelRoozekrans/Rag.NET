using System.Globalization;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class EmbeddingBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IngestionContext MakeContext(
        DocumentMetadata? metadata = null,
        IProgress<IngestionProgress>? progress = null,
        IngestionOptions? options = null)
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
            Progress = progress,
            Options = options,
            GetNextBm25DocId = () => 42,
        };
    }

    /// <summary>
    /// Embedder whose vector for text "cN" is [N]. Records each received batch
    /// (as an ordered list of texts) into <paramref name="batches"/> when provided.
    /// </summary>
    private static IEmbeddingGenerator<string, Embedding<float>> MakeIndexedEmbedder(
        List<string[]>? batches = null)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IEnumerable<string>>().ToArray();
                if (batches is not null)
                {
                    lock (batches) { batches.Add(texts); }
                }

                var embeddings = new List<Embedding<float>>(texts.Length);
                foreach (var text in texts)
                {
                    var index = int.Parse(text[1..], CultureInfo.InvariantCulture);
                    embeddings.Add(new Embedding<float>(new[] { (float)index }));
                }

                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });
        return embedder;
    }

    private static TextChunk MakeChunk(int index, string text, float[]? embedding = null) => new()
    {
        Text = text,
        DocumentId = new DocumentId("doc-1"),
        ChunkIndex = index,
        // NB: without the explicit nullable cast, the null branch converts via the
        // float[] implicit operator to an EMPTY (non-null) ReadOnlyMemory.
        Embedding = embedding is null ? (ReadOnlyMemory<float>?)null : new ReadOnlyMemory<float>(embedding),
    };

    private static ValueTask<IngestionResult> Next(IngestionContext ctx, CancellationToken _)
        => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task HandleAsync_PrecomputedEmbeddings_AreNotReEmbedded_AndOrderIsPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var pre0 = new float[] { 0.1f, 0.2f };
        var pre2 = new float[] { 0.5f, 0.6f };
        var generated1 = new float[] { 0.3f, 0.4f };

        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(generated1),
            ]));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext();
        ctx.Chunks.Add(MakeChunk(0, "precomputed-a", pre0));
        ctx.Chunks.Add(MakeChunk(1, "plain"));
        ctx.Chunks.Add(MakeChunk(2, "precomputed-b", pre2));

        await sut.HandleAsync(ctx, ct, Next);

        // Embedder receives exactly the plain chunk's text: one call, one item.
        await embedder.Received(1).GenerateAsync(
            Arg.Is<IEnumerable<string>>(texts => texts.SequenceEqual(new[] { "plain" })),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());

        // Order preserved.
        Assert.Equal(3, ctx.EmbeddedChunks.Count);
        for (var i = 0; i < ctx.EmbeddedChunks.Count; i++)
        {
            Assert.Equal(i, ctx.EmbeddedChunks[i].Chunk.ChunkIndex);
        }

        // Precomputed vectors used verbatim; plain chunk got the generated vector.
        Assert.Equal(pre0, ctx.EmbeddedChunks[0].Embedding.ToArray());
        Assert.Equal(generated1, ctx.EmbeddedChunks[1].Embedding.ToArray());
        Assert.Equal(pre2, ctx.EmbeddedChunks[2].Embedding.ToArray());
    }

    [Fact]
    public async Task HandleAsync_AllPrecomputed_EmbedderNeverCalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var pre0 = new float[] { 1f, 2f };
        var pre1 = new float[] { 3f, 4f };

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext();
        ctx.Chunks.Add(MakeChunk(0, "a", pre0));
        ctx.Chunks.Add(MakeChunk(1, "b", pre1));

        await sut.HandleAsync(ctx, ct, Next);

        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.Equal(2, ctx.EmbeddedChunks.Count);
        Assert.Equal(pre0, ctx.EmbeddedChunks[0].Embedding.ToArray());
        Assert.Equal(pre1, ctx.EmbeddedChunks[1].Embedding.ToArray());
    }

    [Fact]
    public async Task HandleAsync_EmptyEmbedding_IsTreatedAsAbsent_AndChunkIsEmbedded()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var generatedVec = new float[] { 0.7f, 0.8f };
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(generatedVec),
            ]));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext();
        ctx.Chunks.Add(new TextChunk
        {
            Text = "empty-embedding",
            DocumentId = new DocumentId("doc-1"),
            ChunkIndex = 0,
            Embedding = ReadOnlyMemory<float>.Empty,
        });

        await sut.HandleAsync(ctx, ct, Next);

        await embedder.Received(1).GenerateAsync(
            Arg.Is<IEnumerable<string>>(texts => texts.SequenceEqual(new[] { "empty-embedding" })),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.Single(ctx.EmbeddedChunks);
        Assert.Equal(generatedVec, ctx.EmbeddedChunks[0].Embedding.ToArray());
    }

    [Fact]
    public async Task HandleAsync_GeneratorReturnsWrongCount_ThrowsWithBothCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        // Two pending chunks, but the generator returns only one embedding.
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(new float[] { 1f }),
            ]));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext();
        ctx.Chunks.Add(MakeChunk(0, "a"));
        ctx.Chunks.Add(MakeChunk(1, "b"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(ctx, ct, Next).AsTask());

        Assert.Contains("returned 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 inputs", ex.Message, StringComparison.Ordinal);
    }

    // ── Chunk-batch embedding ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Batched_ReassemblesInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var batches = new List<string[]>();
        var embedder = MakeIndexedEmbedder(batches);

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(options: new IngestionOptions
        {
            EmbedBatchSize = 2,
            MaxConcurrentEmbeddingBatches = 1,
        });
        for (var i = 0; i < 5; i++)
        {
            ctx.Chunks.Add(MakeChunk(i, $"c{i}"));
        }

        await sut.HandleAsync(ctx, ct, Next);

        Assert.Equal(3, batches.Count);
        Assert.Equal(["c0", "c1"], batches[0]);
        Assert.Equal(["c2", "c3"], batches[1]);
        Assert.Equal(["c4"], batches[2]);

        Assert.Equal(5, ctx.EmbeddedChunks.Count);
        for (var i = 0; i < ctx.EmbeddedChunks.Count; i++)
        {
            Assert.Equal(i, ctx.EmbeddedChunks[i].Chunk.ChunkIndex);
            Assert.Equal(new[] { (float)i }, ctx.EmbeddedChunks[i].Embedding.ToArray());
        }
    }

    [Fact]
    public async Task HandleAsync_Batched_MixedPrecomputedAcrossBatchEdges()
    {
        var ct = TestContext.Current.CancellationToken;
        var batches = new List<string[]>();
        var embedder = MakeIndexedEmbedder(batches);

        var pre0 = new float[] { 100f };
        var pre3 = new float[] { 103f };

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(options: new IngestionOptions
        {
            EmbedBatchSize = 2,
            MaxConcurrentEmbeddingBatches = 1,
        });
        ctx.Chunks.Add(MakeChunk(0, "c0", pre0));
        ctx.Chunks.Add(MakeChunk(1, "c1"));
        ctx.Chunks.Add(MakeChunk(2, "c2"));
        ctx.Chunks.Add(MakeChunk(3, "c3", pre3));
        ctx.Chunks.Add(MakeChunk(4, "c4"));

        await sut.HandleAsync(ctx, ct, Next);

        // Pending chunks are c1, c2, c4 → batches of 2: ["c1","c2"], ["c4"].
        Assert.Equal(2, batches.Count);
        Assert.Equal(["c1", "c2"], batches[0]);
        Assert.Equal(["c4"], batches[1]);

        // Precomputed vectors untouched; generated vectors land at their original indices.
        Assert.Equal(5, ctx.EmbeddedChunks.Count);
        Assert.Equal(pre0, ctx.EmbeddedChunks[0].Embedding.ToArray());
        Assert.Equal(new[] { 1f }, ctx.EmbeddedChunks[1].Embedding.ToArray());
        Assert.Equal(new[] { 2f }, ctx.EmbeddedChunks[2].Embedding.ToArray());
        Assert.Equal(pre3, ctx.EmbeddedChunks[3].Embedding.ToArray());
        Assert.Equal(new[] { 4f }, ctx.EmbeddedChunks[4].Embedding.ToArray());

        for (var i = 0; i < ctx.EmbeddedChunks.Count; i++)
        {
            Assert.Equal(i, ctx.EmbeddedChunks[i].Chunk.ChunkIndex);
        }
    }

    [Fact]
    public async Task HandleAsync_Batched_CountMismatchInSecondBatch_ThrowsWithBatchOrdinal()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        // First batch ["c0","c1"] is fine; second batch ["c2","c3"] returns only one embedding.
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IEnumerable<string>>().ToArray();
                var count = Array.Exists(texts, t => string.Equals(t, "c2", StringComparison.Ordinal)) ? 1 : texts.Length;
                var embeddings = new List<Embedding<float>>();
                for (var i = 0; i < count; i++)
                {
                    embeddings.Add(new Embedding<float>(new[] { 0f }));
                }

                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(options: new IngestionOptions
        {
            EmbedBatchSize = 2,
            MaxConcurrentEmbeddingBatches = 1,
        });
        for (var i = 0; i < 4; i++)
        {
            ctx.Chunks.Add(MakeChunk(i, $"c{i}"));
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(ctx, ct, Next).AsTask());

        Assert.Contains("returned 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 inputs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("batch 2/2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("doc-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_Batched_ConcurrencyBoundRespected()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new[] { NewTcs(), NewTcs(), NewTcs(), NewTcs(), NewTcs(), NewTcs() };
        var release = NewTcs();
        var probe = new ConcurrencyProbe();
        var embedder = MakeGatedEmbedder(started, release.Task, probe);

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(options: new IngestionOptions
        {
            EmbedBatchSize = 1,
            MaxConcurrentEmbeddingBatches = 2,
        });
        for (var i = 0; i < 6; i++)
        {
            ctx.Chunks.Add(MakeChunk(i, $"c{i}"));
        }

        var handleTask = sut.HandleAsync(ctx, ct, Next).AsTask();

        try
        {
            // (a) Two batches must start concurrently — deterministic proof that batching
            // actually parallelises. If the bound collapsed to 1, the second call could
            // never start while the gate is held and this wait would time out.
            await Task.WhenAll(started[0].Task, started[1].Task).WaitAsync(TimeSpan.FromSeconds(10), ct);

            // (b) While both in-flight calls are held at the gate, the bound of 2 must
            // prevent any third call from starting. No sleep needed: a third start would
            // have incremented the counter and signalled started[2] before blocking.
            Assert.Equal(2, Volatile.Read(ref probe.Started));
            Assert.False(started[2].Task.IsCompleted);
        }
        finally
        {
            // (c) Release the gate even when an assertion above fails, so the pipeline
            // drains instead of leaving handleTask blocked on the gate forever.
            release.TrySetResult();
        }

        await handleTask;

        Assert.Equal(2, probe.MaxInFlight);
        Assert.Equal(6, ctx.EmbeddedChunks.Count);
        for (var i = 0; i < ctx.EmbeddedChunks.Count; i++)
        {
            Assert.Equal(new[] { (float)i }, ctx.EmbeddedChunks[i].Embedding.ToArray());
        }

        await embedder.Received(6).GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ConcurrencyProbe
    {
        public int Started;
        public int InFlight;
        public int MaxInFlight;
    }

    /// <summary>
    /// Embedder whose calls signal a per-call "started" TCS (in arrival order) and then
    /// block on <paramref name="release"/>. Vector for text "cN" is [N].
    /// </summary>
    private static IEmbeddingGenerator<string, Embedding<float>> MakeGatedEmbedder(
        TaskCompletionSource[] started, Task release, ConcurrencyProbe probe)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var texts = call.Arg<IEnumerable<string>>().ToArray();
                var current = Interlocked.Increment(ref probe.InFlight);
                InterlockedMax(ref probe.MaxInFlight, current);
                started[Interlocked.Increment(ref probe.Started) - 1].SetResult();
                try
                {
                    await release;
                }
                finally
                {
                    Interlocked.Decrement(ref probe.InFlight);
                }

                var embeddings = new List<Embedding<float>>(texts.Length);
                foreach (var text in texts)
                {
                    var index = int.Parse(text[1..], CultureInfo.InvariantCulture);
                    embeddings.Add(new Embedding<float>(new[] { (float)index }));
                }

                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });
        return embedder;
    }

    [Fact]
    public async Task HandleAsync_SingleBatch_PathUnchanged_ExactlyOneGenerateCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = MakeIndexedEmbedder();

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = MakeContext(options: new IngestionOptions { EmbedBatchSize = 100 });
        for (var i = 0; i < 3; i++)
        {
            ctx.Chunks.Add(MakeChunk(i, $"c{i}"));
        }

        await sut.HandleAsync(ctx, ct, Next);

        await embedder.Received(1).GenerateAsync(
            Arg.Is<IEnumerable<string>>(texts => texts.SequenceEqual(new[] { "c0", "c1", "c2" })),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.Equal(3, ctx.EmbeddedChunks.Count);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
