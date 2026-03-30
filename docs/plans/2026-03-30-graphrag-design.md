# GraphRAG — Entity Extraction + Community Summarization

**Date:** 2026-03-30
**Packages:** `Rag.NET.Graph`, `Rag.NET.GraphRag`
**Branch:** `feature/graphrag`

## Problem

Pure vector search treats documents as isolated chunks. Questions that require connecting information across chunks or documents — "How is entity X related to entity Y?" or "What are the main themes across this corpus?" — fall flat because vector similarity operates on individual text fragments with no structural awareness.

GraphRAG solves this by extracting entities and relationships from text, building a knowledge graph, detecting communities of related entities, and generating summary reports at each community level. Retrieval then operates on both the graph structure (multi-hop traversal) and the vector space (semantic similarity), enabling both specific factual queries and broad thematic questions.

## Approach

Full Microsoft GraphRAG specification. Two packages:

- **`Rag.NET.Graph`** — Standalone graph package with zero Rag.NET dependency. Leiden community detection, PageRank scoring, `IGraphStore` abstraction with SQLite default. Designed for independent reuse (no .NET Leiden implementation exists).
- **`Rag.NET.GraphRag`** — References `Rag.NET` + `Rag.NET.Graph`. LLM-driven entity/relationship extraction with iterative gleaning, community report generation, local + global search retrieval behaviors.

**Storage model:** Hybrid — `IGraphStore` for graph structure (entities, edges, communities), `IVectorStore` for embedded entity/relationship/community-report chunks. Graph for traversal, vectors for similarity.

## Package 1: Rag.NET.Graph

Standalone graph library. No dependency on Rag.NET core.

### Data Models

```csharp
public sealed record GraphEntity(string Name, string Type, string Description)
{
    public double PageRankScore { get; set; }
    public string? SourceDocumentId { get; init; }
    public IReadOnlyList<string> SourceChunkIds { get; init; } = [];
}

public sealed record GraphRelationship(
    string SourceEntity, string TargetEntity,
    string Description, double Weight = 1.0)
{
    public string? SourceDocumentId { get; init; }
}

public sealed record Community(
    int Id, int Level,
    IReadOnlyList<string> MemberEntities,
    string? ReportSummary);

public sealed record GraphSnapshot(
    IReadOnlyList<GraphEntity> Entities,
    IReadOnlyList<GraphRelationship> Relationships,
    IReadOnlyList<Community> Communities);
```

### IGraphStore

```csharp
public interface IGraphStore
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

Default implementation: `SqliteGraphStore` backed by `Microsoft.Data.Sqlite`. Tables: `entities`, `relationships`, `community_members`, `communities`.

### Leiden Algorithm

```csharp
namespace Rag.NET.Graph.Algorithms;

public static class Leiden
{
    public static IReadOnlyList<Community> Detect(
        GraphSnapshot graph,
        LeidenOptions? options = null);
}

public sealed class LeidenOptions
{
    public double Resolution { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 10;
    public int? MaxLevels { get; set; }
    public int RandomSeed { get; set; } = 42;
}
```

Full hierarchical Leiden: local moving, refinement, aggregation. Produces multi-level community hierarchy. Internal implementation designed for future extraction to a standalone `Rag.NET.Graph.Leiden` package.

### PageRank

```csharp
namespace Rag.NET.Graph.Algorithms;

public static class PageRank
{
    public static IReadOnlyDictionary<string, double> Compute(
        GraphSnapshot graph,
        double dampingFactor = 0.85,
        int maxIterations = 100,
        double tolerance = 1e-6);
}
```

Standard iterative PageRank on the entity-relationship graph.

## Package 2: Rag.NET.GraphRag

### Ingestion: Entity Extraction

`GraphEntityExtractionBehavior` — `IIngestionBehavior`, positioned after `EmbeddingBehavior`, before `StorageBehavior`.

**Flow per chunk:**
1. Call LLM with extraction prompt → get entities and relationships as structured JSON
2. Gleaning: `GleaningPasses` follow-up calls — "Are there any entities or relationships you missed?" — merge results
3. Entity resolution: case-insensitive dedup, accumulate descriptions across chunks
4. When accumulated entity description exceeds `MaxEntityDescriptionLength`, call LLM to summarize
5. Store entities and relationships in `IGraphStore`
6. Embed entity/relationship descriptions, create `EmbeddedChunk` objects with metadata:
   - `graph_type` = `entity` | `relationship`
   - `graph_entity_name`, `graph_entity_type` (for entities)
   - `graph_source_entity`, `graph_target_entity` (for relationships)
7. Append to `ctx.EmbeddedChunks` → stored alongside document chunks

### Ingestion: Community Detection

`CommunityDetectionBehavior` — `IIngestionBehavior`, runs after `GraphEntityExtractionBehavior`.

**Flow:**
1. Load full graph from `IGraphStore`
2. Run Leiden → hierarchical community assignments
3. Compute PageRank scores, write back to `IGraphStore`
4. For each community: concatenate member entity descriptions + relationship descriptions, call LLM to generate community report
5. Embed community reports, create `EmbeddedChunk` objects with metadata:
   - `graph_type` = `community_report`
   - `community_id`, `community_level`
6. Store community membership in `IGraphStore`
7. Append to `ctx.EmbeddedChunks`

### Retrieval: Local Search

`GraphLocalSearchBehavior` — `IRetrievalBehavior`, positioned before `RerankingBehavior`.

**Flow:**
1. Embed query, search `IVectorStore` for top-K matching entities (`graph_type=entity`)
2. For each matched entity, traverse `IGraphStore`: fetch neighbors up to `LocalSearchDepth` hops
3. Collect: matched entities + neighbors + connecting relationships + community reports for involved entities + source chunks linked to those entities
4. Score: combine vector similarity with PageRank weight (`PageRankWeight` blending factor)
5. Return as `SearchResult[]` — downstream behaviors (reranking, etc.) work unchanged

### Retrieval: Global Search

`GraphGlobalSearchBehavior` — `IRetrievalBehavior`, positioned before `RerankingBehavior`.

**Flow:**
1. Load all community reports from `IVectorStore` (`graph_type=community_report`)
2. Shuffle and batch reports into groups fitting the context window
3. Map: for each batch, LLM call — "Given these community reports, answer: {query}"
4. Reduce: combine all partial answers via final LLM call
5. Return synthesized answer as a single `SearchResult`

### Mode Selection

`GraphRagRetrievalMode` enum: `Local` (default), `Global`, `Auto`.

Auto mode: LLM classifies the query ("Is this asking about specific entities/facts, or about broad themes/patterns?") and routes accordingly.

## Configuration

### GraphRagOptions (ingestion)

| Property | Type | Default | Purpose |
|---|---|---|---|
| `Enabled` | bool | true | Toggle on/off |
| `GleaningPasses` | int | 1 | Follow-up extraction passes per chunk |
| `EntityTypes` | string[]? | null | Constrain to these entity types (null = open) |
| `RelationshipTypes` | string[]? | null | Constrain to these relationship types |
| `MaxEntityDescriptionLength` | int | 500 | Trigger LLM summarization threshold |
| `EntityExtractionPrompt` | string | sensible default | Extraction prompt template |
| `GleaningPrompt` | string | sensible default | Follow-up prompt template |
| `CommunityReportPrompt` | string | sensible default | Community summarization prompt |
| `ExtractionChatClient` | IChatClient? | null | Optional cheaper model for extraction |
| `SummarizationChatClient` | IChatClient? | null | Optional model for reports |

### GraphRagRetrievalOptions (retrieval)

| Property | Type | Default | Purpose |
|---|---|---|---|
| `Mode` | GraphRagRetrievalMode | Local | Local / Global / Auto |
| `LocalSearchDepth` | int | 1 | Hop depth for local traversal |
| `LocalTopEntities` | int | 10 | Top-K entities to start from |
| `PageRankWeight` | double | 0.3 | Blend weight: PageRank vs. similarity |
| `GlobalBatchSize` | int? | null | Reports per map batch (null = auto) |
| `GlobalChatClient` | IChatClient? | null | Optional model for map-reduce |

## DI Registration

```csharp
services.AddRagNet(
    configure: rag => rag.UseGraphRag(
        options => { options.GleaningPasses = 1; },
        retrieval: options => { options.Mode = GraphRagRetrievalMode.Local; },
        graph: store => store.UseSqlite("graphrag.db")),
    ingestion: p => p
        .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
        .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior)),
    retrieval: p => p
        .Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))
);
```

## Task Breakdown

| # | Task | Parallel | Dependencies |
|---|---|---|---|
| 1 | Rag.NET.Graph scaffolding + data models | Yes | — |
| 2 | Rag.NET.GraphRag scaffolding | Yes | — |
| 3 | IGraphStore + SqliteGraphStore | No | After 1 |
| 4 | Leiden algorithm | Yes | After 1 |
| 5 | PageRank algorithm | Yes | After 1 |
| 6 | GraphRagOptions + GraphRagRetrievalOptions | Yes | After 2 |
| 7 | GraphEntityExtractionBehavior | No | After 3, 6 |
| 8 | CommunityDetectionBehavior | No | After 4, 5, 7 |
| 9 | GraphLocalSearchBehavior | No | After 3, 6 |
| 10 | GraphGlobalSearchBehavior | No | After 6 |
| 11 | UseGraphRag DI registration | No | After 7, 8, 9, 10 |
| 12 | Tests | No | After 11 |
| 13 | Documentation | Yes | — |
| 14 | Benchmarks | No | After 12 |
| 15 | Build + test | No | After all |
