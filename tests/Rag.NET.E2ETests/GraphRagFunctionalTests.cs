using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.GraphRag.LocalSearch;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Storage;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.E2ETests;

/// <summary>
/// The first end-to-end run of <c>Rag.NET.GraphRag</c>: ingest a tiny hand-written corpus,
/// then prove every stage produced something — entities, relationships, communities, local
/// and global search results, and a graph that survives reopening the store.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the package shipped marked done without ever being run end to end,
/// and the dead-settings sweep (#108) then found three documented behaviours that did not
/// exist — <c>EntityTypes</c>/<c>RelationshipTypes</c> doing nothing (#112),
/// <c>GraphRagGlobalSearchOptions.Mode</c> never read (#104), and three settings validated only
/// after they were caught silently disabling or hanging search (#103). None of that survives
/// one run of this class, which is the point: every stage here fails loudly when it produces
/// nothing, because a test that passes on an empty graph is how those defects lived so long.
/// </para>
/// <para>
/// Assertions are structural, not exact — LLM extraction is not deterministic, so the tests
/// assert what must hold if the pipeline works (the unmissable entities present, counts
/// non-zero, both search paths returning) rather than an exact entity set. The corpus is
/// hand-written and checked in so the right answer is known: three named people with
/// explicitly stated relationships that a working extractor cannot plausibly miss.
/// </para>
/// </remarks>
[Collection("Ollama")]
public sealed class GraphRagFunctionalTests : IAsyncLifetime
{
    private const string CorpusResourceName = "Rag.NET.E2ETests.Resources.graphrag.txt";

    private readonly OllamaFixture _ollama;
    private readonly string _docId = $"e2e-graphrag-{Guid.CreateVersion7():N}";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ragnet-graphrag-{Guid.CreateVersion7():N}.db");
    private ServiceProvider _sp = null!;
    private IRagPipeline _pipeline = null!;

    public GraphRagFunctionalTests(OllamaFixture ollama)
    {
        _ollama = ollama;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
            await _sp.DisposeAsync();

        // Microsoft.Data.Sqlite pools connections, so the file can still be held here.
        // Best-effort cleanup of a temp-directory file; a leftover is harmless.
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The full functional run with open extraction (the default): every stage must produce
    /// something, both search paths must return, and the graph must survive a store reopen.
    /// </summary>
    [Fact]
    public async Task GraphRag_OpenExtraction_EveryStageProducesOutput()
    {
        BuildPipeline();
        await IngestCorpusAsync();

        var snapshot = await _sp.GetRequiredService<IGraphStore>()
            .GetFullGraphAsync(TestContext.Current.CancellationToken);
        AssertGraphWasBuilt(snapshot);

        await AssertLocalSearchReturnsEntityResultsAsync();
        await AssertGlobalSearchSynthesizesAnswerAsync();
        await AssertGraphSurvivesReopeningTheStoreAsync();
    }

    /// <summary>
    /// <see cref="GraphRagOptions.EntityTypes"/> was dead until #112 — every run used open
    /// extraction regardless of configuration. #112 implemented it as prompt guidance plus a
    /// post-extraction filter, and the filter is what makes this assertable: whatever the LLM
    /// returns, nothing outside the allowed list may reach the graph store.
    /// </summary>
    [Fact]
    public async Task GraphRag_EntityTypesConstrained_StoresOnlyAllowedTypes()
    {
        BuildPipeline(o => o.EntityTypes = ["Person"]);
        await IngestCorpusAsync();

        var snapshot = await _sp.GetRequiredService<IGraphStore>()
            .GetFullGraphAsync(TestContext.Current.CancellationToken);

        Assert.True(
            snapshot.Entities.Count > 0,
            "Constrained extraction stored no entities. The corpus names three people, so a " +
            "working extractor cannot miss every Person — either extraction failed outright " +
            "or the EntityTypes filter is dropping allowed types.");

        Assert.All(snapshot.Entities, e => Assert.True(
            string.Equals(e.Type, "Person", StringComparison.OrdinalIgnoreCase),
            $"Entity '{e.Name}' has type '{e.Type}', outside the configured " +
            "EntityTypes = [\"Person\"] — the post-extraction filter (#112) is not enforcing " +
            "the constraint."));
    }

    /// <summary>
    /// The second pinned setting: <see cref="GraphRagOptions.Enabled"/> = false must make both
    /// graph behaviors pass through — an empty graph store, no graph chunks in retrieval — while
    /// plain ingestion and retrieval keep working. Deterministic, because disabling makes no
    /// LLM extraction calls at all.
    /// </summary>
    [Fact]
    public async Task GraphRag_Disabled_LeavesGraphEmptyWhilePipelineStillWorks()
    {
        BuildPipeline(o => o.Enabled = false);
        await IngestCorpusAsync();

        var snapshot = await _sp.GetRequiredService<IGraphStore>()
            .GetFullGraphAsync(TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Relationships);
        Assert.Empty(snapshot.Communities);

        var retrieval = await _pipeline.RetrieveAsync(
            "Who was Marie Curie?",
            new RetrievalOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.True(retrieval.IsSuccess, $"Retrieval failed with GraphRAG disabled: {retrieval}");
        Assert.True(
            retrieval.Value.Count > 0,
            "Retrieval returned nothing with GraphRAG disabled — the base pipeline broke.");
        Assert.DoesNotContain(
            retrieval.Value,
            r => r.Chunk.Metadata.ContainsKey("graph_type"));
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private void BuildPipeline(Action<GraphRagOptions>? configure = null)
    {
        var chatClient = TestChatClientFactory.Create(_ollama);
        var embeddingGenerator = _ollama.CreateEmbeddingGenerator("nomic-embed-text");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingGenerator);
        services.AddSingleton<IChatClient>(chatClient);
        services.AddSingleton<IVectorStore>(_ => new InMemoryVectorStore());
        services.AddRagNet(
            configure: rag => rag.UseGraphRag(
                configure,
                graph: g => g.UseSqlite(_dbPath)),
            ingestion: p => p
                .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
                .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior)),
            retrieval: p => p
                .Add<GraphGlobalSearchBehavior>(before: typeof(RerankingBehavior)));

        _sp = services.BuildServiceProvider();
        _pipeline = _sp.GetRequiredService<IRagPipeline>();
    }

    private async Task IngestCorpusAsync()
    {
        await using var stream = typeof(GraphRagFunctionalTests).Assembly
            .GetManifestResourceStream(CorpusResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{CorpusResourceName}' not found.");

        var result = await _pipeline.IngestAsync(
            stream,
            new DocumentMetadata
            {
                DocumentId = new DocumentId(_docId),
                FileName = "graphrag.txt",
                ContentType = "text/plain",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, $"IngestAsync failed: {result}");
        Assert.True(
            result.Value.ChunksStored > 0,
            "Ingestion stored no chunks — the pipeline produced an empty result, so every " +
            "assertion after this would pass vacuously.");
    }

    private static void AssertGraphWasBuilt(GraphSnapshot snapshot)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Graph built: {snapshot.Entities.Count} entities, " +
            $"{snapshot.Relationships.Count} relationships, " +
            $"{snapshot.Communities.Count} communities.");

        Assert.True(
            snapshot.Entities.Count > 0,
            "No entities were extracted. The corpus names Marie Curie, Pierre Curie and " +
            "Albert Einstein explicitly — an empty graph means extraction never ran or every " +
            "LLM response failed to parse.");

        AssertEntityPresent(snapshot, "Marie");
        AssertEntityPresent(snapshot, "Pierre");
        AssertEntityPresent(snapshot, "Einstein");

        Assert.True(
            snapshot.Relationships.Count > 0,
            "No relationships were extracted. The corpus states them outright (married, " +
            "worked at, discovered, shared the Nobel Prize) — zero means the relationship " +
            "half of extraction produced nothing.");

        Assert.True(
            snapshot.Communities.Count > 0,
            "Community detection produced no communities over a non-empty entity set — " +
            "CommunityDetectionBehavior did not run, or Leiden returned nothing.");
    }

    private static void AssertEntityPresent(GraphSnapshot snapshot, string nameFragment)
    {
        Assert.True(
            snapshot.Entities.Any(e =>
                e.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)),
            $"No extracted entity name contains '{nameFragment}'. The corpus states that " +
            "person's name repeatedly, so a working extractor cannot plausibly miss it. " +
            $"Extracted: [{string.Join(", ", snapshot.Entities.Select(e => e.Name))}]");
    }

    /// <remarks>
    /// Asserted against <see cref="IGraphRagSearch"/> directly, not <c>_pipeline.RetrieveAsync</c>.
    /// Local search is a service, not a retrieval-pipeline behaviour (<c>UseGraphRag</c> never
    /// places one by default), and its entity chunks live in their own <c>GraphChunkStore</c>
    /// (#247) — <c>GraphChunkRoutingBehavior</c> takes every graph-tagged chunk out of the batch
    /// before it reaches the document vector store <c>RetrieveAsync</c> searches. A chunk tagged
    /// <c>graph_type == "entity"</c> can never come back from that call; asking
    /// <see cref="IGraphRagSearch"/> for its own context window is what actually exercises local
    /// search.
    /// </remarks>
    private async Task AssertLocalSearchReturnsEntityResultsAsync()
    {
        var localSearch = _sp.GetRequiredService<IGraphRagSearch>();
        var context = await localSearch.BuildLocalContextAsync(
            "Who was Marie Curie and where did she work?",
            TestContext.Current.CancellationToken);

        Assert.True(
            context.Entities.Rendered > 0,
            "Local search selected and rendered no entities for a query naming Marie Curie " +
            "explicitly. Entity chunks live in GraphChunkStore, separate from the document " +
            "store (#247) — either entity-selection embedding found no match, or the context " +
            "builder's entity budget dropped every candidate it was offered.");
    }

    private async Task AssertGlobalSearchSynthesizesAnswerAsync()
    {
        var result = await _pipeline.RetrieveAsync(
            "What are the main themes across these documents?",
            new RetrievalOptions { TopK = 50 },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, $"Global-search retrieval failed: {result}");

        var globalAnswer = result.Value.FirstOrDefault(r =>
            r.Chunk.Metadata.TryGetValue("graph_type", out var gt) && gt == "global_answer");

        Assert.True(
            globalAnswer is not null,
            "Global search produced no synthesized answer. Either no community report was " +
            "retrieved (community detection or its embedding failed) or the map-reduce over " +
            "reports returned nothing.");
        Assert.False(
            string.IsNullOrWhiteSpace(globalAnswer!.Chunk.Text),
            "Global search synthesized an empty answer — the reduce phase returned no text.");
    }

    private async Task AssertGraphSurvivesReopeningTheStoreAsync()
    {
        // A second, independent connection to the same file: what was written must be
        // durable, not an artifact of the original connection's in-memory state.
        await using var reopened = new SqliteGraphStore(_dbPath);
        var snapshot = await reopened.GetFullGraphAsync(TestContext.Current.CancellationToken);

        Assert.True(
            snapshot.Entities.Count > 0 && snapshot.Relationships.Count > 0,
            "Reopening the SQLite graph store found no persisted graph — everything lived " +
            "only in the original connection.");
        Assert.True(
            snapshot.Communities.Count > 0,
            "Reopening the SQLite graph store found no persisted communities.");
    }
}
