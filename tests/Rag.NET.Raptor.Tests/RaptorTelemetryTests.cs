using System.Diagnostics;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

[Collection("Telemetry")]
public class RaptorTelemetryTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    [Fact]
    public async Task HandleAsync_EmitsBuildAndSummarizeSpansNestedTogether()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.PerDocument, MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary of cluster");
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();

        var buildSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.raptor.build", StringComparison.Ordinal));
        Assert.NotNull(buildSpan);
        Assert.Equal("test-doc", buildSpan.GetTagItem("document.id"));
        Assert.Equal(1, buildSpan.GetTagItem("raptor.tree.depth"));

        var summarizeSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.raptor.summarize", StringComparison.Ordinal));
        Assert.NotNull(summarizeSpan);
        Assert.Equal(1, summarizeSpan.GetTagItem("raptor.tree.level"));
        Assert.Equal(6, summarizeSpan.GetTagItem("raptor.chunk.count"));
        // ragnet.raptor.summarize is emitted from inside the ragnet.raptor.build activity scope.
        Assert.Equal(buildSpan.SpanId.ToString(), summarizeSpan.ParentSpanId.ToString());
    }

    private static IngestionContext CreateContext(int chunkCount, int embeddingDims = 4)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), FileName = "test.txt", ContentType = "text/plain" },
            GetNextBm25DocId = () => 0,
        };

        var rng = new Random(42);
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = new TextChunk
            {
                Text = $"Chunk {i} content about topic {i % 3}",
                DocumentId = new DocumentId("test-doc"),
                ChunkIndex = i,
            };
            var embedding = GenerateEmbedding(rng, embeddingDims);
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new ReadOnlyMemory<float>(embedding) });
        }

        return ctx;
    }

#pragma warning disable HLQ013 // Use foreach — need index-based assignment
    private static float[] GenerateEmbedding(Random rng, int dims)
    {
        var embedding = new float[dims];
        for (var j = 0; j < embedding.Length; j++)
            embedding[j] = (float)rng.NextDouble();
        return embedding;
    }
#pragma warning restore HLQ013

    private void SetupChatClient(string response)
    {
        _chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

    private void SetupEmbedder(int dims)
    {
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                var rng = new Random(123);
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(
                        Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray())).ToList()));
            });
    }
}
