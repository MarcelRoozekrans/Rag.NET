using Rag.NET.Graph;
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

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
