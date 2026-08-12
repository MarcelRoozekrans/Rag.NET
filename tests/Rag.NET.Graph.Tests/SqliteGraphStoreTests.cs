using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests;

public class SqliteGraphStoreTests : IAsyncDisposable
{
    private readonly SqliteGraphStore _store = new(":memory:");

    [Fact]
    public async Task AddEntitiesAsync_StoresAndRetrievesViaSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var entities = new[] { new GraphEntity("Microsoft", "Organization", "Tech company") };
        await _store.AddEntitiesAsync(entities, ct);
        var snapshot = await _store.GetFullGraphAsync(ct);
        Assert.Single(snapshot.Entities);
        Assert.Equal("Microsoft", snapshot.Entities[0].Name);
    }

    [Fact]
    public async Task AddRelationshipsAsync_StoresAndRetrievesViaSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A desc"), new GraphEntity("B", "Org", "B desc")], ct);
        await _store.AddRelationshipsAsync([new GraphRelationship("A", "B", "works with")], ct);
        var snapshot = await _store.GetFullGraphAsync(ct);
        Assert.Single(snapshot.Relationships);
        Assert.Equal("A", snapshot.Relationships[0].SourceEntity);
    }

    [Fact]
    public async Task GetNeighborsAsync_ReturnsDirectNeighbors()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([
            new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B"), new GraphEntity("C", "Org", "C")], ct);
        await _store.AddRelationshipsAsync([
            new GraphRelationship("A", "B", "r1"), new GraphRelationship("B", "C", "r2")], ct);

        var neighbors = await _store.GetNeighborsAsync("A", depth: 1, ct);
        Assert.Single(neighbors);
        Assert.Equal("B", neighbors[0].Name);
    }

    [Fact]
    public async Task GetNeighborsAsync_Depth2_ReturnsTwoHops()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([
            new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B"), new GraphEntity("C", "Org", "C")], ct);
        await _store.AddRelationshipsAsync([
            new GraphRelationship("A", "B", "r1"), new GraphRelationship("B", "C", "r2")], ct);

        var neighbors = await _store.GetNeighborsAsync("A", depth: 2, ct);
        Assert.Equal(2, neighbors.Count);
    }

    [Fact]
    public async Task GetRelationshipsAsync_ReturnsEdgesForEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B")], ct);
        await _store.AddRelationshipsAsync([new GraphRelationship("A", "B", "works with", 0.9)], ct);

        var rels = await _store.GetRelationshipsAsync("A", ct);
        Assert.Single(rels);
        Assert.Equal("works with", rels[0].Description);
    }

    [Fact]
    public async Task SetCommunitiesAsync_StoresAndRetrieves()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B")], ct);
        var communities = new[] { new Community(0, 0, ["A", "B"], "A and B are related") };
        await _store.SetCommunitiesAsync(communities, ct);

        var result = await _store.GetCommunitiesForEntityAsync("A", ct);
        Assert.Single(result);
        Assert.Equal("A and B are related", result[0].ReportSummary);
    }

    [Fact]
    public async Task DeleteByDocumentIdAsync_RemovesEntitiesAndRelationships()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A") { SourceDocumentId = "doc1" }], ct);
        await _store.AddRelationshipsAsync([new GraphRelationship("A", "B", "r1") { SourceDocumentId = "doc1" }], ct);

        await _store.DeleteByDocumentIdAsync("doc1", ct);

        var snapshot = await _store.GetFullGraphAsync(ct);
        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Relationships);
    }

    [Fact]
    public async Task AddEntitiesAsync_DuplicateName_MergesDescriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "First description")], ct);
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "Second description")], ct);

        var snapshot = await _store.GetFullGraphAsync(ct);
        Assert.Single(snapshot.Entities);
        Assert.Contains("First description", snapshot.Entities[0].Description, StringComparison.Ordinal);
        Assert.Contains("Second description", snapshot.Entities[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetNeighborsAsync_CircularGraph_DoesNotInfiniteLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([
            new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B"), new GraphEntity("C", "Org", "C")], ct);
        await _store.AddRelationshipsAsync([
            new GraphRelationship("A", "B", "r1"), new GraphRelationship("B", "C", "r2"), new GraphRelationship("C", "A", "r3")], ct);

        var neighbors = await _store.GetNeighborsAsync("A", depth: 3, ct);
        // Should not infinite loop; should return B and C (no duplicates)
        Assert.Equal(2, neighbors.Count);
    }

    [Fact]
    public async Task GetNeighborsAsync_CaseInsensitive_FindsEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("Microsoft", "Org", "Tech")], ct);
        await _store.AddRelationshipsAsync([new GraphRelationship("Microsoft", "Google", "competes with")], ct);
        await _store.AddEntitiesAsync([new GraphEntity("Google", "Org", "Search")], ct);

        var neighbors = await _store.GetNeighborsAsync("microsoft", depth: 1, ct);
        Assert.Single(neighbors);
    }

    [Fact]
    public async Task GetNeighborsAsync_NonExistentEntity_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var neighbors = await _store.GetNeighborsAsync("DoesNotExist", depth: 1, ct);
        Assert.Empty(neighbors);
    }

    [Fact]
    public async Task GetRelationshipsAsync_NonExistentEntity_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var rels = await _store.GetRelationshipsAsync("DoesNotExist", ct);
        Assert.Empty(rels);
    }

    [Fact]
    public async Task GetCommunitiesForEntityAsync_NonExistentEntity_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var communities = await _store.GetCommunitiesForEntityAsync("DoesNotExist", ct);
        Assert.Empty(communities);
    }

    /// <summary>
    /// What the store joins, the clusterer clusters — over the store's own snapshot.
    /// </summary>
    /// <remarks>
    /// <b>The two unit tests for this live beside the algorithms; this one is here because it is the
    /// production path.</b> <see cref="Leiden"/> and <see cref="PageRank"/> never see a hand-built
    /// snapshot in real use — they see <see cref="SqliteGraphStore.GetFullGraphAsync"/>'s, and the
    /// defect was precisely that they disagreed with it about what an entity name is. Asserting the
    /// store's traversal and the clusterer's grouping in one test means a later change to the
    /// <c>COLLATE NOCASE</c> schema cannot drift away from <see cref="GraphNames.Comparer"/>
    /// unnoticed: whichever side moves, this goes red.
    /// </remarks>
    [Fact]
    public async Task GetFullGraphAsync_MiscasedEndpoint_IsAnEdgeToBothStoreAndClusterer()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync(
            [
                new GraphEntity("Google", "Organisation", "A search company"),
                new GraphEntity("Alphabet", "Organisation", "Its holding company"),
            ],
            ct);
        await _store.AddRelationshipsAsync(
            [new GraphRelationship("google", "Alphabet", "subsidiary of")], ct);

        var neighbors = await _store.GetNeighborsAsync("Google", 1, ct);
        var communities = Leiden.Detect(await _store.GetFullGraphAsync(ct));

        Assert.Single(neighbors);
        Assert.Single(communities);
        Assert.Equal(2, communities[0].MemberEntities.Count);
    }

    /// <summary>Writing a score writes the score and nothing else.</summary>
    /// <remarks>
    /// The description half is the point. <see cref="SqliteGraphStore.AddEntitiesAsync"/> merges by
    /// appending, so persisting a score through it appended each entity's description to itself;
    /// this method exists so that a caller updating one column touches one column.
    /// </remarks>
    [Fact]
    public async Task SetPageRankScoresAsync_UpdatesScoreAndLeavesDescriptionIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddEntitiesAsync([new GraphEntity("Google", "Org", "A search company")], ct);

        await _store.SetPageRankScoresAsync(
            new Dictionary<string, double>(StringComparer.Ordinal) { ["google"] = 0.75 }, ct);

        var snapshot = await _store.GetFullGraphAsync(ct);
        Assert.Equal(0.75, snapshot.Entities[0].PageRankScore);
        Assert.Equal("A search company", snapshot.Entities[0].Description);
    }

    /// <summary>A score for an entity the store never had is ignored, not invented.</summary>
    [Fact]
    public async Task SetPageRankScoresAsync_UnknownEntity_StoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.SetPageRankScoresAsync(
            new Dictionary<string, double>(StringComparer.Ordinal) { ["Nobody"] = 0.5 }, ct);

        var snapshot = await _store.GetFullGraphAsync(ct);
        Assert.Empty(snapshot.Entities);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
