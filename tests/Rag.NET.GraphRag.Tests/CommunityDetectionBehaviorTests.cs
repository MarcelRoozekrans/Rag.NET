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

    /// <summary>
    /// Community detection persists PageRank scores without rewriting descriptions.
    /// </summary>
    /// <remarks>
    /// <b>It used to double every description in the graph, on every run.</b>
    /// <c>UpdateEntitiesWithPageRank</c> read the whole graph, set a score on each entity, and
    /// handed the entities back to <c>AddEntitiesAsync</c> — whose <c>ON CONFLICT</c> clause is
    /// <c>description = entities.description || char(10) || $description</c>. That concatenation is
    /// wanted and is pinned by <c>SqliteGraphStoreTests.AddEntitiesAsync_DuplicateName_MergesDescriptions</c>:
    /// two articles describing the same subject should merge into one entity. What was never wanted
    /// is a re-add of a row against itself, which appends a description to a copy of itself. So the
    /// score now goes through <c>SetPageRankScoresAsync</c>, which writes the one column it means
    /// to, and the merge path is left exactly as it was.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_RunTwice_DoesNotDuplicateEntityDescriptions()
    {
        var options = new GraphRagOptions { Enabled = true };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ct = TestContext.Current.CancellationToken;

        await PopulateGraphStore();
        SetupChatClient("Community report text");
        SetupEmbedder(4);

        for (var run = 0; run < 2; run++)
        {
            await sut.HandleAsync(CreateContext(), ct, (c, _) =>
                ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));
        }

        var snapshot = await _graphStore.GetFullGraphAsync(ct);
        foreach (var entity in snapshot.Entities)
        {
            Assert.Equal($"Company {entity.Name}", entity.Description);
        }
    }

    /// <summary>The scores community detection computed are the scores the store hands back.</summary>
    /// <remarks>
    /// The doubling fix must not become a silent drop: local search blends
    /// <see cref="GraphEntity.PageRankScore"/> read straight off the store, so a
    /// <c>SetPageRankScoresAsync</c> that wrote nothing would leave every entity at zero and look
    /// fine to every other assertion here.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_PersistsPageRankScores()
    {
        var options = new GraphRagOptions { Enabled = true };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ct = TestContext.Current.CancellationToken;

        await PopulateGraphStore();
        SetupChatClient("Community report text");
        SetupEmbedder(4);

        await sut.HandleAsync(CreateContext(), ct, (c, _) =>
            ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var snapshot = await _graphStore.GetFullGraphAsync(ct);
        Assert.All(snapshot.Entities, entity => Assert.True(entity.PageRankScore > 0.0));
    }

    /// <summary>The configured Leiden resolution actually reaches Leiden.</summary>
    /// <remarks>
    /// <b>The plumbing test above proves the option is stored; this one proves it is read.</b> That
    /// is the distinction #108 was written about: <c>GraphRagOptions.EntityTypes</c> was settable,
    /// documented and marked done for a release while being ignored at the point of use, and a test
    /// asserting only that the setter round-trips would have stayed green throughout. Resolution
    /// scales modularity's penalty term, so raising it splits communities that a lower one merges;
    /// two runs over the same graph must therefore disagree.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_HigherLeidenResolution_ProducesMoreCommunities()
    {
        SetupChatClient("Community report text");
        SetupEmbedder(4);
        await PopulateGraphStore();

        var coarse = await CountCommunitiesAtResolutionAsync(0.5);
        var fine = await CountCommunitiesAtResolutionAsync(5.0);

        Assert.True(
            fine > coarse,
            $"resolution 5.0 produced {fine} communities and 0.5 produced {coarse}; if they are " +
            "equal the setting is not reaching Leiden.Detect");
    }

    /// <summary>Runs detection once at one resolution and reports how many communities it found.</summary>
    private async Task<int> CountCommunitiesAtResolutionAsync(double resolution)
    {
        var options = new GraphRagOptions { Enabled = true };
        options.Leiden.Resolution = resolution;
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);
        var ct = TestContext.Current.CancellationToken;

        await sut.HandleAsync(CreateContext(), ct, (c, _) =>
            ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var snapshot = await _graphStore.GetFullGraphAsync(ct);

        return snapshot.Communities.Count;
    }

    /// <summary>The report prompt stays inside its configured budget.</summary>
    /// <remarks>
    /// <b>The prompt used to have no bound at all, so its size was a property of the corpus.</b>
    /// A sixty-article slice reached 1,806,352 characters — 450,000 tokens against a 128,000-token
    /// context. This asserts the code now decides, not the data: forty entities carrying a
    /// kilobyte of description each would render some 40,000 characters unbounded, and must not.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_LargeCommunity_BoundsTheReportPrompt()
    {
        const int budget = 4_000;
        var prompts = await CapturePromptsAsync(budget, entities: 40, descriptionLength: 1_000);

        Assert.NotEmpty(prompts);
        Assert.All(prompts, prompt => Assert.True(
            prompt.Length <= budget,
            $"a report prompt ran to {prompt.Length} characters against a budget of {budget}"));
    }

    /// <summary>Truncation is stated in the prompt rather than left for the model to infer.</summary>
    /// <remarks>
    /// <b>A silently shortened community is a lie told to the summarizer.</b> It would describe
    /// forty entities as though they were the whole cluster, and the report would read as complete
    /// while covering a fraction. The notice is the difference between a bound and a defect.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_TruncatedReport_SaysSoInThePrompt()
    {
        var prompts = await CapturePromptsAsync(budget: 4_000, entities: 40, descriptionLength: 1_000);

        Assert.Contains(prompts, p => p.Contains("entities are shown, most central first", StringComparison.Ordinal));
        Assert.Contains(prompts, p => p.Contains("were omitted to keep this report prompt", StringComparison.Ordinal));
    }

    /// <summary>What survives truncation is the most central entities, not an arbitrary prefix.</summary>
    /// <remarks>
    /// The hub of a star graph is the one entity a summary of it cannot omit. Emitting members in
    /// PageRank order is what makes the bound cost the least — dropping by store order would just
    /// as often drop the subject of the community.
    /// </remarks>
    [Fact]
    public async Task HandleAsync_TruncatedReport_KeepsTheMostCentralEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        await PopulateStarGraph(spokes: 30, descriptionLength: 400);
        SetupChatClient("report");
        SetupEmbedder(4);

        var prompts = CaptureChatPrompts();
        var options = new GraphRagOptions { Enabled = true, MaxCommunityReportPromptLength = 2_000 };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);

        await sut.HandleAsync(CreateContext(), ct, (c, _) =>
            ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var hubPrompt = prompts.Find(p => p.Contains("- Hub (", StringComparison.Ordinal));
        Assert.NotNull(hubPrompt);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Runs detection over one padded clique and returns the prompts it built.</summary>
    private async Task<List<string>> CapturePromptsAsync(int budget, int entities, int descriptionLength)
    {
        var ct = TestContext.Current.CancellationToken;
        await PopulateClique(entities, descriptionLength);
        SetupChatClient("report");
        SetupEmbedder(4);

        var prompts = CaptureChatPrompts();
        var options = new GraphRagOptions { Enabled = true, MaxCommunityReportPromptLength = budget };
        var sut = new CommunityDetectionBehavior(_chatClient, _embedder, _graphStore, options);

        await sut.HandleAsync(CreateContext(), ct, (c, _) =>
            ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        return prompts;
    }

    /// <summary>Records the text of every prompt the chat client is handed.</summary>
    private List<string> CaptureChatPrompts()
    {
        var prompts = new List<string>();
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                foreach (var message in callInfo.Arg<IEnumerable<ChatMessage>>()!)
                {
                    prompts.Add(message.Text);
                }

                return new ChatResponse([new ChatMessage(ChatRole.Assistant, "report")]);
            });

        return prompts;
    }

    /// <summary>A fully connected clique whose descriptions are deliberately bulky.</summary>
    private async Task PopulateClique(int count, int descriptionLength)
    {
        var padding = new string('x', descriptionLength);
        var entities = new List<GraphEntity>(count);
        for (int i = 0; i < count; i++)
        {
            entities.Add(new GraphEntity($"E{i}", "Org", $"Entity {i} {padding}"));
        }

        await _graphStore.AddEntitiesAsync(entities);

        var rels = new List<GraphRelationship>();
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                rels.Add(new GraphRelationship($"E{i}", $"E{j}", "works with"));

        await _graphStore.AddRelationshipsAsync(rels);
    }

    /// <summary>A star: one hub every spoke points at, so PageRank has a clear winner.</summary>
    private async Task PopulateStarGraph(int spokes, int descriptionLength)
    {
        var padding = new string('y', descriptionLength);
        var entities = new List<GraphEntity> { new("Hub", "Org", $"The hub {padding}") };
        var rels = new List<GraphRelationship>();

        for (int i = 0; i < spokes; i++)
        {
            entities.Add(new GraphEntity($"Spoke{i:D2}", "Org", $"Spoke {i} {padding}"));
            rels.Add(new GraphRelationship($"Spoke{i:D2}", "Hub", "reports to"));
        }

        await _graphStore.AddEntitiesAsync(entities);
        await _graphStore.AddRelationshipsAsync(rels);
    }


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
