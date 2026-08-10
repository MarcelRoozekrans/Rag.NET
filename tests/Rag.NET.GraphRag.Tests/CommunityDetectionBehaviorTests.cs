using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class CommunityDetectionBehaviorTests : IAsyncDisposable
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    [Fact]
    public async Task HandleAsync_WhenDisabled_SkipsCommunityDetection()
    {
        var options = new GraphRagOptions { Enabled = false };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        Assert.True(nextCalled);
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyGraph_SkipsCommunityDetection()
    {
        var options = new GraphRagOptions { Enabled = true };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        Assert.True(nextCalled);
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RunsLeidenAndStoresCommunities()
    {
        var options = new GraphRagOptions { Enabled = true };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        await PopulateGraphStore();
        SetupChatClient("Community report text");
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(snapshot.Communities);
        // Leiden should detect at least 2 communities from the two cliques
        Assert.True(snapshot.Communities.Count >= 2);
    }

    [Fact]
    public async Task HandleAsync_GeneratesCommunityReports()
    {
        var options = new GraphRagOptions { Enabled = true };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        await PopulateGraphStore();
        SetupChatClient("Generated report for community");
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        // LLM should be called once per community
        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        await _chatClient.Received(snapshot.Communities.Count).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // All communities should have report summaries
        for (int i = 0; i < snapshot.Communities.Count; i++)
        {
            Assert.Equal("Generated report for community", snapshot.Communities[i].ReportSummary);
        }
    }

    [Fact]
    public async Task HandleAsync_EmbedsCommunityReports()
    {
        var options = new GraphRagOptions { Enabled = true };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        await PopulateGraphStore();
        SetupChatClient("Report text");
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var communityChunks = ctx.EmbeddedChunks
            .Where(ec => ec.Chunk.Metadata.TryGetValue("graph_type", out var t)
                && t == "community_report")
            .ToList();

        Assert.NotEmpty(communityChunks);

        // Each community chunk should have the expected metadata
        foreach (var chunk in communityChunks)
        {
            Assert.True(chunk.Chunk.Metadata.ContainsKey("community_id"));
            Assert.True(chunk.Chunk.Metadata.ContainsKey("community_level"));
            Assert.Equal("Report text", chunk.Chunk.Text);
            Assert.False(chunk.Embedding.IsEmpty);
        }
    }

    [Fact]
    public async Task HandleAsync_UsesCustomSummarizationClient()
    {
        var customClient = Substitute.For<IChatClient>();
        var options = new GraphRagOptions { Enabled = true, SummarizationChatClient = customClient };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        await PopulateGraphStore();

        customClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Custom report")]));
        SetupEmbedder(4);

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await customClient.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IngestionContext CreateContext()
    {
        return new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
    }

    private async Task PopulateGraphStore()
    {
        // Two cliques of entities to ensure Leiden finds communities
        await _graphStore.AddEntitiesAsync([
            new GraphEntity("A1", "Org", "Company A1"), new GraphEntity("A2", "Org", "Company A2"),
            new GraphEntity("A3", "Org", "Company A3"), new GraphEntity("A4", "Org", "Company A4"),
            new GraphEntity("B1", "Org", "Company B1"), new GraphEntity("B2", "Org", "Company B2"),
            new GraphEntity("B3", "Org", "Company B3"), new GraphEntity("B4", "Org", "Company B4"),
        ]);
        // Fully connect each clique
        var rels = new List<GraphRelationship>();
        string[] groupA = ["A1", "A2", "A3", "A4"];
        string[] groupB = ["B1", "B2", "B3", "B4"];
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
            {
                rels.Add(new GraphRelationship(groupA[i], groupA[j], "works with"));
                rels.Add(new GraphRelationship(groupB[i], groupB[j], "works with"));
            }
        await _graphStore.AddRelationshipsAsync(rels);
    }

    private void SetupChatClient(string response)
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

    private void SetupEmbedder(int dims)
    {
        _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
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
