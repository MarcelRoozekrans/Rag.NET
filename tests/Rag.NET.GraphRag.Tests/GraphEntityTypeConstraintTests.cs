using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// <see cref="GraphRagOptions.EntityTypes"/> and <see cref="GraphRagOptions.RelationshipTypes"/>
/// were documented as constraining extraction long before anything read them (issue #108).
/// These tests pin the two-layer implementation: the allowed list is substituted into the
/// prompt's <c>{entity_types}</c>/<c>{relationship_types}</c> placeholders, and out-of-list
/// extractions are filtered before they reach the graph store or the embedded chunks — because
/// an LLM does not perfectly obey a prompt constraint.
/// </summary>
public sealed class GraphEntityTypeConstraintTests : IAsyncDisposable
{
    private const string MixedTypesJson = """
        {"entities":[
            {"name":"Aspirin","type":"Drug","description":"Pain reliever"},
            {"name":"Bayer","type":"Organization","description":"Pharma company"}],
         "relationships":[
            {"source":"Bayer","target":"Aspirin","description":"manufactures","weight":1.0},
            {"source":"Aspirin","target":"Bayer","description":"generates revenue for","weight":0.5}]}
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    [Fact]
    public async Task HandleAsync_EntityTypesNull_RendersOpenExtractionGuidance()
    {
        var options = new GraphRagOptions { GleaningPasses = 0 };
        var prompts = CapturePrompts(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var prompt = Assert.Single(prompts);
        Assert.Contains(
            "Entity types should be general categories like: Person, Organization, Location, Event, Concept, Technology, Document.",
            prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Relationship descriptions should be concise verb phrases.",
            prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{entity_types}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{relationship_types}", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_EntityTypesSet_InjectsAllowedListIntoPrompt()
    {
        var options = new GraphRagOptions { GleaningPasses = 0, EntityTypes = ["Drug", "Gene"] };
        var prompts = CapturePrompts(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var prompt = Assert.Single(prompts);
        Assert.Contains(
            "Entity \"type\" must be exactly one of: Drug, Gene.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("general categories", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_RelationshipTypesSet_InjectsAllowedListIntoPrompt()
    {
        var options = new GraphRagOptions { GleaningPasses = 0, RelationshipTypes = ["manufactures"] };
        var prompts = CapturePrompts(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var prompt = Assert.Single(prompts);
        Assert.Contains(
            "Relationship \"description\" must be exactly one of: manufactures.",
            prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("concise verb phrases", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_EntityTypesSet_DropsOutOfListEntities()
    {
        var options = new GraphRagOptions { GleaningPasses = 0, EntityTypes = ["Drug"] };
        SetupChatClient(MixedTypesJson);
        SetupEmbedder();

        var ctx = await RunAsync(options);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal("Aspirin", entity.Name);

        var entityChunks = ctx.EmbeddedChunks
            .Where(ec => ec.Chunk.Metadata.TryGetValue("graph_type", out var t) &&
                         t == "entity")
            .ToList();
        var chunk = Assert.Single(entityChunks);
        Assert.Equal<MetadataValue>("Aspirin", chunk.Chunk.Metadata["graph_entity_name"]);
    }

    [Fact]
    public async Task HandleAsync_RelationshipTypesSet_DropsOutOfListRelationships()
    {
        var options = new GraphRagOptions { GleaningPasses = 0, RelationshipTypes = ["manufactures"] };
        SetupChatClient(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var relationship = Assert.Single(snapshot.Relationships);
        Assert.Equal("manufactures", relationship.Description);
    }

    [Fact]
    public async Task HandleAsync_CustomPromptWithoutPlaceholders_FilterStillApplies()
    {
        // A user-supplied prompt has no placeholder to steer through — the post-extraction
        // filter is the layer that still honours the documented constraint.
        var options = new GraphRagOptions
        {
            GleaningPasses = 0,
            EntityTypes = ["Drug"],
            EntityExtractionPrompt = "Extract entities and relationships as JSON from: {text}",
        };
        SetupChatClient(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal("Drug", entity.Type);
    }

    [Fact]
    public async Task HandleAsync_TypeFilter_IsCaseInsensitive()
    {
        var options = new GraphRagOptions { GleaningPasses = 0, EntityTypes = ["dRuG"] };
        SetupChatClient(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal("Aspirin", entity.Name);
    }

    [Fact]
    public async Task HandleAsync_EmptyTypeArrays_BehaveAsOpenExtraction()
    {
        // An empty list means "constrain to nothing", which would silently drop every
        // extraction — the defect class of issue #94. Empty therefore behaves like null.
        var options = new GraphRagOptions
        {
            GleaningPasses = 0,
            EntityTypes = [],
            RelationshipTypes = [],
        };
        SetupChatClient(MixedTypesJson);
        SetupEmbedder();

        await RunAsync(options);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Entities.Count);
        Assert.Equal(2, snapshot.Relationships.Count);
    }

    [Fact]
    public async Task HandleAsync_GleanedResults_AreFilteredToo()
    {
        const string initialJson = """
            {"entities":[{"name":"Aspirin","type":"Drug","description":"Pain reliever"}],"relationships":[]}
            """;
        const string gleanedJson = """
            {"entities":[{"name":"Leverkusen","type":"Location","description":"Bayer HQ city"}],"relationships":[]}
            """;
        var options = new GraphRagOptions { GleaningPasses = 1, EntityTypes = ["Drug"] };
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, initialJson)]),
                new ChatResponse([new ChatMessage(ChatRole.Assistant, gleanedJson)]));
        SetupEmbedder();

        await RunAsync(options);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal("Aspirin", entity.Name);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<IngestionContext> RunAsync(GraphRagOptions options)
    {
        var sut = new GraphEntityExtractionBehavior(_chatClient, _embedder, _graphStore, options);
        var ctx = CreateContext();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        return ctx;
    }

    private static IngestionContext CreateContext()
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };

        ctx.Chunks.Add(new TextChunk
        {
            Text = "Bayer manufactures Aspirin.",
            DocumentId = new DocumentId("test-doc"),
            ChunkIndex = 0,
        });

        return ctx;
    }

    private List<string> CapturePrompts(string response)
    {
        var prompts = new List<string>();
        _chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => prompts.Add(messages.First().Text)),
                Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
        return prompts;
    }

    private void SetupChatClient(string response)
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

    private void SetupEmbedder()
    {
        _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(new float[4])).ToList()));
            });
    }
}
