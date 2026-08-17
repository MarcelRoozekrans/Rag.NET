using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Pins how often <see cref="CommunityDetectionBehavior"/> recomputes the whole graph, and that the
/// on-demand rebuild ignores the threshold.
/// </summary>
/// <remarks>
/// <para>
/// <b>#300.</b> Detection is a whole-graph operation — load the graph, run Leiden and PageRank over
/// it, write every score back — on a per-document ingestion behaviour. It therefore ran once per
/// ingested document against a graph growing throughout: on a 17,648-document corpus, 17,648
/// whole-graph recomputes.
/// </para>
/// <para>
/// <b>Nothing was gained by the repetition</b>, and <see cref="FiveRunsLeaveTheSameCommunitiesAsOne"/>
/// is what establishes that: detection is a pure function of the graph and each run overwrites the
/// last, so five runs leave exactly what one leaves. That test is the justification for debouncing
/// rather than a description of it — without it, skipping detections would be trading correctness
/// for speed instead of dropping work that was already being thrown away.
/// </para>
/// <para>
/// Counting tests rather than timing ones: the count is deterministic and the cost is not.
/// </para>
/// </remarks>
public sealed class CommunityDetectionCostTests : IAsyncDisposable
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _inner = new(":memory:");

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>
    /// Makes the substitutes answer, so detection reaches report generation instead of throwing.
    /// </summary>
    /// <remarks>
    /// Without this the substitute <c>IChatClient</c> returns null and <c>GenerateCommunityReports</c>
    /// throws a <see cref="NullReferenceException"/> — which would fail these tests for a reason that
    /// has nothing to do with how often detection runs.
    /// </remarks>
    private void SetupModels()
    {
        _ = _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "A summary of the community.")]));

        _ = _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })).ToList()));
            });
    }

    /// <remarks>
    /// The default threshold is a 10% growth in entity count. These five documents add no entities
    /// after the seed, so exactly one detection should happen — the first.
    /// </remarks>
    [Fact]
    public async Task TheGrowthThresholdStopsEveryDocumentRecomputing()
    {
        SetupModels();
        await SeedTwoCliquesAsync();
        var counting = new CountingGraphStore(_inner);
        var sut = new CommunityDetectionBehavior(
            _chatClient, _embedder, counting, new GraphRagOptions { Enabled = true });

        for (var i = 0; i < 5; i++)
        {
            await RunOnceAsync(sut, $"doc-{i}");
        }

        // One detection, not five. The expensive half — Leiden, PageRank, the score write-back and
        // report generation — runs once.
        Assert.Equal(1, counting.PageRankWriteBacks);

        // The graph is still LOADED every document, and that is deliberate rather than an oversight:
        // the entity count is the growth signal and IGraphStore offers no cheaper count. Pinned so
        // the remaining cost is visible instead of being mistaken for zero.
        Assert.Equal(5, counting.FullGraphLoads);
    }

    /// <remarks>
    /// The escape hatch, checked in the direction that matters. A threshold of zero must restore the
    /// pre-#300 behaviour exactly, or the option is documentation rather than a setting.
    /// </remarks>
    [Fact]
    public async Task AThresholdOfZeroRestoresDetectionOnEveryDocument()
    {
        SetupModels();
        await SeedTwoCliquesAsync();
        var counting = new CountingGraphStore(_inner);
        var sut = new CommunityDetectionBehavior(
            _chatClient, _embedder, counting,
            new GraphRagOptions { Enabled = true, CommunityDetectionGrowthThreshold = 0 });

        for (var i = 0; i < 5; i++)
        {
            await RunOnceAsync(sut, $"doc-{i}");
        }

        Assert.Equal(5, counting.PageRankWriteBacks);
    }

    /// <remarks>
    /// <para>
    /// The other half of the trade. Debouncing is only safe because there is a way to say "make the
    /// projections current now", so the rebuild must run even when the threshold would have skipped.
    /// </para>
    /// <para>
    /// Sequenced deliberately: ingest first so the threshold is primed and would refuse, then
    /// rebuild. A rebuild tested on a fresh instance would pass without exercising the bypass at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RebuildRunsEvenWhenTheThresholdWouldSkip()
    {
        SetupModels();
        await SeedTwoCliquesAsync();
        var counting = new CountingGraphStore(_inner);
        var behaviour = new CommunityDetectionBehavior(
            _chatClient, _embedder, counting, new GraphRagOptions { Enabled = true });

        await RunOnceAsync(behaviour, "doc-0");   // primes the threshold
        await RunOnceAsync(behaviour, "doc-1");   // skipped
        Assert.Equal(1, counting.PageRankWriteBacks);

        using var vectorStore = new InMemoryVectorStore();
        var sut = new GraphProjectionRebuilder(behaviour, vectorStore);

        var communities = await sut.RebuildAsync(TestContext.Current.CancellationToken);

        Assert.True(communities > 0, "The rebuild detected no communities over a seeded graph.");
        Assert.Equal(2, counting.PageRankWriteBacks);
    }

    /// <remarks>
    /// Reports are addressable: they go under a synthetic document id rather than whichever article
    /// happened to trigger detection, so deleting that id removes exactly the reports.
    /// </remarks>
    [Fact]
    public async Task RebuildStoresReportsUnderTheSyntheticDocumentId()
    {
        SetupModels();
        await SeedTwoCliquesAsync();
        var behaviour = new CommunityDetectionBehavior(
            _chatClient, _embedder, _inner, new GraphRagOptions { Enabled = true });

        using var vectorStore = new InMemoryVectorStore();
        var sut = new GraphProjectionRebuilder(behaviour, vectorStore);

        _ = await sut.RebuildAsync(TestContext.Current.CancellationToken);

        var stored = await vectorStore.SearchAsync(
            new float[] { 0.1f, 0.2f, 0.3f },
            new SearchOptions { TopK = 100 },
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(stored);
        Assert.All(stored, r => Assert.Equal(
            GraphProjectionRebuilder.ReportDocumentId, r.Chunk.DocumentId.Value));
        Assert.All(stored, r => Assert.Equal(
            "community_report", r.Chunk.Metadata["graph_type"].ToString()));
    }

    /// <remarks>
    /// The claim that only the last run matters, made checkable. Five runs and one run leave the
    /// same communities behind, so four fifths of the work here changed nothing observable.
    /// </remarks>
    [Fact]
    public async Task FiveRunsLeaveTheSameCommunitiesAsOne()
    {
        SetupModels();
        await SeedTwoCliquesAsync();
        var sut = new CommunityDetectionBehavior(
            _chatClient, _embedder, _inner, new GraphRagOptions { Enabled = true });

        await RunOnceAsync(sut, "doc-0");
        var afterOne = await SnapshotCommunitiesAsync();

        for (var i = 1; i < 5; i++)
        {
            await RunOnceAsync(sut, $"doc-{i}");
        }

        Assert.Equal(afterOne, await SnapshotCommunitiesAsync(), StringComparer.Ordinal);
    }

    private async Task RunOnceAsync(CommunityDetectionBehavior sut, string documentId) =>
        await sut.HandleAsync(
            CreateContext(documentId),
            TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

    /// <summary>Community membership as a comparable, order-independent shape.</summary>
    private async Task<List<string>> SnapshotCommunitiesAsync()
    {
        var graph = await _inner.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var members = new List<string>();
        foreach (var entity in graph.Entities)
        {
            var communities = await _inner.GetCommunitiesForEntityAsync(
                entity.Name, TestContext.Current.CancellationToken);
            foreach (var community in communities)
            {
                members.Add($"{entity.Name}->{community.Id}@{community.Level}");
            }
        }

        members.Sort(StringComparer.Ordinal);
        return members;
    }

    private async Task SeedTwoCliquesAsync()
    {
        await _inner.AddEntitiesAsync([
            new GraphEntity("A1", "Org", "Company A1"), new GraphEntity("A2", "Org", "Company A2"),
            new GraphEntity("A3", "Org", "Company A3"), new GraphEntity("A4", "Org", "Company A4"),
            new GraphEntity("B1", "Org", "Company B1"), new GraphEntity("B2", "Org", "Company B2"),
            new GraphEntity("B3", "Org", "Company B3"), new GraphEntity("B4", "Org", "Company B4"),
        ]);

        // Fixed bounds rather than group.Length, matching CommunityDetectionBehaviorTests: the
        // Hyperlinq analyser (HLQ013) rejects indexed iteration expressed against an array's Length,
        // and a pairwise clique needs the indices.
        var relationships = new List<GraphRelationship>();
        string[] groupA = ["A1", "A2", "A3", "A4"];
        string[] groupB = ["B1", "B2", "B3", "B4"];
        for (var i = 0; i < 4; i++)
        {
            for (var j = i + 1; j < 4; j++)
            {
                relationships.Add(new GraphRelationship(groupA[i], groupA[j], "works with"));
                relationships.Add(new GraphRelationship(groupB[i], groupB[j], "works with"));
            }
        }

        await _inner.AddRelationshipsAsync(relationships);
    }

    private static IngestionContext CreateContext(string documentId) => new()
    {
        Stream = Stream.Null,
        Metadata = new DocumentMetadata { DocumentId = new DocumentId(documentId), FileName = "test.txt" },
        GetNextBm25DocId = () => 0,
    };

    /// <summary>Delegates to a real store and counts the two whole-graph operations.</summary>
    /// <remarks>
    /// A decorator over the real <see cref="SqliteGraphStore"/> rather than a substitute, so Leiden
    /// and PageRank run against a real graph and the counts describe real work rather than a
    /// scripted one.
    /// </remarks>
    private sealed class CountingGraphStore(IGraphStore inner) : IGraphStore
    {
        public int FullGraphLoads { get; private set; }

        public int PageRankWriteBacks { get; private set; }

        public Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default)
        {
            FullGraphLoads++;
            return inner.GetFullGraphAsync(ct);
        }

        public Task SetPageRankScoresAsync(IReadOnlyDictionary<string, double> scores, CancellationToken ct = default)
        {
            PageRankWriteBacks++;
            return inner.SetPageRankScoresAsync(scores, ct);
        }

        public Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default) =>
            inner.AddEntitiesAsync(entities, ct);

        public Task AddRelationshipsAsync(IReadOnlyList<GraphRelationship> relationships, CancellationToken ct = default) =>
            inner.AddRelationshipsAsync(relationships, ct);

        public Task<IReadOnlyList<GraphEntity>> GetNeighborsAsync(string entityName, int depth, CancellationToken ct = default) =>
            inner.GetNeighborsAsync(entityName, depth, ct);

        public Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(string entityName, CancellationToken ct = default) =>
            inner.GetRelationshipsAsync(entityName, ct);

        public Task SetCommunitiesAsync(IReadOnlyList<Community> communities, CancellationToken ct = default) =>
            inner.SetCommunitiesAsync(communities, ct);

        public Task<IReadOnlyList<Community>> GetCommunitiesForEntityAsync(string entityName, CancellationToken ct = default) =>
            inner.GetCommunitiesForEntityAsync(entityName, ct);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default) =>
            inner.DeleteByDocumentIdAsync(documentId, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
