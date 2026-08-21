# RAPTOR — Recursive Abstractive Processing for Tree-Organized Retrieval

RAPTOR builds a hierarchical tree of summaries — by default over the whole corpus, not one document at a time — so that retrieval can match at both fine-grained (leaf chunk) and abstract (summary) levels simultaneously. This addresses a core limitation of flat chunking: questions about a broad theme that spans several documents may not match any individual chunk well, and may not even be answerable from any single document's own summary.

## When to Use RAPTOR

- **Long documents** (10+ pages) where high-level questions are expected
- **Multi-topic documents** where readers may ask about themes that span sections
- **Knowledge bases** where both specific facts and broad overviews matter

Avoid RAPTOR for short documents (< 5 chunks) or when latency at ingestion time is critical — tree building requires LLM calls per cluster per level.

## How It Works

### Ingestion (Tree Building)

1. **Start with leaf chunks** — every chunk embedded so far, across the corpus (or one document's, under `PerDocument` scope — see [Tree Scope](#tree-scope))
2. **UMAP reduction** — reduce embedding dimensions (e.g. 1536 → 10) for efficient clustering
3. **GMM clustering** — soft-cluster chunks using Gaussian Mixture Models; BIC selects optimal cluster count
4. **Summarize each cluster** — concatenate chunk texts, call LLM to produce a summary
5. **Embed summaries** — generate embeddings for each summary
6. **Recurse** — repeat steps 2-5 on the summaries until one cluster remains (or MaxTreeDepth reached)
7. **Store everything** — leaf chunks + all summary levels go to the vector store

Each summary chunk carries metadata:
- `raptor_level` — tree depth (1 = first summary, 2 = summary of summaries, etc.)
- `raptor_cluster_id` — which cluster within the level
- `raptor_child_ids` — comma-separated chunk indices of children

### Retrieval

Three modes control how RAPTOR chunks participate in search:

| Mode | Behaviour | Best for |
|------|-----------|----------|
| **Blend** (default) | All levels participate via natural vector similarity | General use — let the embeddings decide |
| **Boost** | Multiply summary chunk scores by `SummaryBoostFactor` | When broad questions are common |
| **Filter** | Restrict to specific levels via `MinRaptorLevel` / `MaxRaptorLevel` | When you know the abstraction level needed |

## Tree Scope

`RaptorOptions.TreeScope` controls what set of chunks the tree is built over:

| Value | Behaviour |
|-------|-----------|
| **`Corpus`** (default) | Cluster across every leaf chunk ingested so far, corpus-wide — the mechanism the RAPTOR paper describes. A summary can span two documents that turn out to share a theme, which `PerDocument` can never produce. Requires an `IRaptorLeafStore`, because the vector store cannot enumerate what it holds. |
| **`PerDocument`** | Cluster within one document's chunks, at ingestion time. The library's original behaviour, kept fully supported — it is the control arm Phase 6.2.1 differences the corpus scope against. No leaf store required. |

**`Corpus` requires `Rag.NET.Raptor.Store`.** Pass `leafStorePath` to `UseRaptor` to register a `SqliteRaptorLeafStore` and enable it — this is what the Quick Start example above does. `UseRaptor` throws `ArgumentException` at registration if `TreeScope` is `Corpus` and no `leafStorePath` is given: there is nowhere to persist leaves between ingests otherwise.

**Ingesting one document no longer produces a tree immediately.** Under `Corpus` scope, a single ingest appends that document's leaves to the leaf store and nothing more. A tree is (re)built only once the corpus has grown by `CorpusGrowthThreshold` (default 0.10, i.e. 10%) since the last build — the same debounce shape as `GraphRagOptions.CommunityDetectionGrowthThreshold`, and for the same reason: clustering the whole corpus on every single ingest is expensive and grows worse as the corpus grows. Call `RaptorTreeRebuilder.RebuildAsync` to force a rebuild on demand — after a bulk load, before measuring, or on a schedule; it is registered whenever `leafStorePath` is supplied. Corpus summaries are filed under the reserved id `RaptorCorpusDocumentId.Value` (`raptor://corpus-tree`), never under a real document's id — a corpus-wide summary attributed to whichever document happened to trigger the build would misattribute it to one arbitrary article.

### When to choose `PerDocument`

- You need isolated per-document trees on purpose — for example multi-tenant document sets, where a cross-document summary would leak content between tenants.
- You cannot add a leaf store (`Rag.NET.Raptor.Store` or your own `IRaptorLeafStore`) to the deployment.
- You are differencing against `Corpus` scope, the way Phase 6.2.1 does.

Set it explicitly — an explicit value is clearer than code that depends silently on whichever way the default happens to point:

```csharp
services.AddRagNet(rag => rag.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument));
```

## Migration from the pre-v1.0 default

Before v1.0, `TreeScope` defaulted to `PerDocument`. Upgrading without changing anything now throws: `UseRaptor()` — or any call that does not set `TreeScope` explicitly — hits the `Corpus`-requires-`leafStorePath` check above and fails at registration with `ArgumentException`. Fix that first, one of two ways:

- Pass `leafStorePath` and add a reference to `Rag.NET.Raptor.Store` to opt into the new `Corpus` default, or
- Set `o.TreeScope = RaptorTreeScope.PerDocument` explicitly to keep the previous behaviour unchanged.

If you do move to `Corpus` scope, the summary chunks a previous `PerDocument` ingest already wrote are now stale: they are filed per document rather than under the corpus id, they overlap with nothing the corpus tree produces, and at retrieval time they compete for rank against real corpus summaries on an equal footing. **There is no automatic cleanup**, and deliberately so: old summary chunks carry a real `raptor_level` and a real `DocumentId`, so a heuristic guessing which chunks were RAPTOR's from those fields alone would occasionally guess wrong on someone else's data and delete it — worse than leaving stale summaries in place. The migration is manual:

1. Delete every chunk carrying `raptor_level` metadata from the vector store (leaf chunks have no such metadata and are unaffected).
2. Re-ingest your documents so their leaves land in the leaf store, or — if the leaves are already there — call `RaptorTreeRebuilder.RebuildAsync` once to build the corpus tree fresh.

## Quick Start

```csharp
// Install: dotnet add package Rag.NET.Raptor
//          dotnet add package Rag.NET.Raptor.Store   — Corpus scope (the default) needs a leaf store

services.AddRagNet(rag => rag.UseRaptor(leafStorePath: "raptor-leaves.db"));
```

That is the whole registration. `UseRaptor` places `RaptorIngestionBehavior` directly after `EmbeddingBehavior` and `RaptorRetrievalBehavior` directly before `RerankingBehavior` — the two positions described under [Pipeline Positioning](#pipeline-positioning) — so the call enables RAPTOR rather than merely registering it. `leafStorePath` is required here because the default `TreeScope` is `Corpus`; see [Tree Scope](#tree-scope) for what that buys you and how to opt out of it.

### Choosing the positions yourself

Earlier versions of this page taught a three-delegate form, because `UseRaptor` used to register both behaviours without placing either and the delegates were the only way to get them into a pipeline. That form still works and still takes precedence — use it when you want RAPTOR somewhere other than its defaults:

```csharp
services.AddRagNet(
    configure: rag => rag.UseRaptor(leafStorePath: "raptor-leaves.db"),
    ingestion: pipeline => pipeline
        .Add<RaptorIngestionBehavior>(after: typeof(EmbeddingBehavior)),
    retrieval: pipeline => pipeline
        .Add<RaptorRetrievalBehavior>(before: typeof(RerankingBehavior))
);
```

`Add` is idempotent and the `ingestion:` and `retrieval:` delegates run before `configure` does, so your placement lands first and `UseRaptor`'s default is skipped. Each behaviour ends up in the chain exactly once, where you put it.

`UseRaptor` throws `InvalidOperationException` if it is called on a `RagBuilder` that did not come from `AddRagNet`, since there is no pipeline to place anything in. It no longer returns quietly having enabled nothing.

## Configuration

### Ingestion Options

```csharp
rag.UseRaptor(options =>
{
    options.Enabled = true;                  // Toggle RAPTOR on/off
    options.MinChunksForRaptor = 5;          // Skip for small documents
    options.ReducedDimensionality = 10;      // UMAP target dims — must be greater than 0
    options.MaxClusters = null;              // null = BIC auto-selects; when set, must be greater than 1
    options.MaxTreeDepth = null;             // null = recurse until 1 cluster; when set, must be greater than 0
    options.StoreLeafChunks = true;          // Keep originals alongside summaries
    options.SummaryChatClient = cheapModel;  // Optional: cheaper model for summaries
    options.SummaryEmbedder = fastEmbedder;  // Optional: separate embedder
    options.TreeScope = RaptorTreeScope.Corpus;  // Corpus (default) or PerDocument — see Tree Scope
    options.CorpusGrowthThreshold = 0.10;    // Corpus scope only: rebuild once the corpus is this much larger than at the last build
});
```

`UseRaptor` validates the configured options at registration and throws `ArgumentException` from the configuring line. The bounds are not pedantry: `MaxClusters = 1` or `MaxTreeDepth = 0` would build no summary levels at all — RAPTOR silently disabled while `Enabled` still reads `true` — and a non-positive `ReducedDimensionality` would leave clustering nothing to work on or crash mid-ingestion.

### Retrieval Options

```csharp
rag.UseRaptor(
    retrieval: options =>
    {
        options.Mode = RaptorRetrievalMode.Boost;
        options.SummaryBoostFactor = 1.5;    // Score multiplier for summaries — must be greater than 0, and finite
        options.MinRaptorLevel = null;       // Level filter lower bound — must not exceed MaxRaptorLevel
        options.MaxRaptorLevel = null;       // Level filter upper bound — when set, must be zero or positive
    }
);
```

These are validated at registration too: `SummaryBoostFactor = 0` would bury every summary and a negative factor would invert their ranking — the opposite of what Boost mode is for — while an empty Filter window (`MinRaptorLevel > MaxRaptorLevel`, or a negative `MaxRaptorLevel`) would remove every result on every retrieval.

## Cost and Performance

### Ingestion Cost

RAPTOR adds LLM calls at ingestion time:

| Document size | Typical clusters | LLM calls (1 level) | LLM calls (2 levels) |
|---------------|-----------------|---------------------|---------------------|
| 5-10 chunks | 2-3 | 2-3 | 3-4 |
| 20-50 chunks | 3-6 | 3-6 | 6-9 |
| 100+ chunks | 5-10 | 5-10 | 10-15 |

**Mitigation strategies:**
- Use a cheaper/faster model via `SummaryChatClient` (e.g. GPT-4o-mini, Haiku)
- Cap tree depth with `MaxTreeDepth = 1` for single-level summaries
- Increase `MinChunksForRaptor` to skip small documents

### Retrieval Cost

RAPTOR adds **zero** latency at retrieval time in Blend mode — summary chunks are just additional vectors in the store. Boost mode adds negligible post-processing. Filter mode may reduce result count.

### Storage

Summary chunks are stored alongside leaf chunks. Typical overhead: 10-30% more vectors depending on document structure and tree depth.

## Pipeline Positioning

```
Ingestion:  Parse → Chunk → Embed → [RAPTOR] → Store
Retrieval:  VectorStore → Ensemble → Filter → [RAPTOR] → Rerank → ...
```

RAPTOR ingestion runs **after** EmbeddingBehavior (needs embeddings) and **before** StorageBehavior (adds summary chunks to the batch).

RAPTOR retrieval runs **before** RerankingBehavior (score adjustments should happen before reranking) and after the vector store returns results.

These are the positions `UseRaptor` places both behaviours at. Pass the `ingestion:` / `retrieval:` delegates only when you want different ones.

## Retrieval Modes in Detail

### Blend (Default)

No score adjustment. Summary chunks compete with leaf chunks purely on vector similarity. This works well because:
- Broad queries naturally match broad summaries
- Specific queries naturally match specific leaf chunks
- The embedding space handles the routing

### Boost

Multiplies scores of chunks where `raptor_level > 0` by `SummaryBoostFactor`:

```csharp
options.Mode = RaptorRetrievalMode.Boost;
options.SummaryBoostFactor = 1.5; // 50% boost for summaries
```

Use when your query workload skews toward overview/theme questions.

### Filter

Restricts results to specific tree levels:

```csharp
// Only summaries (no leaf chunks)
options.Mode = RaptorRetrievalMode.Filter;
options.MinRaptorLevel = 1;

// Only top-level summaries
options.Mode = RaptorRetrievalMode.Filter;
options.MinRaptorLevel = 2;

// Only leaf chunks (disable RAPTOR retrieval effectively)
options.Mode = RaptorRetrievalMode.Filter;
options.MaxRaptorLevel = 0;
```

## Troubleshooting

**RAPTOR is not creating any summary chunks**
- Check that `Enabled = true` (default)
- Under `Corpus` scope (the default): this is expected on most ingests — see [Tree Scope](#tree-scope). Call `RaptorTreeRebuilder.RebuildAsync` to force a build now.
- Under `PerDocument` scope: ensure your document produces at least `MinChunksForRaptor` chunks (default 5)
- Verify `IChatClient` is registered in DI (or `SummaryChatClient` is set)

**Too many/few clusters**
- Set `MaxClusters` to cap the number of clusters per level
- Adjust `ReducedDimensionality` — lower values = coarser clustering

**Summaries are too generic**
- Customize `SummaryPrompt` to be more specific to your domain
- Reduce cluster sizes by increasing the number of clusters

**High ingestion latency**
- Use a cheaper model via `SummaryChatClient`
- Set `MaxTreeDepth = 1` to limit to one summary level
- Increase `MinChunksForRaptor` to skip small documents
