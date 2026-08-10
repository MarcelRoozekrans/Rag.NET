using System.Diagnostics;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

[Collection("Telemetry")]
public sealed class GraphRagTelemetryTests : IAsyncDisposable
{
    private const string ExtractionJson = """
        {"entities":[{"name":"Microsoft","type":"Organization","description":"Tech company"}],"relationships":[{"source":"Microsoft","target":"GraphRAG","description":"developed","weight":1.0}]}
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    private static (List<Activity> Activities, ActivityListener Listener) CreateListener()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    /// <summary>
    /// Proves the nesting constraint (Task 8, hard constraint 2): a satellite span opened while a
    /// core span is the ambient <see cref="Activity.Current"/> becomes a genuine child of it, not
    /// merely a same-trace sibling. Starts an activity named like core's <c>ragnet.ingest</c> span
    /// to stand in for <c>PipelineIngestor</c> without needing the whole pipeline wired up.
    /// </summary>
    [Fact]
    public async Task Extract_NestsUnderIngestSpan()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var options = new GraphRagOptions { GleaningPasses = 0 };
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateIngestionContext();

        _chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, ExtractionJson)]));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                new(callInfo.Arg<IEnumerable<string>>()!.Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f })).ToList())));

        using var ingestSpan = new Activity("ragnet.ingest").Start();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var extractSpan = activities.Single(a => string.Equals(a.OperationName, "ragnet.graphrag.extract", StringComparison.Ordinal));
        Assert.Equal(ingestSpan.SpanId.ToString(), extractSpan.ParentSpanId.ToString());
        Assert.Equal("test-doc", extractSpan.GetTagItem("document.id"));
        Assert.Equal(1, extractSpan.GetTagItem("graphrag.entity.count"));
        Assert.Equal(1, extractSpan.GetTagItem("graphrag.relationship.count"));
    }

    [Fact]
    public async Task LocalSearch_EmitsSearchSpanWithLocalMode()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var graphStore = Substitute.For<IGraphStore>();
        graphStore.GetNeighborsAsync("Alice", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        graphStore.GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>()).Returns([]);
        graphStore.GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GraphLocalSearchBehavior(graphStore, new GraphRagRetrievalOptions { LocalTopEntities = 10, LocalSearchDepth = 1 });
        var ctx = new RetrievalContext { Query = "who is Alice?", Options = new RetrievalOptions() };
        var entityResult = new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = "Alice", DocumentId = new DocumentId("doc1"), ChunkIndex = 0,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["graph_type"] = "entity",
                    ["graph_entity_name"] = "Alice",
                },
            },
            Score = 0.9,
        };

        using var parent = new Activity("test-parent").Start();
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([entityResult]));

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.graphrag.search", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal("local", span.GetTagItem("graphrag.search.mode"));
        Assert.Equal(1, span.GetTagItem("graphrag.entity.count"));
    }

    private static IngestionContext CreateIngestionContext()
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text = "Microsoft developed GraphRAG.",
            DocumentId = new DocumentId("test-doc"),
            ChunkIndex = 0,
        });
        return ctx;
    }
}
