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
4. **Community Detection** — the `Leiden` type detects clusters of related entities. It implements Traag/Waltman/van Eck's Leiden algorithm over modularity — Louvain's local moving and aggregation with the paper's refinement phase between them — so **every returned community is connected in the subgraph it induces**, which is the guarantee that paper exists to supply. See the type's XML remarks for where the guarantee comes from and what it still does not promise
5. **PageRank** — Computes importance scores for each entity
6. **Community Reports** — LLM generates summary reports for each community, embedded and stored

### Retrieval

**Local Search** (default) — For specific factual questions:
1. Find entities matching the query via vector similarity
2. Traverse graph neighbors (configurable depth), collecting their PageRank scores
3. Score: blend vector similarity with PageRank importance

**Global Search** — For broad thematic questions:
1. Collect all community reports
2. Map: LLM answers the query per batch of reports
3. Reduce: LLM combines partial answers into a final response

**Which search runs is decided by the behaviors you register**, not by a setting. `UseGraphRag` puts `GraphLocalSearchBehavior` in the pipeline for you; add `GraphGlobalSearchBehavior` yourself when you want it, or both — each runs on the chunks it recognises.

Global search is left out of the default on cost, not on preference. Local search traverses the graph over results retrieval already produced; global search re-enters the pipeline to fetch community reports and runs an LLM map-reduce over them **on every query**. That is not something a bare `UseGraphRag()` should switch on.

## Quick Start

```csharp
// Install packages:
// dotnet add package Rag.NET.GraphRag
// dotnet add package Rag.NET.Graph

services.AddRagNet(rag => rag.UseGraphRag(
    options => { options.GleaningPasses = 1; },
    retrieval: options => { options.LocalSearchDepth = 1; },
    graph: store => store.UseSqlite("graphrag.db")));
```

That is the whole registration. `UseGraphRag` places `GraphEntityExtractionBehavior` after `EmbeddingBehavior`, `CommunityDetectionBehavior` after that, and `GraphLocalSearchBehavior` before `RerankingBehavior`.

### Adding global search, or choosing the positions yourself

Earlier versions of this page taught a four-delegate form, because `UseGraphRag` used to register its behaviors without placing any of them and the delegates were the only way into a pipeline. That form still works and still takes precedence — use it for global search, or to put anything somewhere other than its default:

```csharp
services.AddRagNet(
    configure: rag => rag.UseGraphRag(
        graph: store => store.UseSqlite("graphrag.db")),
    ingestion: p => p
        .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
        .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior)),
    retrieval: p => p
        .Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))
        .Add<GraphGlobalSearchBehavior>(before: typeof(RerankingBehavior))
);
```

`Add` is idempotent and the `ingestion:` and `retrieval:` delegates run before `configure` does, so your placement lands first and `UseGraphRag`'s default is skipped. Each behavior ends up in the chain exactly once, where you put it.

`UseGraphRag` throws `InvalidOperationException` if it is called on a `RagBuilder` that did not come from `AddRagNet`, since there is no pipeline to place anything in. It no longer returns quietly having enabled nothing.

## Configuration

### Ingestion Options

```csharp
rag.UseGraphRag(options =>
{
    options.Enabled = true;                          // Toggle on/off
    options.GleaningPasses = 1;                      // Follow-up extraction passes (0 = skip)
    options.EntityTypes = ["Person", "Organization"]; // Constrain entity types (null = open)
    options.RelationshipTypes = null;                 // Constrain relationship kinds (null = open)
    options.MaxEntityDescriptionLength = 500;         // Summarization threshold — must be greater than 0
    options.ExtractionChatClient = cheapModel;        // Optional cheaper model
    options.SummarizationChatClient = cheapModel;     // Optional for reports

    options.Leiden.Resolution = 1.0;      // Clustering granularity — must be > 0
    options.Leiden.MaxIterations = 10;    // Local-moving passes per level — must be > 0
    options.Leiden.MaxLevels = null;      // null = aggregate until no improvement
    options.Leiden.RandomSeed = 42;       // Fixed, so clustering is reproducible
    options.Leiden.Randomness = 0.01;     // θ in the refinement's draw — must be > 0

    options.MaxCommunityReportPromptLength = 50_000;  // Report prompt cap, characters — must be > 0
    options.CommunityReportConcurrency = 4;           // Report LLM calls in flight at once — must be > 0
});
```

`UseGraphRag` validates the configured options at registration and throws `ArgumentException` from the configuring line. A negative `MaxEntityDescriptionLength` would throw mid-ingestion on the first extracted entity; zero would silently empty every entity description. A `Leiden.Resolution` of zero or below is rejected the same way: resolution scales modularity's penalty term, so zero removes the penalty entirely and returns one community for every connected graph.

`Randomness` is θ from the Leiden paper, and it is validated where it is set rather than at registration: the refinement divides by it inside `exp(ΔQ / θ)`, so a zero, negative or non-finite value throws `ArgumentOutOfRangeException` from the assigning line. It controls how sharply the refinement's merge draw prefers the best candidate — small values approach greedy, large values approach uniform over every legal merge — and 0.01 is the value the paper's own experiments use. **Randomised does not mean unreproducible:** every draw comes from `RandomSeed`, so a fixed seed still gives a fixed partition.

`options.Leiden` reaches the clustering that community detection runs. Before it existed, `CommunityDetectionBehavior` called the clusterer without options, so every setting on `LeidenOptions` was unreachable through `UseGraphRag` and the defaults were the only values that had ever run — despite this guide telling you to adjust them.

`MaxCommunityReportPromptLength` bounds the prompt used to summarise one community. Without it the prompt's size was a property of your corpus rather than of the code — every member entity's whole merged description went into one message — and a large community could build a prompt no model would accept. A community that exceeds the budget is **truncated, not rejected**: members are emitted in PageRank order so the least central drop out first, three quarters of the budget goes to entities and the rest to the relationships between them, and the prompt says what was left out so the summariser is not shown a fragment as though it were the whole. Truncation is tagged on the `ragnet.graphrag.communities` activity as `graphrag.community.report.truncated`.

`CommunityReportConcurrency` bounds how many community-report LLM calls are in flight at once. Until it existed the report loop awaited one community at a time — on a 609-article corpus that is 3,587 sequential round trips, hours in a loop where every report depends only on its own community. **Parallel does not mean unrepeatable here:** every prompt is built first, in the community order Leiden returned and PageRank order inside each, and each response is written back to the community whose prompt produced it, so two runs at different concurrencies produce the same reports on the same communities in the same order. The default of 4 is deliberately modest because your provider's rate limit, not this number, is the real ceiling — parallelising into a `429` storm trades one wait for another — so measure against the provider before raising it. Measured once, 2026-08-15, against OpenRouter's `openai/gpt-4o-mini` at temperature 0 with the report prompt bounded at 50,000 characters: **4.62 s per report at 1 in flight, 1.13 s at 4, 0.63 s at 8, with zero retries at every level** — near-linear to 8 on that provider, on that day, over three disjoint sets of 45–70 reports. That is one provider and one model; yours may throttle sooner. The value in force is tagged on the same activity as `graphrag.community.report.concurrency`.

`EntityTypes` and `RelationshipTypes` are enforced in two layers. The allowed lists are substituted into the extraction prompt's `{entity_types}` and `{relationship_types}` placeholders (when they are null the placeholders render the open-extraction guidance instead), and anything the LLM still returns outside a configured list is dropped — case-insensitively — before it reaches the graph store or the embedded chunks, including gleaning-pass output. A custom `EntityExtractionPrompt` without the placeholders still gets the filtering layer, so the constraint holds regardless of prompt. Relationships carry their kind in the `description` field (a concise verb phrase), so `RelationshipTypes` constrains that field. An empty array behaves like null rather than silently dropping every extraction.

### Retrieval Options

```csharp
rag.UseGraphRag(retrieval: options =>
{
    options.LocalSearchDepth = 1;                 // Hop depth — must be greater than 0
    options.LocalTopEntities = 10;                // Starting entities — must be greater than 0
    options.PageRankWeight = 0.0;                 // PageRank vs similarity blend — DEFAULT 0, see below
    options.GlobalBatchSize = 5;                  // Reports per map batch — when set, must be greater than 0
    options.GlobalReportCandidates = 50;          // Reports fetched when none were handed down — when set, > 0
    options.GlobalChatClient = cheapModel;         // Optional for map-reduce
});
```

These are validated at registration too. `LocalSearchDepth` or `LocalTopEntities` at zero would silently disable local graph search; a `PageRankWeight` outside `[0, 1]` would give one blend term a negative coefficient; `GlobalBatchSize = 0` would hang global search in an infinite batching loop; `GlobalReportCandidates = 0` would ask the store for no reports and silently restore the do-nothing behaviour below.

`GlobalReportCandidates` exists because global search was, in practice, unreachable. It maps and reduces over chunks tagged `graph_type = community_report`, partitioned out of whatever retrieval handed it — and a corpus produces a few hundred long, general reports against tens of thousands of short, specific entity and article chunks, with nothing reserving the reports a slot. Over a sixty-article corpus not one report appeared in a dense top-500, so the map phase never ran and the behavior returned its input untouched, looking to every caller as though it had worked. It now re-enters the retrieval pipeline with a metadata filter of its own when it is handed no reports, fetching this many. Any `MetadataFilter` you set is preserved — only the graph-type key is added — and the second retrieval is skipped entirely when the first already contains reports.

> **Which search runs is a registration decision, not a setting.** Add `GraphLocalSearchBehavior`, `GraphGlobalSearchBehavior`, or both to the retrieval pipeline; each runs on the chunks it recognises. There is deliberately no `Mode` property — one existed until 0.1.0, was never read by any behavior, and is described in issue #104.

### The graph's own chunks are hidden from results by default

GraphRAG embeds entities, relationships and community reports into **the same vector store** as your
article chunks. On MultiHop-RAG that is 303,503 synthetic units beside 17,648 article chunks, and
dense retrieval treats them as peers of the text — so a top-6 window fills with entity descriptions
instead of article content.

Measured, with depth and chunking held constant:

| | nDCG@10 | answer accuracy |
|---|---|---|
| article-only store | 0.63967 | 0.350 |
| graph store, unfiltered | 0.59658 | **0.138** |
| graph store, filtered | — | **0.350** |

Filtering recovers all of it. On 46 of 50 queries the filtered context was *byte-identical* to the
article-only context: the synthetic chunks were displacing article chunks without changing which
ones would otherwise win.

So `FilterGraphChunksFromResults` defaults to `true`:

```csharp
rag.UseGraphRag(retrieval: options =>
{
    options.FilterGraphChunksFromResults = true;  // default
    options.GraphChunkOverFetchFactor    = 20;    // fetch TopK x this, then filter, then cut
});
```

**The graph behaviours are unaffected.** The filter runs *outside* them, so local search still
traverses from entity chunks and global search still maps over reports — only what reaches you is
filtered. Global search's synthesised answer is never filtered.

**Turn it off if you want the graph's own units in the model's context.** That is what local search
was described as being for; when it was measured, it cost 0.21 answer accuracy. The option exists so
the choice is yours, not because the evidence is balanced.

`GraphChunkOverFetchFactor` is a heuristic — 20 suits a store where synthetic units outnumber article
chunks about 17:1. A denser graph can still under-fill your `TopK`, and the behaviour tags
`graphrag.filter.underfilled` on its activity when that happens rather than leaving you to infer it.

### Keeping communities current

Community detection is a **whole-graph** operation: it loads the entire graph, runs Leiden and
PageRank over it, and writes every score back. It runs during ingestion, which is per document — so
until #300 it did all of that once per ingested document, against a graph growing throughout. On a
17,648-document corpus that is 17,648 whole-graph recomputes, and every one but the last was
discarded rather than merged, because detection is a pure function of the graph and each run
overwrites the previous one.

Ingestion now **debounces on graph growth**:

```csharp
rag.UseGraphRag(graph: null, options =>
{
    options.CommunityDetectionGrowthThreshold = 0.10;  // default: detect when entities grow 10%
});
```

Requiring 10% growth spaces detections geometrically, so their number is logarithmic in the corpus
rather than linear. Set it to `0` for the previous behaviour — detect on every document.

**The trade:** communities can be up to that fraction stale at the end of an ingest, because the
final document may not have triggered a detection. When they must be current — after a bulk load,
before measuring, or on a schedule — rebuild them explicitly:

```csharp
var rebuilder = serviceProvider.GetRequiredService<GraphProjectionRebuilder>();
var communities = await rebuilder.RebuildAsync(cancellationToken);
```

`RebuildAsync` ignores the threshold, resets its baseline, and replaces the stored report chunks.
Reports are written under the synthetic document id `graphrag://communities` rather than whichever
article happened to trigger detection, so they are addressable: deleting that id removes exactly the
reports and nothing else.

### Graph Store

```csharp
rag.UseGraphRag(graph: store =>
{
    store.UseSqlite("graphrag.db");  // SQLite-backed
});
```

**If you do not call `UseSqlite`, the graph is held in memory and discarded when the process
exits** — it is rebuilt from scratch on the next ingest, and graph construction is the expensive
half of GraphRAG. Give it a path unless you mean that.

#### Entity names are matched case-insensitively, in every script

`Ångström` and `ångström` are one entity, and so are `Москва`/`москва` and `Γεωργία`/`γεωργία`.
Folding happens in .NET rather than in SQL, because SQLite's `COLLATE NOCASE` folds `A`–`Z` and
nothing else — under it, non-ASCII names produced *two* rows for one subject and their descriptions
never merged.

The spelling you supply is preserved for display, and the first spelling seen wins: an entity does
not change how it reads in a report because a later document happened to shout its name.

Two consequences if you read the SQLite file directly rather than through `IGraphStore`:

- `entities.name` and the `relationships` endpoints hold the **folded** (upper-cased) key. Read
  `display_name`, `source_display` and `target_display` for the original spelling.
- **A graph file written before this change is migrated in place when opened**, which adds the
  display columns, folds the keys, and merges any duplicate rows the old collation allowed. Back it
  up first if that matters to you.

## Search Modes in Detail

### Local Search

Best for: "What companies did John Smith work for?" or "How is React related to Next.js?"

The behavior:
1. Takes the top-K entity chunks the vector store already matched (configurable via `LocalTopEntities`)
2. Traverses the graph to find neighbors within `LocalSearchDepth` hops, collecting their PageRank scores
3. Blends entity scores: `(1 - PageRankWeight) * similarity + PageRankWeight * pageRank`

**Step 3 does nothing by default, and that is the fix rather than an oversight** (issue #239).
PageRank is normalised to sum to one over all entities, so on a 62,000-entity graph its values are
around 1e-5, against similarities of 0.3–0.6. At the **old** default of `0.3` the blend *lowered*
every graph-connected entity chunk's score by roughly 30% relative to chunks the walk did not
reach — it demoted precisely the chunks it had traversed to. On MultiHop-RAG that was the entire
measured difference between local search and plain dense retrieval of the same candidates: at
`PageRankWeight = 0` the two rankings were **identical on 2,255 of 2,255 queries**, so the whole
−0.02761 nDCG@10 was this one default.

`PageRankWeight` now defaults to **0**, and at 0 the behaviour **skips the graph walk entirely** —
there is no point collecting scores nothing will read. Setting a non-zero weight is an opt-in, and
worth doing only once the two scales are reconciled; that is still open on #239. Deduplication is
unaffected either way, because it does not depend on the blend. Note also what the behavior does **not** do: it adds no candidates — the
traversal only collects PageRank scores — so it can reorder what retrieval found and cannot raise
recall above it. (At the default weight it does not even reorder; it deduplicates.) What it is for, then, is the *shape* of the context it hands the model (entity,
relationship and report chunks beside article chunks), which is what the answer-level evaluation in
Phase 5.2.2 measures.

### Global Search

Best for: "What are the main themes in this document?" or "Summarize the key findings"

The behavior:
1. Partitions the retrieved results, taking every community report chunk
2. Shuffles and batches them (`GlobalBatchSize` reports per batch)
3. Map phase: LLM answers the query for each batch
4. Reduce phase: LLM combines all partial answers
5. Prepends the single synthesized answer to the remaining results

Step 2's shuffle is seeded from a stable hash of the query since #241 — it was seeded from
`string.GetHashCode`, which .NET randomises per process, so the batches and every map prompt
differed run to run for the same query, and nothing keyed on those prompts could be replayed. The
same query over the same reports now produces the same order in every process.

**What was measured** (Phase 5.2.2, 2026-08-15, MultiHop-RAG, `gpt-4o-mini`, top-6 context, the
dataset authors' own accuracy rule): on the 816 questions whose answer is an entity, global search
answered **0.844** correctly against dense retrieval's **0.772** — a real gain, and the one place
in this programme where the graph path beats plain dense. On yes/no questions no arm beat an
always-"yes" baseline and global's apparent lead there is that it commits (it said "yes" 532
times and "no" 55); and it abstained on only 9% of unanswerable questions where dense abstained on
49%. So use it for questions where an answer must be *found* — synthesis across articles — and
expect it to guess rather than decline. Local search as shipped scored **0.210** against dense's
0.350 on the same questions, and dense over the graph store with no behaviour 0.138: what hurts
is the shared store handing the model entity and report chunks instead of article text, not the
graph. `docs/plans/2026-08-15-graphrag-answer-level-evaluation.md` has the design and the reading.

### Automatic routing

Not implemented, and not declared. Routing a query to Local or Global by classifying it as specific/factual versus broad/thematic is a real feature and a real cost — an extra LLM call per query — so it will arrive as one, with a benchmark behind it, rather than as an enum member that does nothing. Register the behaviors you want in the meantime.

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
- Leave `CommunityDetectionGrowthThreshold` at its default. Community detection is a *whole-graph*
  operation, and it used to run once per ingested document — on a 17,648-document corpus that was
  17,648 recomputes of a graph that reached 62,392 entities, and every one but the last was
  discarded. Setting the threshold to `0` restores that.

### Retrieval Cost

- **Local Search**: Zero additional LLM calls. At the default `PageRankWeight = 0` it performs *no
  graph traversal either* — the blend is the identity, so there would be nothing to read the
  traversal's PageRank scores. It deduplicates and returns what retrieval found. Set a non-zero
  weight and the walk runs, one neighbour query per seed entity.
- **Global Search**: one map call per batch of `GlobalBatchSize` community reports in the candidate set (5 by default), plus 1 reduce — not one per community. It is the reports that reach retrieval that cost, not the communities that exist.

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
- Adjust `options.Leiden.Resolution` in `UseGraphRag`'s ingestion options
- Higher resolution = more, smaller communities

**Global search returns empty**
- Ensure CommunityDetectionBehavior runs during ingestion
- Verify community reports were embedded (check for `graph_type=community_report` in vector store)
- **Or the reports are simply stale.** Detection is debounced on graph growth, so the last documents
  of an ingest may not have triggered one. Call `GraphProjectionRebuilder.RebuildAsync` and try
  again — that is what it is for, and it is the expected step after a bulk load
- The `graphrag.community.skipped` tag on the `ragnet.graphrag.communities` activity tells you a
  document was debounced rather than detected

**Global search returns results but never calls the LLM**
- It found no community reports in the candidate set and its own refetch also came back empty
- Check the `graphrag.community.refetched` tag on the `ragnet.graphrag.search` activity
- Confirm your vector store applies `SearchOptions.MetadataFilter`; the refetch relies on it

**High ingestion cost**
- Use `ExtractionChatClient` with a cheaper model
- Set `GleaningPasses = 0`
- Constrain `EntityTypes` to reduce extraction scope
- Check you have not set `CommunityDetectionGrowthThreshold = 0`, which recomputes the whole graph
  on every document
