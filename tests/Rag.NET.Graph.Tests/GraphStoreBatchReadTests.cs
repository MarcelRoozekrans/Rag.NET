using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.Graph.Tests;

/// <summary>
/// Covers the batch reads local search assembles its context from, and — the point of the file —
/// that <see cref="SqliteGraphStore"/>'s overrides agree with the interface defaults they replace.
/// </summary>
/// <remarks>
/// <para>
/// Each of <c>GetEntitiesAsync</c>, <c>GetDegreesAsync</c>, the batch <c>GetRelationshipsAsync</c>
/// and <c>GetCommunitiesForEntitiesAsync</c> ships twice: a default on <see cref="IGraphStore"/>
/// that is correct and slow, so implementations outside this repository keep working, and a real
/// query on the SQLite store. Two implementations of one contract disagree unless something checks,
/// and the disagreement would be silent — a context assembled from slightly different material,
/// with nothing to compare it against.
/// </para>
/// <para>
/// So <see cref="DefaultShim"/> forces the defaults to run against the same database and the
/// results are compared. It is the whole reason for the shim.
/// </para>
/// </remarks>
public sealed class GraphStoreBatchReadTests
{
    /// <summary>Seeds a small graph: three connected entities, one isolated, one community.</summary>
    /// <returns>The store.</returns>
    private static async Task<SqliteGraphStore> SeedAsync()
    {
        var store = new SqliteGraphStore(":memory:");
        var ct = TestContext.Current.CancellationToken;

        await store.AddEntitiesAsync(
        [
            new GraphEntity("Ångström", "Person", "Swedish physicist") { SourceChunkIds = ["doc1_0"] },
            new GraphEntity("Kelvin", "Person", "British physicist") { SourceChunkIds = ["doc1_1"] },
            new GraphEntity("Spectroscopy", "Field", "Study of spectra") { SourceChunkIds = ["doc1_0"] },
            new GraphEntity("Unrelated", "Thing", "Connected to nothing"),
        ], ct);

        await store.AddRelationshipsAsync(
        [
            new GraphRelationship("Ångström", "Kelvin", "corresponded with")
            {
                SourceChunkIds = ["doc1_0"],
            },
            new GraphRelationship("Ångström", "Spectroscopy", "founded"),
            new GraphRelationship("Kelvin", "Thermodynamics", "defined"),
        ], ct);

        await store.SetCommunitiesAsync(
        [
            new Community(1, 0, ["Ångström", "Kelvin"], "Nineteenth-century physicists"),
            new Community(2, 0, ["Unrelated"], "Something else"),
        ], ct);

        return store;
    }

    [Fact]
    public async Task EntitiesComeBackByNameInAnyCasing()
    {
        await using var store = await SeedAsync();

        var found = await store.GetEntitiesAsync(
            ["ÅNGSTRÖM", "kelvin", "NotThere"], TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);

        // Display spelling, not the folded key — the property #299 exists to hold.
        Assert.Contains(found, e => string.Equals(e.Name, "Ångström", StringComparison.Ordinal));
        Assert.Contains(found, e => string.Equals(e.Name, "Kelvin", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The merged row, with its source chunks, is what local search needs — the embedded entity
    /// chunk carries only the view one document's extraction produced.
    /// </remarks>
    [Fact]
    public async Task AFetchedEntityCarriesItsSourceChunks()
    {
        await using var store = await SeedAsync();

        var found = await store.GetEntitiesAsync(["Ångström"], TestContext.Current.CancellationToken);

        Assert.Equal(["doc1_0"], Assert.Single(found).SourceChunkIds, StringComparer.Ordinal);
    }

    /// <remarks>
    /// Degree counts relationships at <i>either</i> endpoint. Ångström has two, Kelvin two (one of
    /// them shared with Ångström), and an entity with none reports 0 rather than being omitted.
    /// </remarks>
    [Fact]
    public async Task DegreeCountsBothEndpointsAndReportsZeroRatherThanNothing()
    {
        await using var store = await SeedAsync();

        var degrees = await store.GetDegreesAsync(
            ["Ångström", "Kelvin", "Unrelated"], TestContext.Current.CancellationToken);

        Assert.Equal(2, degrees["Ångström"]);
        Assert.Equal(2, degrees["Kelvin"]);
        Assert.Equal(0, degrees["Unrelated"]);
    }

    /// <remarks>
    /// An edge between two of the requested entities is one relationship, not two. The per-name
    /// default finds it twice and de-duplicates; the batch query returns it once to begin with, and
    /// both must agree — a doubled edge would double its endpoint's apparent connectivity.
    /// </remarks>
    [Fact]
    public async Task AnEdgeBetweenTwoRequestedEntitiesIsReturnedOnce()
    {
        await using var store = await SeedAsync();

        var found = await store.GetRelationshipsAsync(
            ["Ångström", "Kelvin"], TestContext.Current.CancellationToken);

        Assert.Equal(3, found.Count);
        Assert.Single(found, r =>
            string.Equals(r.Description, "corresponded with", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelationshipProvenanceSurvivesARoundTrip()
    {
        await using var store = await SeedAsync();

        var found = await store.GetRelationshipsAsync(
            ["Ångström"], TestContext.Current.CancellationToken);

        var corresponded = found.Single(r =>
            string.Equals(r.Description, "corresponded with", StringComparison.Ordinal));
        Assert.Equal(["doc1_0"], corresponded.SourceChunkIds, StringComparer.Ordinal);

        // And an edge written without provenance reads back empty rather than null.
        var founded = found.Single(r => string.Equals(r.Description, "founded", StringComparison.Ordinal));
        Assert.Empty(founded.SourceChunkIds);
    }

    /// <remarks>
    /// A community holding several requested entities is returned once. Local search ranks reports
    /// by how many selected entities each community holds, and it counts that itself from the
    /// members — a duplicated community would be counted twice.
    /// </remarks>
    [Fact]
    public async Task ACommunityHoldingSeveralRequestedEntitiesIsReturnedOnce()
    {
        await using var store = await SeedAsync();

        var found = await store.GetCommunitiesForEntitiesAsync(
            ["Ångström", "Kelvin"], TestContext.Current.CancellationToken);

        var community = Assert.Single(found);
        Assert.Equal(1, community.Id);
        Assert.Equal(2, community.MemberEntities.Count);
    }

    [Theory]
    [InlineData(0)]
    public async Task AnEmptyRequestIsAnEmptyAnswerRatherThanEverything(int _)
    {
        await using var store = await SeedAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await store.GetEntitiesAsync([], ct));
        Assert.Empty(await store.GetRelationshipsAsync([], ct));
        Assert.Empty(await store.GetCommunitiesForEntitiesAsync([], ct));
        Assert.Empty(await store.GetDegreesAsync([], ct));
    }

    /// <remarks>
    /// <b>The reason this file exists.</b> Every batch read ships twice — a default on
    /// <see cref="IGraphStore"/> for implementations outside this repository, and a real query here.
    /// Nothing else compares them, and a divergence would show up as a context assembled from
    /// subtly different material with no baseline to notice against.
    /// </remarks>
    [Fact]
    public async Task TheSqliteOverridesAgreeWithTheInterfaceDefaultsTheyReplace()
    {
        await using var store = await SeedAsync();
        var ct = TestContext.Current.CancellationToken;
        var names = new[] { "Ångström", "Kelvin", "Unrelated", "NotThere" };

        IGraphStore viaDefaults = new DefaultShim(store);

        Assert.Equal(
            (await store.GetEntitiesAsync(names, ct)).Select(e => e.Name).Order(StringComparer.Ordinal),
            (await viaDefaults.GetEntitiesAsync(names, ct)).Select(e => e.Name).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

        Assert.Equal(
            await store.GetDegreesAsync(names, ct),
            await viaDefaults.GetDegreesAsync(names, ct));

        Assert.Equal(
            (await store.GetRelationshipsAsync(names, ct)).Select(r => r.Description).Order(StringComparer.Ordinal),
            (await viaDefaults.GetRelationshipsAsync(names, ct)).Select(r => r.Description).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

        Assert.Equal(
            (await store.GetCommunitiesForEntitiesAsync(names, ct)).Select(c => c.Id).Order(),
            (await viaDefaults.GetCommunitiesForEntitiesAsync(names, ct)).Select(c => c.Id).Order());
    }

    /// <summary>
    /// Forwards only the per-entity reads, so the interface's default batch implementations are the
    /// ones that run.
    /// </summary>
    /// <remarks>
    /// A store cannot be made to use a default it has overridden, so reaching the defaults needs a
    /// type that does not override them. This is that type and nothing more.
    /// </remarks>
    /// <param name="inner">The real store.</param>
    private sealed class DefaultShim(SqliteGraphStore inner) : IGraphStore
    {
        public Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default) =>
            inner.AddEntitiesAsync(entities, ct);

        public Task SetPageRankScoresAsync(
            IReadOnlyDictionary<string, double> scores, CancellationToken ct = default) =>
            inner.SetPageRankScoresAsync(scores, ct);

        public Task AddRelationshipsAsync(
            IReadOnlyList<GraphRelationship> relationships, CancellationToken ct = default) =>
            inner.AddRelationshipsAsync(relationships, ct);

        public Task<IReadOnlyList<GraphEntity>> GetNeighborsAsync(
            string entityName, int depth, CancellationToken ct = default) =>
            inner.GetNeighborsAsync(entityName, depth, ct);

        public Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(
            string entityName, CancellationToken ct = default) =>
            inner.GetRelationshipsAsync(entityName, ct);

        public Task SetCommunitiesAsync(IReadOnlyList<Community> communities, CancellationToken ct = default) =>
            inner.SetCommunitiesAsync(communities, ct);

        public Task<IReadOnlyList<Community>> GetCommunitiesForEntityAsync(
            string entityName, CancellationToken ct = default) =>
            inner.GetCommunitiesForEntityAsync(entityName, ct);

        public Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default) =>
            inner.GetFullGraphAsync(ct);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default) =>
            inner.DeleteByDocumentIdAsync(documentId, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
