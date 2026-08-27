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

**Local Search** — For specific factual questions:
1. Find entities matching the query via vector similarity
2. Collect everything the graph attaches to them — relationships, community reports, source chunks
3. Assemble a token-budgeted context window and answer from it

**Global Search** — For broad thematic questions:
1. Collect all community reports
2. Map: LLM answers the query per batch of reports
3. Reduce: LLM combines partial answers into a final response

**Local search is not part of the retrieval pipeline; global search is, and stays opt-in.**
`UseGraphRag` registers `IGraphRagSearch` as a service — local search is a call you make
directly, not a behaviour that runs on every retrieval. `GraphGlobalSearchBehavior` is a
retrieval behaviour you add yourself when you want it: it re-enters the pipeline to fetch
community reports and runs an LLM map-reduce over them **on every query**, so that is not
something a bare `UseGraphRag()` should switch on.

## Quick Start

```csharp
// Install packages:
// dotnet add package Rag.NET.GraphRag
// dotnet add package Rag.NET.Graph

services.AddRagNet(rag => rag.UseGraphRag(
    options => { options.GleaningPasses = 1; },
    localSearch: options => { options.MaxContextTokens = 12_000; },
    graph: store => store.UseSqlite("graphrag.db")));
```

That is the whole registration. `UseGraphRag` places `GraphEntityExtractionBehavior` after
`EmbeddingBehavior` and `CommunityDetectionBehavior` after that. Neither search behaviour is
placed in the retrieval pipeline: local search is `IGraphRagSearch`, a service you call directly
rather than a pipeline behaviour, and `GraphGlobalSearchBehavior` is deliberately left out — it
runs an LLM map-reduce over community reports on every query, so it stays opt-in.

### Adding global search

Earlier versions of this page taught a four-delegate form, because `UseGraphRag` used to register its behaviors without placing any of them and the delegates were the only way into a pipeline. That form still works and still takes precedence — use it to place `GraphGlobalSearchBehavior` yourself, or to move the ingestion behaviours to different positions:

```csharp
services.AddRagNet(
    configure: rag => rag.UseGraphRag(
        graph: store => store.UseSqlite("graphrag.db")),
    ingestion: p => p
        .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
        .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior)),
    retrieval: p => p
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
    options.GlobalBatchSize = 5;                  // Reports per map batch — when set, must be greater than 0
    options.GlobalReportCandidates = 50;          // Reports fetched when none were handed down — when set, > 0
    options.GlobalChatClient = cheapModel;         // Optional for map-reduce
});
```

These are validated at registration too. `GlobalBatchSize = 0` would hang global search in an infinite batching loop; `GlobalReportCandidates = 0` would ask the store for no reports and silently restore the do-nothing behaviour below.

`GlobalReportCandidates` exists because global search was, in practice, unreachable. It maps and reduces over chunks tagged `graph_type = community_report`, partitioned out of whatever retrieval handed it — and a corpus produces a few hundred long, general reports against tens of thousands of short, specific entity and article chunks, with nothing reserving the reports a slot. Over a sixty-article corpus not one report appeared in a dense top-500, so the map phase never ran and the behavior returned its input untouched, looking to every caller as though it had worked. It now re-enters the retrieval pipeline with a metadata filter of its own when it is handed no reports, fetching this many. Any `MetadataFilter` you set is preserved — only the graph-type key is added — and the second retrieval is skipped entirely when the first already contains reports.

> **Which search runs is a registration decision, not a setting.** Call `IGraphRagSearch` directly for local search, or add `GraphGlobalSearchBehavior` to the retrieval pipeline for global search. There is deliberately no `Mode` property — one existed until 0.1.0, was never read by any behavior, and is described in issue #104.

> The PageRank blend has been removed. It scored local search by mixing PageRank into dense
> similarity, which is not part of Microsoft's local search; at its shipped default the blend
> demoted the very chunks the graph walk had reached, and at weight 0 it reproduced the plain
> candidate set on 2,255 of 2,255 queries. Local search is now
> `LocalSearch.IGraphRagSearch`, measured at 0.3459 overall and 0.8603 on inference questions.

### The graph's chunks live in their own store

GraphRAG creates entity, relationship and community-report chunks. **They are stored separately from
your documents**, and that separation is the single most valuable change measured in this project.

They used to share one store — 303,503 synthetic units beside 17,648 article chunks on MultiHop-RAG
— and dense retrieval treated them as peers of the text, so a six-chunk window filled with entity
descriptions instead of article content. With depth and chunking held constant:

| | nDCG@10 | answer accuracy |
|---|---|---|
| documents only | 0.63967 | 0.350 |
| shared store | 0.59658 | **0.138** |

On 46 of 50 queries, removing the synthetic chunks reconstructed the documents-only context
*byte-identically*: they were displacing article chunks without changing which ones would otherwise
win.

By default the graph's chunks go to a separate **in-memory** store. Point it somewhere real for
anything beyond a trial:

```csharp
rag.UseGraphRag(
    graph:  g => g.UseSqlite("graphrag.db"),        // the graph structure
    chunks: c => c.Use(myGraphChunkVectorStore));   // the graph's chunks
```

> **The default is in-memory even when your document store is not.** Graph chunks are then discarded
> at process exit while a configured graph store persists, so the two halves disagree after a restart
> until the next ingest. Configure `chunks:` unless you mean that.

**Nothing is lost from retrieval.** Local search seeds from the graph chunk store and global search
reads community reports from it — each asks the store that holds what it needs, which is cheaper than
the old arrangement: global search no longer makes a second pass through your whole retrieval
pipeline to find reports.

**Upgrading an existing index:** re-ingest. Nothing removes synthetic chunks already written into a
document store; the routing applies to what is ingested from now on.

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

**Local search is not part of the retrieval pipeline.** It is its own entry point:

```csharp
var search = provider.GetRequiredService<IGraphRagSearch>();

var answer = await search.LocalSearchAsync("How is Ångström related to Kelvin?");
Console.WriteLine(answer.Answer);

// Or just the context, without paying for a completion:
var context = await search.BuildLocalContextAsync("How is Ångström related to Kelvin?");
Console.WriteLine(context.Text);

// With prior conversation, oldest turn first — folded into entity selection AND rendered as
// its own context section:
var history = new List<ConversationTurn>
{
    new(ConversationRole.User, "Who discovered Ångström?"),
    new(ConversationRole.Assistant, "Anders Jonas Ångström."),
};
var followUp = await search.LocalSearchAsync("And what unit is named after him?", history);
```

`LocalSearchAsync` and `BuildLocalContextAsync` are each two overloads: the query alone, or the
query plus `IReadOnlyList<ConversationTurn>` history — the single-argument overload is exactly the
two-argument one called with an empty list.

Configure it through `UseGraphRag`'s `localSearch:` delegate:

```csharp
rag.UseGraphRag(localSearch: o =>
{
    o.MaxContextTokens = 12_000;   // the whole budget
    o.CommunityProportion = 0.15;  // reports
    o.TextUnitProportion = 0.5;    // source chunks; the rest goes to entities and relationships
    o.TopKEntities = 10;
    o.ResponseType = "multiple paragraphs";

    o.ConversationHistoryMaxTurns = 5;       // question-and-answer PAIRS, not messages; 0 = no history
    o.IncludeUserTurnsOnly = true;           // render only the user's questions, not the answers
    o.ConversationHistoryRecencyBias = false; // false = keep the OLDEST pairs when the cap trims — see below
});
```

#### What it does

It builds a **context window**, in this order, each section under its own slice of the token
budget:

| Section | Budget | Contents |
|---|---|---|
| `Conversation History` | off the top, before the other proportions are applied | Up to `ConversationHistoryMaxTurns` question-and-answer pairs from the conversation you passed in |
| `Reports` | 15% of what remains | Community reports, ordered by how many selected entities each community holds |
| `Entities` | 35%, shared | The selected entities, in similarity order |
| `Relationships` | with entities | In-network first, uncapped; then out-of-network by `(links, rank)` |
| `Sources` | 50% | The article chunks the selected entities were extracted from |

Entities are selected by searching entity-description embeddings, oversampling by
`EntityOversampleScaler` — and, faithfully to Microsoft, **not truncating back**, so the default
selects up to 20 entities for a `TopKEntities` of 10. Set the scaler to 1 for exactly `TopKEntities`.

It ranks no documents and re-scores nothing. Everything the model sees, it sees because the graph
put it there.

#### Conversation history

A `ConversationTurn` is a `Role` (`User`, `Assistant`, or `System`) and its `Content`. History
reaches an answer through two independent paths: the last `ConversationHistoryMaxTurns` user
turns are folded onto the query, newest first, before entity selection — a follow-up such as "and
who signed it?" embeds to almost nothing on its own, and it is the preceding questions that make
it match an entity — and separately, the conversation is grouped into question-and-answer pairs
and rendered as its own `Conversation History` section, capped at the same
`ConversationHistoryMaxTurns`.

**The two paths disagree about which end of the conversation they keep, on purpose — this is
upstream's own behaviour, reproduced rather than smoothed over.** The fold onto the query takes
the *newest* questions first. The rendered section, at the shipped
`ConversationHistoryRecencyBias = false`, keeps the **oldest** pairs when the conversation is
longer than the cap — which is the surprising half: Microsoft's own `recency_bias` parameter
defaults to `true`, but local search's only call site passes `recency_bias=False`, so the
rendered history a model actually sees is the *beginning* of the conversation, not its most
recent exchanges. Set `ConversationHistoryRecencyBias = true` for the newest-first behaviour most
readers expect.

#### Why it is not a retrieval behaviour

It used to be, and that is how the library lost it. An `IRetrievalBehavior` takes a ranked
candidate list and returns a ranked candidate list, which leaves nowhere to put entities,
relationships or reports — so the old implementation blended PageRank into the scores instead. On
MultiHop-RAG that blend was the **entire** measured difference between local search and plain dense
retrieval of the same candidates: at `PageRankWeight = 0` the two rankings were **identical on
2,255 of 2,255 queries**, so the whole −0.02761 nDCG@10 was that one default.

The trade is explicit: **local search no longer composes with hybrid search or reranking.** You
pick this or you pick the pipeline. The composition that existed before was not real — the blend
re-scored candidates the graph had no say in choosing.

#### What it measures, now that it exists

Measured 2026-08-20 over the whole 609-article MultiHop-RAG corpus, 2,556 queries against
`openai/gpt-4o-mini` at temperature 0, scored by the paper's own rule over the 2,255 judged
queries. Every arm sees the same prompt; only the context differs.

| arm | overall | inference | comparison | temporal | abstains on nulls |
|---|---|---|---|---|---|
| dense (article chunks only) | 0.3499 | 0.7721 | 0.1636 | 0.0326 | 48.5% |
| the old PageRank blend | 0.2102 | 0.4620 | 0.1005 | 0.0189 | 40.5% |
| global search | 0.5951 | 0.8444 | 0.4953 | 0.3928 | 9.3% |
| **local search, as specified** | **0.3459** | **0.8603** | 0.0736 | 0.0257 | 34.6% |

**On entity questions this is the strongest result the project has measured** — 0.8603, above
global search and above dense. It commits on 91.4% of inference queries at precision 0.941, where
dense commits on 82% at 0.943: the same accuracy, far more willing to answer.

**Overall it is level with dense, and the reason is worth knowing before you choose it.** Read the
yes/no columns against their base rates — comparison gold is 60% yes and temporal 46% yes, so
answering "yes" every time scores 0.598 and 0.463. Local search scores 0.0736 and 0.0257 because
it *abstains*, committing on only 8.8% of comparison and 4.3% of temporal questions. It also
abstains on just 34.6% of unanswerable questions against dense's 48.5%. So it declines answerable
comparisons while committing on unanswerable ones: the graph context makes the model confident
about entities and unwilling about comparisons.

**Choose it for entity and relationship questions. Do not choose it for yes/no comparisons** —
global search is the better arm there, and plain dense retrieval is better than both at knowing
when to refuse.

This also revises what this guide said before. Milestone 5.2 concluded that GraphRAG did not help
on this corpus; that conclusion was drawn from the PageRank blend described above, which is not
Microsoft's local search. Against the blend, the specified implementation is +0.1357 overall and
+0.3983 on inference.

See `docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md` for the reading of Microsoft's
implementation this follows, and for the deviations it cannot avoid.

#### Requirements

The Sources section needs a vector store implementing `IChunkLookup` — the source chunks are chosen
by graph provenance, not by score, so there is no query that returns them. `InMemoryVectorStore`
implements it; the remote backends do not yet (#318). Without it, local search logs a warning and
Sources comes back empty, spending half the budget on nothing.

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

- **Local Search** (`IGraphRagSearch`): `BuildLocalContextAsync` makes zero LLM calls — one
  embedding call to select entities, then batched graph-store reads for their relationships,
  community reports and source chunks. `LocalSearchAsync` adds exactly one chat completion on top,
  to generate the answer from that context.
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
