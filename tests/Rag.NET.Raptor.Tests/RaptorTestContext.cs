using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// Shared NSubstitute fixtures and context-building helpers for RAPTOR ingestion tests. Promoted
/// out of <see cref="RaptorIngestionBehaviorTests"/> so every later test class exercising
/// <see cref="RaptorIngestionBehavior"/> shares the same setup rather than growing its own copy.
/// </summary>
internal sealed class RaptorTestContext
{
    internal IChatClient ChatClient { get; } = Substitute.For<IChatClient>();

    internal IEmbeddingGenerator<string, Embedding<float>> Embedder { get; } =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    internal IngestionContext CreateContext(int chunkCount, int embeddingDims = 8, string documentId = "test-doc")
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(documentId), FileName = "test.txt", ContentType = "text/plain" },
            GetNextBm25DocId = () => 0,
        };

        var rng = new Random(42);
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = new TextChunk
            {
                Text = $"Chunk {i} content about topic {i % 3}",
                DocumentId = new DocumentId(documentId),
                ChunkIndex = i,
            };
            var embedding = GenerateEmbedding(rng, embeddingDims, topic: i % 3);
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new ReadOnlyMemory<float>(embedding) });
        }

        return ctx;
    }

    internal void SetupChatClient(string response)
    {
        ChatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

#pragma warning disable HLQ013 // Use foreach — need index-based assignment
    // Offset per topic rather than uniform over [0,1): the chunk text already claims to be
    // "about topic {i % 3}", and an embedding that ignores the topic contradicts the text it is
    // supposed to embed. It also carries no cluster structure whatsoever, so once SelectK stopped
    // isolating every point into its own component (#333) BIC read a whole context as a single
    // Gaussian — correctly — and no tree level could form. Three tight, well-separated blobs make
    // the vectors agree with the text, so clustering has something real to find.
    private static float[] GenerateEmbedding(Random rng, int dims, int topic)
    {
        var embedding = new float[dims];
        for (var j = 0; j < embedding.Length; j++)
            embedding[j] = topic + (float)(rng.NextDouble() * 0.1);
        return embedding;
    }
#pragma warning restore HLQ013

    internal void SetupEmbedder(int dims)
    {
        // rng is captured by the closure, not recreated per call: a real embedder returns
        // different vectors for different inputs, and RAPTOR's summary embedding calls are
        // always single-item batches, so re-seeding on every call made every summary chunk at
        // every tree level embed to the identical vector — collapsing every level above the
        // leaves into indistinguishable points and making a tree deeper than 1 level
        // unreachable in tests (see #332 test coverage gap).
        var rng = new Random(123);
        Embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(
                        Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray())).ToList()));
            });
    }
}
