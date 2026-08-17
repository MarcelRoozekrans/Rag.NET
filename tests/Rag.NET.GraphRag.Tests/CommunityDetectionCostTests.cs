using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Pins how often <see cref="CommunityDetectionBehavior"/> recomputes the whole graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>This documents a defect (#300) rather than a guarantee.</b> The behaviour is an
/// <c>IIngestionBehavior</c> and <see cref="IngestionContext"/> carries one <c>Stream</c> and one
/// <c>Metadata</c> — it is per document. Each invocation loads the entire graph, runs Leiden and
/// PageRank over it, and writes every score back, with no guard beyond <c>Enabled</c> and an
/// empty-graph early return.
/// </para>
/// <para>
/// So ingesting N documents does N whole-graph loads and N detections against a graph that grows
/// throughout. On the MultiHop-RAG corpus that is 17,648 documents against a graph reaching 62,392
/// entities and 147,021 relationships.
/// </para>
/// <para>
/// <b>Every run but the last is superseded.</b> Detection is a pure function of the graph, and each
/// run overwrites the previous one's communities and scores — so the final state after the last
/// document is identical to running detection once at the end. The intermediate work is not
/// approximate or partial; it is discarded.
/// </para>
/// <para>
/// Committed as a counting test rather than a timing one on purpose: the count is deterministic and
/// the cost is not. Whichever option #300 takes, this is the assertion that has to change, and it
/// names what changed.
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

    [Fact]
    public async Task EveryIngestedDocumentReloadsAndRecomputesTheWholeGraph()
    {
        SetupModels();
        await SeedTwoCliquesAsync();
        var counting = new CountingGraphStore(_inner);
        var sut = new CommunityDetectionBehavior(
            _chatClient, _embedder, counting, new GraphRagOptions { Enabled = true });

        const int documents = 5;
        for (var i = 0; i < documents; i++)
        {
            await sut.HandleAsync(
                CreateContext($"doc-{i}"),
                TestContext.Current.CancellationToken,
                (c, ct) => ValueTask.FromResult(
                    new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));
        }

        // One whole-graph load per document, and one full score write-back per document. Both are
        // O(graph), so the ingest is O(documents x graph).
        Assert.Equal(documents, counting.FullGraphLoads);
        Assert.Equal(documents, counting.PageRankWriteBacks);
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
