using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class EmbeddingBehaviorTests
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
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
                ContentType = "text/plain",
            },
            Progress = progress,
            GetNextBm25DocId = () => 42,
        };
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
}
