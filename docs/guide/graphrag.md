# GraphRAG — Entity Extraction + Community Summarization

GraphRAG builds a knowledge graph from your documents at ingestion time — extracting entities, relationships, and detecting communities — then uses this graph structure for retrieval. Unlike pure vector search, GraphRAG can answer multi-hop questions ("How is X related to Y?") and broad thematic queries ("What are the main themes across this corpus?").

## When to Use GraphRAG

- **Multi-hop reasoning** — questions that require connecting information across different parts of a document or corpus
- **Thematic analysis** — "What are the main themes?" or "Summarize the key topics"
- **Entity-centric retrieval** — questions about specific people, organizations, or concepts and their relationships
- **Large corpora** where understanding the global structure matters as much as individual facts

Avoid GraphRAG for simple factual Q&A where standard vector search suffices — GraphRAG adds significant ingestion cost (LLM calls per chunk for extraction + community reports).

## Architecture

Two packages:

- **`Rag.NET.Graph`** — Standalone graph library (no Rag.NET dependency). Leiden community detection, PageRank, IGraphStore abstraction with SQLite default. Usable independently.
- **`Rag.NET.GraphRag`** — GraphRAG behaviors for Rag.NET. Entity extraction, community detection, local + global search.

### Hybrid Storage Model

| Data | Storage | Purpose |
|------|---------|---------|
| Entities (name, type, description) | IGraphStore + IVectorStore | Graph for traversal, vectors for semantic matching |
| Relationships (source, target, description) | IGraphStore + IVectorStore | Structure + similarity |
| Community reports (summary text) | IGraphStore + IVectorStore | Hierarchy + global search |
| Original document chunks | IVectorStore only | Standard RAG retrieval |

## How It Works

### Ingestion

1. **Entity Extraction** — For each chunk, an LLM extracts entities (name, type, description) and relationships (source, target, description, weight)
2. **Gleaning** — Follow-up LLM passes ask "Did I miss anything?" to improve recall (configurable, default 1 pass)
3. **Graph Building** — Entities and relationships stored in IGraphStore, descriptions embedded in IVectorStore
4. **Community Detection** — Leiden algorithm detects clusters of related entities
5. **PageRank** — Computes importance scores for each entity
6. **Community Reports** — LLM generates summary reports for each community, embedded and stored

### Retrieval

**Local Search** (default) — For specific factual questions:
1. Find entities matching the query via vector similarity
2. Traverse graph neighbors (configurable depth)
3. Collect: entities + relationships + community reports
4. Score: blend vector similarity with PageRank importance

**Global Search** — For broad thematic questions:
1. Collect all community reports
2. Map: LLM answers the query per batch of reports
3. Reduce: LLM combines partial answers into a final response

**Auto Mode** — LLM classifies the query and routes to Local or Global automatically.

## Quick Start

```csharp
// Install packages:
// dotnet add package Rag.NET.GraphRag
// dotnet add package Rag.NET.Graph

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

## Configuration

### Ingestion Options

```csharp
rag.UseGraphRag(options =>
{
    options.Enabled = true;                          // Toggle on/off
    options.GleaningPasses = 1;                      // Follow-up extraction passes
    options.EntityTypes = ["Person", "Organization"]; // Constrain types (null = open)
    options.RelationshipTypes = null;                 // Constrain relationship types
    options.MaxEntityDescriptionLength = 500;         // Summarization threshold
    options.ExtractionChatClient = cheapModel;        // Optional cheaper model
    options.SummarizationChatClient = cheapModel;     // Optional for reports
});
```

### Retrieval Options

```csharp
rag.UseGraphRag(retrieval: options =>
{
    options.Mode = GraphRagRetrievalMode.Local;  // Local / Global / Auto
    options.LocalSearchDepth = 1;                 // Hop depth
    options.LocalTopEntities = 10;                // Starting entities
    options.PageRankWeight = 0.3;                 // PageRank vs similarity blend
    options.GlobalBatchSize = 5;                  // Reports per map batch
    options.GlobalChatClient = cheapModel;         // Optional for map-reduce
});
```

### Graph Store

```csharp
rag.UseGraphRag(graph: store =>
{
    store.UseSqlite("graphrag.db");  // SQLite-backed (default)
});
```

## Search Modes in Detail

### Local Search

Best for: "What companies did John Smith work for?" or "How is React related to Next.js?"

The behavior:
1. Embeds the query and finds matching entity chunks in the vector store
2. Takes the top-K entities (configurable via `LocalTopEntities`)
3. Traverses the graph to find neighbors within `LocalSearchDepth` hops
4. Collects all related entities, relationships, and community reports
5. Blends scores: `(1 - PageRankWeight) * similarity + PageRankWeight * pageRank`

### Global Search

Best for: "What are the main themes in this document?" or "Summarize the key findings"

The behavior:
1. Collects all community report chunks from the vector store
2. Shuffles and batches them
3. Map phase: LLM answers the query for each batch
4. Reduce phase: LLM combines all partial answers
5. Returns a single synthesized answer

### Auto Mode

An LLM classifies the query as "specific/factual" (-> Local) or "broad/thematic" (-> Global) before routing.

## Cost and Performance

### Ingestion Cost

GraphRAG is the most expensive ingestion strategy — LLM calls per chunk:

| Document size | Entity extraction | Gleaning (1 pass) | Community reports | Total LLM calls |
|---------------|------------------|--------------------|-------------------|-----------------|
| 10 chunks | 10 | 10 | 2-3 | ~23 |
| 50 chunks | 50 | 50 | 5-10 | ~110 |
| 200 chunks | 200 | 200 | 10-20 | ~420 |

**Mitigation:**
- Use a cheaper model via `ExtractionChatClient` (e.g. GPT-4o-mini, Haiku)
- Set `GleaningPasses = 0` to skip follow-up passes
- Constrain `EntityTypes` to reduce noise

### Retrieval Cost

- **Local Search**: Zero additional LLM calls. Graph traversal + vector search only.
- **Global Search**: LLM calls proportional to number of communities (map) + 1 (reduce).

### Storage

Entities, relationships, and community reports are stored as additional embedded chunks. Typical overhead: 20-50% more vectors depending on entity density.

## Standalone Graph Library

`Rag.NET.Graph` is usable independently — no Rag.NET dependency required:

```csharp
// Leiden community detection
var graph = new GraphSnapshot(entities, relationships, []);
var communities = Leiden.Detect(graph, new LeidenOptions { Resolution = 1.0 });

// PageRank
var ranks = PageRank.Compute(graph);

// SQLite graph store
await using var store = new SqliteGraphStore("graph.db");
await store.AddEntitiesAsync(entities);
await store.AddRelationshipsAsync(relationships);
var neighbors = await store.GetNeighborsAsync("EntityName", depth: 2);
```

## Pipeline Positioning

```
Ingestion:  Parse → Chunk → Embed → [Entity Extraction] → [Community Detection] → Store
Retrieval:  VectorStore → Ensemble → Filter → [GraphRAG Local/Global] → Rerank → ...
```

## Troubleshooting

**No entities extracted**
- Verify IChatClient is registered in DI
- Check LLM response format — extraction expects JSON with "entities" and "relationships" arrays
- Try increasing chunk size — very short chunks may not contain extractable entities

**Too many/few communities**
- Adjust Leiden `Resolution` parameter via LeidenOptions
- Higher resolution = more, smaller communities

**Global search returns empty**
- Ensure CommunityDetectionBehavior runs during ingestion
- Verify community reports were embedded (check for `graph_type=community_report` in vector store)

**High ingestion cost**
- Use `ExtractionChatClient` with a cheaper model
- Set `GleaningPasses = 0`
- Constrain `EntityTypes` to reduce extraction scope
