# GraphRAG Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `Rag.NET.Graph` (standalone graph library with Leiden + PageRank) and `Rag.NET.GraphRag` (entity extraction, community detection, local + global search) implementing the full Microsoft GraphRAG specification.

**Architecture:** Two packages — `Rag.NET.Graph` has zero Rag.NET dependency (data models, IGraphStore + SQLite default, Leiden community detection, PageRank). `Rag.NET.GraphRag` references both and provides ingestion behaviors (entity extraction with gleaning, community detection + reports) and retrieval behaviors (local entity-hop search, global map-reduce over community reports). Hybrid storage: IGraphStore for structure, IVectorStore for embeddings.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, Microsoft.Extensions.AI abstractions, xunit.v3, NSubstitute

---

### Task 1: Rag.NET.Graph scaffolding + data models

**Files:**
- Create: `src/Rag.NET.Graph/Rag.NET.Graph.csproj`
- Create: `src/Rag.NET.Graph/GraphEntity.cs`
- Create: `src/Rag.NET.Graph/GraphRelationship.cs`
- Create: `src/Rag.NET.Graph/Community.cs`
- Create: `src/Rag.NET.Graph/GraphSnapshot.cs`
- Create: `tests/Rag.NET.Graph.Tests/Rag.NET.Graph.Tests.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Create the project file**

Create `src/Rag.NET.Graph/Rag.NET.Graph.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Graph</RootNamespace>
    <PackageId>Rag.NET.Graph</PackageId>
    <Description>Standalone graph library — Leiden community detection, PageRank, IGraphStore abstraction</Description>
  </PropertyGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.Graph.Tests</_Parameter1>
    </AssemblyAttribute>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.Benchmarks</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create data models**

Create `src/Rag.NET.Graph/GraphEntity.cs`:

```csharp
namespace Rag.NET.Graph;

/// <summary>A named entity extracted from text with a type and description.</summary>
public sealed record GraphEntity(string Name, string Type, string Description)
{
    /// <summary>PageRank score computed over the entity-relationship graph.</summary>
    public double PageRankScore { get; set; }

    /// <summary>Document ID this entity was extracted from.</summary>
    public string? SourceDocumentId { get; init; }

    /// <summary>Chunk indices that mention this entity.</summary>
    public IReadOnlyList<string> SourceChunkIds { get; init; } = [];
}
```

Create `src/Rag.NET.Graph/GraphRelationship.cs`:

```csharp
namespace Rag.NET.Graph;

/// <summary>A directed relationship between two entities.</summary>
public sealed record GraphRelationship(
    string SourceEntity,
    string TargetEntity,
    string Description,
    double Weight = 1.0)
{
    /// <summary>Document ID this relationship was extracted from.</summary>
    public string? SourceDocumentId { get; init; }
}
```

Create `src/Rag.NET.Graph/Community.cs`:

```csharp
namespace Rag.NET.Graph;

/// <summary>A community of related entities detected by Leiden.</summary>
public sealed record Community(
    int Id,
    int Level,
    IReadOnlyList<string> MemberEntities,
    string? ReportSummary);
```

Create `src/Rag.NET.Graph/GraphSnapshot.cs`:

```csharp
namespace Rag.NET.Graph;

/// <summary>Complete snapshot of the graph — entities, relationships, and communities.</summary>
public sealed record GraphSnapshot(
    IReadOnlyList<GraphEntity> Entities,
    IReadOnlyList<GraphRelationship> Relationships,
    IReadOnlyList<Community> Communities);
```

**Step 3: Create test project**

Create `tests/Rag.NET.Graph.Tests/Rag.NET.Graph.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Graph\Rag.NET.Graph.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**Step 4: Add to solution**

In `Rag.NET.slnx`, add to `/src/`:
```xml
<Project Path="src/Rag.NET.Graph/Rag.NET.Graph.csproj" />
```
Add to `/tests/`:
```xml
<Project Path="tests/Rag.NET.Graph.Tests/Rag.NET.Graph.Tests.csproj" />
```

**Step 5: Build and commit**

Run: `dotnet build src/Rag.NET.Graph/Rag.NET.Graph.csproj`
Expected: Build succeeded

```bash
git add src/Rag.NET.Graph/ tests/Rag.NET.Graph.Tests/ Rag.NET.slnx
git commit -m "feat(graphrag): add Rag.NET.Graph scaffolding and data models"
```

---

### Task 2: Rag.NET.GraphRag scaffolding

**Files:**
- Create: `src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj`
- Create: `tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Create the project file**

Create `src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.GraphRag</RootNamespace>
    <PackageId>Rag.NET.GraphRag</PackageId>
    <Description>GraphRAG — entity extraction, community detection, local + global search for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <ProjectReference Include="..\Rag.NET.Graph\Rag.NET.Graph.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test project file**

Create `tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.GraphRag\Rag.NET.GraphRag.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET.Graph\Rag.NET.Graph.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
  </ItemGroup>

</Project>
```

**Step 3: Add to solution and build**

Add both projects to `Rag.NET.slnx` in appropriate folders.

Run: `dotnet build src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj`

**Step 4: Commit**

```bash
git add src/Rag.NET.GraphRag/ tests/Rag.NET.GraphRag.Tests/ Rag.NET.slnx
git commit -m "feat(graphrag): add Rag.NET.GraphRag scaffolding"
```

---

### Task 3: IGraphStore + SqliteGraphStore

**Files:**
- Create: `src/Rag.NET.Graph/IGraphStore.cs`
- Create: `src/Rag.NET.Graph/SqliteGraphStore.cs`
- Test: `tests/Rag.NET.Graph.Tests/SqliteGraphStoreTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Graph.Tests/SqliteGraphStoreTests.cs`:

```csharp
using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.Graph.Tests;

public class SqliteGraphStoreTests : IAsyncDisposable
{
    private readonly SqliteGraphStore _store = new(":memory:");

    [Fact]
    public async Task AddEntitiesAsync_StoresAndRetrievesViaSnapshot()
    {
        var entities = new[] { new GraphEntity("Microsoft", "Organization", "Tech company") };
        await _store.AddEntitiesAsync(entities);
        var snapshot = await _store.GetFullGraphAsync();
        Assert.Single(snapshot.Entities);
        Assert.Equal("Microsoft", snapshot.Entities[0].Name);
    }

    [Fact]
    public async Task AddRelationshipsAsync_StoresAndRetrievesViaSnapshot()
    {
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A desc"), new GraphEntity("B", "Org", "B desc")]);
        await _store.AddRelationshipsAsync([new GraphRelationship("A", "B", "works with")]);
        var snapshot = await _store.GetFullGraphAsync();
        Assert.Single(snapshot.Relationships);
        Assert.Equal("A", snapshot.Relationships[0].SourceEntity);
    }

    [Fact]
    public async Task GetNeighborsAsync_ReturnsDirectNeighbors()
    {
        await _store.AddEntitiesAsync([
            new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B"), new GraphEntity("C", "Org", "C")]);
        await _store.AddRelationshipsAsync([
            new GraphRelationship("A", "B", "r1"), new GraphRelationship("B", "C", "r2")]);

        var neighbors = await _store.GetNeighborsAsync("A", depth: 1);
        Assert.Single(neighbors);
        Assert.Equal("B", neighbors[0].Name);
    }

    [Fact]
    public async Task GetNeighborsAsync_Depth2_ReturnsTwoHops()
    {
        await _store.AddEntitiesAsync([
            new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B"), new GraphEntity("C", "Org", "C")]);
        await _store.AddRelationshipsAsync([
            new GraphRelationship("A", "B", "r1"), new GraphRelationship("B", "C", "r2")]);

        var neighbors = await _store.GetNeighborsAsync("A", depth: 2);
        Assert.Equal(2, neighbors.Count);
    }

    [Fact]
    public async Task GetRelationshipsAsync_ReturnsEdgesForEntity()
    {
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B")]);
        await _store.AddRelationshipsAsync([new GraphRelationship("A", "B", "works with", 0.9)]);

        var rels = await _store.GetRelationshipsAsync("A");
        Assert.Single(rels);
        Assert.Equal("works with", rels[0].Description);
    }

    [Fact]
    public async Task SetCommunitiesAsync_StoresAndRetrieves()
    {
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A"), new GraphEntity("B", "Org", "B")]);
        var communities = new[] { new Community(0, 0, ["A", "B"], "A and B are related") };
        await _store.SetCommunitiesAsync(communities);

        var result = await _store.GetCommunitiesForEntityAsync("A");
        Assert.Single(result);
        Assert.Equal("A and B are related", result[0].ReportSummary);
    }

    [Fact]
    public async Task DeleteByDocumentIdAsync_RemovesEntitiesAndRelationships()
    {
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "A") { SourceDocumentId = "doc1" }]);
        await _store.AddRelationshipsAsync([new GraphRelationship("A", "B", "r1") { SourceDocumentId = "doc1" }]);

        await _store.DeleteByDocumentIdAsync("doc1");

        var snapshot = await _store.GetFullGraphAsync();
        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Relationships);
    }

    [Fact]
    public async Task AddEntitiesAsync_DuplicateName_MergesDescriptions()
    {
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "First description")]);
        await _store.AddEntitiesAsync([new GraphEntity("A", "Org", "Second description")]);

        var snapshot = await _store.GetFullGraphAsync();
        Assert.Single(snapshot.Entities);
        Assert.Contains("First description", snapshot.Entities[0].Description);
        Assert.Contains("Second description", snapshot.Entities[0].Description);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Implement IGraphStore interface**

Create `src/Rag.NET.Graph/IGraphStore.cs`:

```csharp
namespace Rag.NET.Graph;

/// <summary>Abstraction for storing and querying entity-relationship graphs.</summary>
public interface IGraphStore : IAsyncDisposable
{
    Task AddEntitiesAsync(IReadOnlyList<GraphEntity> entities, CancellationToken ct = default);
    Task AddRelationshipsAsync(IReadOnlyList<GraphRelationship> relationships, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEntity>> GetNeighborsAsync(string entityName, int depth, CancellationToken ct = default);
    Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(string entityName, CancellationToken ct = default);
    Task SetCommunitiesAsync(IReadOnlyList<Community> communities, CancellationToken ct = default);
    Task<IReadOnlyList<Community>> GetCommunitiesForEntityAsync(string entityName, CancellationToken ct = default);
    Task<GraphSnapshot> GetFullGraphAsync(CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);
}
```

**Step 3: Implement SqliteGraphStore**

Create `src/Rag.NET.Graph/SqliteGraphStore.cs`:

SQLite-backed implementation with tables:
- `entities` (name TEXT PK, type TEXT, description TEXT, page_rank REAL, source_document_id TEXT, source_chunk_ids TEXT)
- `relationships` (source_entity TEXT, target_entity TEXT, description TEXT, weight REAL, source_document_id TEXT)
- `communities` (id INTEGER, level INTEGER, report_summary TEXT)
- `community_members` (community_id INTEGER, entity_name TEXT)

Key behaviors:
- `AddEntitiesAsync`: UPSERT — on conflict, append description with newline separator
- `GetNeighborsAsync`: BFS traversal up to `depth` hops using both directions of relationships
- Entity names are case-insensitive (stored normalized to uppercase for comparison, original case preserved)

**Step 4: Run tests, verify pass**

Run: `dotnet test tests/Rag.NET.Graph.Tests/ -v q`
Expected: All 8 tests pass

**Step 5: Commit**

```bash
git add src/Rag.NET.Graph/IGraphStore.cs src/Rag.NET.Graph/SqliteGraphStore.cs tests/Rag.NET.Graph.Tests/
git commit -m "feat(graphrag): add IGraphStore abstraction and SqliteGraphStore"
```

---

### Task 4: Leiden community detection algorithm

**Files:**
- Create: `src/Rag.NET.Graph/Algorithms/Leiden.cs`
- Create: `src/Rag.NET.Graph/Algorithms/LeidenOptions.cs`
- Test: `tests/Rag.NET.Graph.Tests/Algorithms/LeidenTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Graph.Tests/Algorithms/LeidenTests.cs`:

```csharp
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

public class LeidenTests
{
    [Fact]
    public void Detect_TwoDisconnectedCliques_FindsTwoCommunities()
    {
        var entities = Enumerable.Range(0, 8)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();

        var relationships = new List<GraphRelationship>();
        // Clique 1: E0-E3 fully connected
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        // Clique 2: E4-E7 fully connected
        for (int i = 4; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);

        Assert.Equal(2, communities.Count);
        var c0Members = communities[0].MemberEntities.ToHashSet();
        var c1Members = communities[1].MemberEntities.ToHashSet();
        // Each clique should be in its own community
        Assert.True(
            (c0Members.SetEquals(["E0", "E1", "E2", "E3"]) && c1Members.SetEquals(["E4", "E5", "E6", "E7"])) ||
            (c1Members.SetEquals(["E0", "E1", "E2", "E3"]) && c0Members.SetEquals(["E4", "E5", "E6", "E7"])));
    }

    [Fact]
    public void Detect_SingleNode_ReturnsSingleCommunity()
    {
        var graph = new GraphSnapshot([new GraphEntity("A", "Node", "A")], [], []);
        var communities = Leiden.Detect(graph);
        Assert.Single(communities);
        Assert.Single(communities[0].MemberEntities);
    }

    [Fact]
    public void Detect_EmptyGraph_ReturnsEmpty()
    {
        var graph = new GraphSnapshot([], [], []);
        var communities = Leiden.Detect(graph);
        Assert.Empty(communities);
    }

    [Fact]
    public void Detect_FullyConnected_ReturnsSingleCommunity()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 5; i++)
            for (int j = i + 1; j < 5; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph);
        Assert.Single(communities);
        Assert.Equal(5, communities[0].MemberEntities.Count);
    }

    [Fact]
    public void Detect_ReturnsHierarchicalLevels()
    {
        // Three cliques with one bridge edge each
        var entities = Enumerable.Range(0, 12)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        // Clique 0-3, 4-7, 8-11
        for (int c = 0; c < 3; c++)
            for (int i = c * 4; i < c * 4 + 4; i++)
                for (int j = i + 1; j < c * 4 + 4; j++)
                    relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected"));
        // Bridge edges
        relationships.Add(new GraphRelationship("E3", "E4", "bridge"));
        relationships.Add(new GraphRelationship("E7", "E8", "bridge"));

        var graph = new GraphSnapshot(entities, relationships, []);
        var communities = Leiden.Detect(graph, new LeidenOptions { MaxLevels = 2 });

        // Should find communities at level 0
        Assert.True(communities.Count >= 3);
        Assert.All(communities, c => Assert.Equal(0, c.Level));
    }

    [Fact]
    public void Detect_ResolutionParameter_AffectsGranularity()
    {
        // Two cliques with a weak bridge
        var entities = Enumerable.Range(0, 8)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var relationships = new List<GraphRelationship>();
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected", 1.0));
        for (int i = 4; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "connected", 1.0));
        relationships.Add(new GraphRelationship("E3", "E4", "weak bridge", 0.1));

        var graph = new GraphSnapshot(entities, relationships, []);

        var lowRes = Leiden.Detect(graph, new LeidenOptions { Resolution = 0.5 });
        var highRes = Leiden.Detect(graph, new LeidenOptions { Resolution = 2.0 });

        // Higher resolution should produce >= as many communities as lower
        Assert.True(highRes.Count >= lowRes.Count);
    }

    [Fact]
    public void Detect_IsDeterministicWithSameSeed()
    {
        var entities = Enumerable.Range(0, 20)
            .Select(i => new GraphEntity($"E{i}", "Node", $"Entity {i}"))
            .ToList();
        var rng = new Random(42);
        var relationships = Enumerable.Range(0, 40)
            .Select(_ => new GraphRelationship($"E{rng.Next(20)}", $"E{rng.Next(20)}", "r"))
            .ToList();

        var graph = new GraphSnapshot(entities, relationships, []);
        var run1 = Leiden.Detect(graph, new LeidenOptions { RandomSeed = 123 });
        var run2 = Leiden.Detect(graph, new LeidenOptions { RandomSeed = 123 });

        Assert.Equal(run1.Count, run2.Count);
        for (int i = 0; i < run1.Count; i++)
            Assert.Equal(run1[i].MemberEntities.OrderBy(x => x), run2[i].MemberEntities.OrderBy(x => x));
    }
}
```

**Step 2: Implement Leiden**

Create `src/Rag.NET.Graph/Algorithms/LeidenOptions.cs`:

```csharp
namespace Rag.NET.Graph.Algorithms;

/// <summary>Options for the Leiden community detection algorithm.</summary>
public sealed class LeidenOptions
{
    /// <summary>Resolution parameter — higher values produce more, smaller communities. Default: 1.0.</summary>
    public double Resolution { get; set; } = 1.0;

    /// <summary>Maximum iterations per level. Default: 10.</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>Maximum hierarchy levels. Null = until no improvement. Default: null.</summary>
    public int? MaxLevels { get; set; }

    /// <summary>Random seed for deterministic results. Default: 42.</summary>
    public int RandomSeed { get; set; } = 42;
}
```

Create `src/Rag.NET.Graph/Algorithms/Leiden.cs`:

Full hierarchical Leiden implementation:
1. **Local moving phase**: For each node, compute modularity gain of moving to each neighbor's community. Move to best-gain community. Repeat until no improvement.
2. **Refinement phase**: Within each community from step 1, try to split into sub-communities to avoid poorly connected communities (this is what distinguishes Leiden from Louvain).
3. **Aggregation phase**: Create a new graph where each community becomes a super-node. Edge weights between super-nodes = sum of inter-community edges.
4. Recurse on the aggregated graph until no further improvement or MaxLevels reached.

Modularity gain formula (CPM variant): `ΔQ = (edges_in_new - edges_in_old) - resolution * (degree_new² - degree_old²) / (2 * total_edges)`

The class should be public static:
```csharp
namespace Rag.NET.Graph.Algorithms;

/// <summary>Leiden community detection algorithm with hierarchical refinement.</summary>
public static class Leiden
{
    /// <summary>Detect communities in the graph using the Leiden algorithm.</summary>
    public static IReadOnlyList<Community> Detect(GraphSnapshot graph, LeidenOptions? options = null)
    { /* Full implementation */ }
}
```

**Step 3: Run tests, verify pass**

Run: `dotnet test tests/Rag.NET.Graph.Tests/ --filter "FullyQualifiedName~LeidenTests" -v q`
Expected: All 7 tests pass

**Step 4: Commit**

```bash
git add src/Rag.NET.Graph/Algorithms/ tests/Rag.NET.Graph.Tests/Algorithms/
git commit -m "feat(graphrag): add Leiden community detection algorithm"
```

---

### Task 5: PageRank algorithm

**Files:**
- Create: `src/Rag.NET.Graph/Algorithms/PageRank.cs`
- Test: `tests/Rag.NET.Graph.Tests/Algorithms/PageRankTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Graph.Tests/Algorithms/PageRankTests.cs`:

```csharp
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

public class PageRankTests
{
    [Fact]
    public void Compute_StarGraph_CenterHasHighestRank()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        // E0 is center, all others point to it
        var relationships = Enumerable.Range(1, 4)
            .Select(i => new GraphRelationship($"E{i}", "E0", "points to"))
            .ToList();

        var graph = new GraphSnapshot(entities, relationships, []);
        var ranks = PageRank.Compute(graph);

        Assert.Equal(5, ranks.Count);
        Assert.True(ranks["E0"] > ranks["E1"]);
        Assert.True(ranks["E0"] > ranks["E2"]);
    }

    [Fact]
    public void Compute_EmptyGraph_ReturnsEmpty()
    {
        var graph = new GraphSnapshot([], [], []);
        var ranks = PageRank.Compute(graph);
        Assert.Empty(ranks);
    }

    [Fact]
    public void Compute_SingleNode_ReturnsOne()
    {
        var graph = new GraphSnapshot([new GraphEntity("A", "Node", "A")], [], []);
        var ranks = PageRank.Compute(graph);
        Assert.Equal(1.0, ranks["A"], precision: 5);
    }

    [Fact]
    public void Compute_ScoresSumToOne()
    {
        var entities = Enumerable.Range(0, 10)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        var rng = new Random(42);
        var relationships = Enumerable.Range(0, 20)
            .Select(_ => new GraphRelationship($"E{rng.Next(10)}", $"E{rng.Next(10)}", "r"))
            .ToList();

        var graph = new GraphSnapshot(entities, relationships, []);
        var ranks = PageRank.Compute(graph);

        Assert.InRange(ranks.Values.Sum(), 0.99, 1.01);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new GraphEntity($"E{i}", "Node", $"E{i}"))
            .ToList();
        var relationships = new List<GraphRelationship>
        {
            new("E0", "E1", "r"), new("E1", "E2", "r"),
            new("E2", "E3", "r"), new("E3", "E4", "r"),
            new("E4", "E0", "r"),
        };
        var graph = new GraphSnapshot(entities, relationships, []);

        var run1 = PageRank.Compute(graph);
        var run2 = PageRank.Compute(graph);

        foreach (var key in run1.Keys)
            Assert.Equal(run1[key], run2[key], precision: 10);
    }
}
```

**Step 2: Implement PageRank**

Create `src/Rag.NET.Graph/Algorithms/PageRank.cs`:

```csharp
namespace Rag.NET.Graph.Algorithms;

/// <summary>Standard iterative PageRank on an entity-relationship graph.</summary>
public static class PageRank
{
    /// <summary>Compute PageRank scores for all entities. Scores sum to 1.0.</summary>
    public static IReadOnlyDictionary<string, double> Compute(
        GraphSnapshot graph,
        double dampingFactor = 0.85,
        int maxIterations = 100,
        double tolerance = 1e-6)
    { /* Standard iterative PageRank implementation */ }
}
```

Standard algorithm: `PR(i) = (1-d)/N + d * Σ(PR(j)/L(j))` for all j linking to i. Iterate until convergence.

**Step 3: Run tests, commit**

Run: `dotnet test tests/Rag.NET.Graph.Tests/ --filter "FullyQualifiedName~PageRankTests" -v q`

```bash
git add src/Rag.NET.Graph/Algorithms/PageRank.cs tests/Rag.NET.Graph.Tests/Algorithms/
git commit -m "feat(graphrag): add PageRank algorithm"
```

---

### Task 6: GraphRag configuration models

**Files:**
- Create: `src/Rag.NET.GraphRag/GraphRagOptions.cs`
- Create: `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs`
- Create: `src/Rag.NET.GraphRag/GraphRagRetrievalMode.cs`

**Step 1: Create all three files**

Create `src/Rag.NET.GraphRag/GraphRagRetrievalMode.cs`:

```csharp
namespace Rag.NET.GraphRag;

/// <summary>Controls which GraphRAG search strategy is used at retrieval time.</summary>
public enum GraphRagRetrievalMode
{
    /// <summary>Entity-hop traversal — start from matched entities, traverse neighbors, collect context.</summary>
    Local,

    /// <summary>Map-reduce over community reports — broad thematic questions across the full corpus.</summary>
    Global,

    /// <summary>LLM classifies the query and routes to Local or Global automatically.</summary>
    Auto,
}
```

Create `src/Rag.NET.GraphRag/GraphRagOptions.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for GraphRAG ingestion behaviors.</summary>
public sealed class GraphRagOptions
{
    /// <summary>Toggle GraphRAG on/off. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of follow-up "did I miss anything?" LLM passes per chunk. Default: 1.</summary>
    public int GleaningPasses { get; set; } = 1;

    /// <summary>Constrain entity extraction to these types. Null = open extraction. Default: null.</summary>
    public string[]? EntityTypes { get; set; }

    /// <summary>Constrain relationship extraction to these types. Null = open. Default: null.</summary>
    public string[]? RelationshipTypes { get; set; }

    /// <summary>Trigger LLM summarization when accumulated entity description exceeds this length. Default: 500.</summary>
    public int MaxEntityDescriptionLength { get; set; } = 500;

    /// <summary>LLM prompt template for entity/relationship extraction. {text} is replaced with chunk text.</summary>
    public string EntityExtractionPrompt { get; set; } = """
        Extract all entities and relationships from the following text.
        Return a JSON object with two arrays:
        - "entities": [{"name": "...", "type": "...", "description": "..."}]
        - "relationships": [{"source": "...", "target": "...", "description": "...", "weight": 1.0}]

        Entity types should be general categories like: Person, Organization, Location, Event, Concept, Technology, Document.
        Relationship descriptions should be concise verb phrases.
        Extract ALL entities and relationships, even minor ones.

        Text:
        {text}
        """;

    /// <summary>Follow-up prompt for gleaning passes. {text} and {previous} are replaced.</summary>
    public string GleaningPrompt { get; set; } = """
        You previously extracted entities and relationships from this text.
        Your previous extraction: {previous}

        Are there any entities or relationships you missed? Look carefully for:
        - Implicit relationships
        - Minor entities mentioned in passing
        - Temporal or causal relationships

        Return ONLY the additional entities and relationships in the same JSON format.
        Return {"entities": [], "relationships": []} if nothing was missed.

        Text:
        {text}
        """;

    /// <summary>Prompt template for community report generation. {entities} and {relationships} are replaced.</summary>
    public string CommunityReportPrompt { get; set; } = """
        You are analyzing a community of related entities in a knowledge graph.
        Write a comprehensive summary report of this community that covers:
        - The main entities and their roles
        - Key relationships and how entities interact
        - Overall themes and significance

        Entities:
        {entities}

        Relationships:
        {relationships}

        Write a clear, informative report in 2-4 paragraphs.
        """;

    /// <summary>Optional cheaper model for entity extraction. Null = use DI-registered IChatClient.</summary>
    public IChatClient? ExtractionChatClient { get; set; }

    /// <summary>Optional model for community report generation. Null = use DI-registered IChatClient.</summary>
    public IChatClient? SummarizationChatClient { get; set; }
}
```

Create `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for GraphRAG retrieval behaviors.</summary>
public sealed class GraphRagRetrievalOptions
{
    /// <summary>Search mode. Default: Local.</summary>
    public GraphRagRetrievalMode Mode { get; set; } = GraphRagRetrievalMode.Local;

    /// <summary>Hop depth for local entity traversal. Default: 1.</summary>
    public int LocalSearchDepth { get; set; } = 1;

    /// <summary>Top-K entities to start local traversal from. Default: 10.</summary>
    public int LocalTopEntities { get; set; } = 10;

    /// <summary>Blend weight for PageRank vs. vector similarity in scoring. Default: 0.3.</summary>
    public double PageRankWeight { get; set; } = 0.3;

    /// <summary>Reports per batch in global map phase. Null = auto. Default: null.</summary>
    public int? GlobalBatchSize { get; set; }

    /// <summary>Optional model for global map-reduce. Null = use DI-registered IChatClient.</summary>
    public IChatClient? GlobalChatClient { get; set; }
}
```

**Step 2: Build and commit**

Run: `dotnet build src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj`

```bash
git add src/Rag.NET.GraphRag/GraphRagOptions.cs src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs src/Rag.NET.GraphRag/GraphRagRetrievalMode.cs
git commit -m "feat(graphrag): add configuration models"
```

---

### Task 7: GraphEntityExtractionBehavior

**Files:**
- Create: `src/Rag.NET.GraphRag/GraphEntityExtractionBehavior.cs`
- Create: `src/Rag.NET.GraphRag/ExtractionResult.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/GraphEntityExtractionBehaviorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.GraphRag.Tests/GraphEntityExtractionBehaviorTests.cs` with tests:

1. `HandleAsync_WhenDisabled_SkipsExtraction` — Enabled=false, next is called, no LLM calls
2. `HandleAsync_ExtractsEntitiesAndRelationships` — Mock LLM returns JSON with entities/relationships, verify they're stored in IGraphStore and appended to ctx.EmbeddedChunks with `graph_type=entity` metadata
3. `HandleAsync_PerformsGleaningPass` — GleaningPasses=1, verify LLM is called twice per chunk (extraction + gleaning)
4. `HandleAsync_GleaningPassZero_NoFollowUp` — GleaningPasses=0, only one LLM call per chunk
5. `HandleAsync_MergesEntitiesAcrossChunks` — Same entity from two chunks, descriptions are merged in IGraphStore
6. `HandleAsync_UsesCustomExtractionClient` — ExtractionChatClient is set, verify it's used instead of default
7. `HandleAsync_EmbedsEntityDescriptions` — Verify IEmbeddingGenerator is called for entity descriptions and results appended to ctx.EmbeddedChunks

**Step 2: Create ExtractionResult model**

Create `src/Rag.NET.GraphRag/ExtractionResult.cs`:

```csharp
namespace Rag.NET.GraphRag;

/// <summary>Deserialized result of entity/relationship extraction from LLM.</summary>
internal sealed record ExtractionResult
{
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = [];
    public IReadOnlyList<ExtractedRelationship> Relationships { get; init; } = [];
}

internal sealed record ExtractedEntity(string Name, string Type, string Description);
internal sealed record ExtractedRelationship(string Source, string Target, string Description, double Weight = 1.0);
```

**Step 3: Implement GraphEntityExtractionBehavior**

Create `src/Rag.NET.GraphRag/GraphEntityExtractionBehavior.cs`:

Implements `IIngestionBehavior`. Constructor takes `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`, `IGraphStore`, `GraphRagOptions`.

Flow:
1. For each chunk in `ctx.Chunks`:
   a. Call LLM with extraction prompt (chunk text injected)
   b. Parse JSON response into `ExtractionResult`
   c. For each gleaning pass: call LLM with gleaning prompt, merge results
   d. Convert to `GraphEntity` / `GraphRelationship` with source document/chunk metadata
   e. Store in `IGraphStore` via `AddEntitiesAsync` / `AddRelationshipsAsync`
2. After all chunks: get all entities from graph, embed descriptions, create `EmbeddedChunk` objects with `graph_type=entity` metadata
3. Same for relationships: embed descriptions, create `EmbeddedChunk` with `graph_type=relationship`
4. Append all to `ctx.EmbeddedChunks`
5. Call `next(ctx, ct)`

**Step 4: Run tests, commit**

```bash
git commit -m "feat(graphrag): add GraphEntityExtractionBehavior with gleaning"
```

---

### Task 8: CommunityDetectionBehavior

**Files:**
- Create: `src/Rag.NET.GraphRag/CommunityDetectionBehavior.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/CommunityDetectionBehaviorTests.cs`

**Step 1: Write the failing tests**

Tests:
1. `HandleAsync_RunsLeidenAndStoresCommunities` — Verify Leiden is called, communities stored in IGraphStore
2. `HandleAsync_ComputesPageRankScores` — Verify PageRank runs and scores are set on entities
3. `HandleAsync_GeneratesCommunityReports` — Mock LLM, verify it's called per community with entity/relationship text
4. `HandleAsync_EmbedsCommunityReports` — Verify reports are embedded and appended to ctx.EmbeddedChunks with `graph_type=community_report` metadata
5. `HandleAsync_UsesCustomSummarizationClient` — SummarizationChatClient is set, verify it's used
6. `HandleAsync_EmptyGraph_SkipsCommunityDetection` — No entities in graph, behavior passes through

**Step 2: Implement CommunityDetectionBehavior**

Implements `IIngestionBehavior`. Constructor takes `IChatClient`, `IEmbeddingGenerator`, `IGraphStore`, `GraphRagOptions`.

Flow:
1. Load full graph from `IGraphStore.GetFullGraphAsync()`
2. If empty, call `next()` immediately
3. Run `Leiden.Detect(graph)` → communities
4. Run `PageRank.Compute(graph)` → scores, update entities in store
5. For each community: build prompt with member entities + relationships, call LLM for report
6. Store communities (with reports) in `IGraphStore.SetCommunitiesAsync()`
7. Embed community reports, create `EmbeddedChunk` with metadata `graph_type=community_report`, `community_id`, `community_level`
8. Append to `ctx.EmbeddedChunks`
9. Call `next(ctx, ct)`

**Step 3: Run tests, commit**

```bash
git commit -m "feat(graphrag): add CommunityDetectionBehavior"
```

---

### Task 9: GraphLocalSearchBehavior

**Files:**
- Create: `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/GraphLocalSearchBehaviorTests.cs`

**Step 1: Write the failing tests**

Tests:
1. `HandleAsync_FindsEntitiesAndTraversesNeighbors` — Query matches entity, neighbors are returned
2. `HandleAsync_BlendsPageRankWithSimilarity` — Verify final scores combine vector similarity and PageRank with configured weight
3. `HandleAsync_RespectsLocalSearchDepth` — Depth=2, verify 2-hop neighbors included
4. `HandleAsync_IncludesRelationshipsAndCommunityReports` — Full context: entities + relationships + community reports
5. `HandleAsync_NoMatchingEntities_DelegatesToNext` — No entity matches, falls through to standard retrieval

**Step 2: Implement GraphLocalSearchBehavior**

Implements `IRetrievalBehavior`. Constructor takes `IGraphStore`, `GraphRagRetrievalOptions`.

Flow:
1. Call `next(ctx, ct)` to get standard retrieval results
2. Filter results for entity matches (`graph_type=entity`), take top `LocalTopEntities`
3. For each matched entity: `IGraphStore.GetNeighborsAsync(name, LocalSearchDepth)` + `GetRelationshipsAsync` + `GetCommunitiesForEntityAsync`
4. Build expanded result set: original results + neighbor entity chunks + relationship chunks + community report chunks
5. Score: `finalScore = (1 - PageRankWeight) * vectorSimilarity + PageRankWeight * pageRankScore`
6. Deduplicate, sort descending, return

**Step 3: Run tests, commit**

```bash
git commit -m "feat(graphrag): add GraphLocalSearchBehavior"
```

---

### Task 10: GraphGlobalSearchBehavior

**Files:**
- Create: `src/Rag.NET.GraphRag/GraphGlobalSearchBehavior.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/GraphGlobalSearchBehaviorTests.cs`

**Step 1: Write the failing tests**

Tests:
1. `HandleAsync_CollectsCommunityReportsAndMapReduces` — Mock LLM, verify map calls per batch + final reduce call
2. `HandleAsync_ShufflesReportsBeforeBatching` — Deterministic seed, verify order differs from insertion order
3. `HandleAsync_RespectsGlobalBatchSize` — BatchSize=2, verify correct number of map calls
4. `HandleAsync_UsesGlobalChatClient` — Custom client, verify used for map-reduce
5. `HandleAsync_NoCommunityReports_DelegatesToNext` — No reports, falls through

**Step 2: Implement GraphGlobalSearchBehavior**

Implements `IRetrievalBehavior`. Constructor takes `IChatClient`, `GraphRagRetrievalOptions`.

Flow:
1. Call `next(ctx, ct)` to get all results
2. Filter for community reports (`graph_type=community_report`)
3. If none, return standard results
4. Shuffle reports, batch into groups of `GlobalBatchSize`
5. Map: for each batch, LLM call — "Given these community reports, answer: {query}" → partial answers
6. Reduce: final LLM call combining all partial answers
7. Return: synthesized answer as a `SearchResult` prepended to other results

**Step 3: Run tests, commit**

```bash
git commit -m "feat(graphrag): add GraphGlobalSearchBehavior"
```

---

### Task 11: UseGraphRag DI registration

**Files:**
- Create: `src/Rag.NET.GraphRag/RagBuilderExtensions.cs`
- Create: `src/Rag.NET.GraphRag/GraphStoreBuilder.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/RagBuilderExtensionsTests.cs`

**Step 1: Write the failing tests**

Tests:
1. `UseGraphRag_RegistersAllOptions` — Verify GraphRagOptions, GraphRagRetrievalOptions registered
2. `UseGraphRag_RegistersAllBehaviors` — Verify all 4 behaviors registered
3. `UseGraphRag_ConfigureDelegateApplied` — options.GleaningPasses = 5, verify stored
4. `UseGraphRag_UseSqlite_RegistersGraphStore` — Verify IGraphStore resolves to SqliteGraphStore
5. `UseGraphRag_ReturnsBuilderForChaining` — Fluent API

**Step 2: Implement**

Create `src/Rag.NET.GraphRag/GraphStoreBuilder.cs`:

```csharp
namespace Rag.NET.GraphRag;

public sealed class GraphStoreBuilder
{
    internal Type? StoreType { get; private set; }
    internal object[]? StoreArgs { get; private set; }

    public GraphStoreBuilder UseSqlite(string dbPath)
    {
        StoreType = typeof(SqliteGraphStore);
        StoreArgs = [dbPath];
        return this;
    }
}
```

Create `src/Rag.NET.GraphRag/RagBuilderExtensions.cs`:

```csharp
public static RagBuilder UseGraphRag(
    this RagBuilder builder,
    Action<GraphRagOptions>? configure = null,
    Action<GraphRagRetrievalOptions>? retrieval = null,
    Action<GraphStoreBuilder>? graph = null)
```

Registers all options, behaviors, and graph store.

**Step 3: Run tests, commit**

```bash
git commit -m "feat(graphrag): add UseGraphRag DI registration"
```

---

### Task 12: Comprehensive tests

**Files:**
- Additional tests in existing test files + edge cases

Add tests for:
- Edge cases: empty chunks, single entity, circular relationships
- SqliteGraphStore: concurrent access, large graphs (100+ entities)
- Entity name case-insensitivity
- Extraction JSON parsing errors (malformed LLM response → graceful skip)
- Community report with empty communities
- Global search with very large number of reports
- Auto mode classification

Run: `dotnet test tests/Rag.NET.Graph.Tests/ tests/Rag.NET.GraphRag.Tests/ -v q`

```bash
git commit -m "test(graphrag): add comprehensive edge case tests"
```

---

### Task 13: Documentation

**Files:**
- Create: `docs/guide/graphrag.md`
- Modify: `docs/reference/features.md`

Create comprehensive guide covering:
- What is GraphRAG and when to use it
- Architecture: two packages, hybrid storage
- Quick start with code example
- Entity extraction configuration
- Community detection and reports
- Local vs. Global search modes
- Auto mode
- Cost/performance trade-offs
- Troubleshooting

Update features.md: mark GraphRAG as `[x]` Done.

```bash
git commit -m "docs: add GraphRAG guide and mark feature as done"
```

---

### Task 14: Benchmarks

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/GraphRagBenchmarks.cs`
- Modify: `benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj`

Benchmarks:
- Leiden algorithm: varying graph sizes (50, 200, 1000 nodes)
- PageRank: varying graph sizes
- Entity extraction behavior overhead (with mocked LLM)
- Local search behavior overhead
- Global search behavior overhead

```bash
git commit -m "bench: add GraphRAG benchmarks"
```

---

### Task 15: Build + test full solution

**Step 1:** `dotnet build Rag.NET.slnx`
**Step 2:** `dotnet test tests/Rag.NET.Graph.Tests/ tests/Rag.NET.GraphRag.Tests/ -v q`
**Step 3:** `dotnet test Rag.NET.slnx -v q` (full regression)
**Step 4:** Fix any issues, commit if needed.

---

## Parallel Execution Guide

| Task | Can run in parallel with | Depends on |
|------|--------------------------|------------|
| 1 (Graph scaffolding) | 2 | — |
| 2 (GraphRag scaffolding) | 1 | — |
| 3 (IGraphStore + SQLite) | 4, 5, 6 | 1 |
| 4 (Leiden) | 3, 5, 6 | 1 |
| 5 (PageRank) | 3, 4, 6 | 1 |
| 6 (Options models) | 3, 4, 5 | 2 |
| 7 (Entity extraction) | — | 3, 6 |
| 8 (Community detection) | — | 4, 5, 7 |
| 9 (Local search) | 10 | 3, 6 |
| 10 (Global search) | 9 | 6 |
| 11 (DI registration) | — | 7, 8, 9, 10 |
| 12 (Tests) | 13 | 11 |
| 13 (Documentation) | 12 | 2 |
| 14 (Benchmarks) | — | 12 |
| 15 (Build + test) | — | all |
