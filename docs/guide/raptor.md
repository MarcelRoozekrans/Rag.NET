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
6. **Recurse** — repeat steps 2-5 on the summaries until a level can no longer be usefully split (or MaxTreeDepth reached). The top level always keeps at least two nodes — a level whose cluster count would not shrink the level below it is rejected rather than collapsed to a single cluster.
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

**`Corpus` requires a leaf store.** Pass `leafStorePath` to `UseRaptor` to register a `SqliteRaptorLeafStore` and enable it — this is what the Quick Start example above does. `UseRaptor` throws `ArgumentException` at registration if `TreeScope` is `Corpus` and no `leafStorePath` is given: there is nowhere to persist leaves between ingests otherwise. `Rag.NET.Raptor.Store` — the assembly `SqliteRaptorLeafStore` lives in — is not something you opt into: `Rag.NET.Raptor` references it unconditionally (`IRaptorLeafStore` appears in `RaptorIngestionBehavior`'s public constructor), so it arrives transitively with `Rag.NET.Raptor` regardless of `TreeScope`. `leafStorePath` decides whether it is *used*, not whether it ships.

**Ingesting one document no longer produces a tree immediately.** Under `Corpus` scope, a single ingest appends that document's leaves to the leaf store and nothing more. A tree is (re)built only once the corpus has grown by `CorpusGrowthThreshold` (default 0.10, i.e. 10%) since the last build — the same debounce shape as `GraphRagOptions.CommunityDetectionGrowthThreshold`, and for the same reason: clustering the whole corpus on every single ingest is expensive and grows worse as the corpus grows. Call `RaptorTreeRebuilder.RebuildAsync` to force a rebuild on demand — after a bulk load, before measuring, or on a schedule; it is registered whenever `leafStorePath` is supplied. Corpus summaries are filed under the reserved id `RaptorCorpusDocumentId.Value` (`raptor://corpus-tree`), never under a real document's id — a corpus-wide summary attributed to whichever document happened to trigger the build would misattribute it to one arbitrary article.

**The debounce baseline is process-local, not persisted.** `RaptorIngestionBehavior` tracks "leaves at last build" in memory. It does not read the leaf store's actual growth since some earlier process's last build — every process starts with no baseline, so the first ingest after any restart always triggers a build regardless of `CorpusGrowthThreshold`, at a full LLM spend proportional to however large the corpus already is. A process that restarts often (redeploys, a serverless host that recycles instances, a CLI invoked once per document) pays that cost far more often than the threshold alone would suggest.

### When to choose `PerDocument`

- You need isolated per-document trees on purpose — for example multi-tenant document sets, where a cross-document summary would leak content between tenants.
- You are differencing against `Corpus` scope, the way Phase 6.2.1 does.

Not a reason: avoiding the `Rag.NET.Raptor.Store` dependency. `Rag.NET.Raptor.csproj` references it unconditionally — `IRaptorLeafStore` appears in `RaptorIngestionBehavior`'s public constructor — so the assembly (and its `Microsoft.Data.Sqlite` dependency) arrives regardless of `TreeScope`. What `PerDocument` avoids is *using* a leaf store, not shipping one.

Set it explicitly — an explicit value is clearer than code that depends silently on whichever way the default happens to point:

```csharp
services.AddRagNet(rag => rag.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument));
```

## Migration from the pre-v1.0 default

Before v1.0, `TreeScope` defaulted to `PerDocument`. Upgrading without changing anything now throws: `UseRaptor()` — or any call that does not set `TreeScope` explicitly — hits the `Corpus`-requires-`leafStorePath` check above and fails at registration with `ArgumentException`. Fix that first, one of two ways:

- Pass `leafStorePath` to opt into the new `Corpus` default (no separate package install needed — `Rag.NET.Raptor.Store` already arrives transitively with `Rag.NET.Raptor`; see [Tree Scope](#tree-scope)), or
- Set `o.TreeScope = RaptorTreeScope.PerDocument` explicitly to keep the previous behaviour unchanged.

If you do move to `Corpus` scope, the summary chunks a previous `PerDocument` ingest already wrote are now stale: they are filed per document rather than under the corpus id, they overlap with nothing the corpus tree produces, and at retrieval time they compete for rank against real corpus summaries on an equal footing. **There is no automatic cleanup**, and deliberately so: old summary chunks carry a real `raptor_level` and a real `DocumentId`, so a heuristic guessing which chunks were RAPTOR's from those fields alone would occasionally guess wrong on someone else's data and delete it — worse than leaving stale summaries in place. The migration is manual:

1. `IVectorStore` has no enumeration and no metadata-predicate delete, so "delete every chunk carrying `raptor_level`" is not an operation the API supports. Instead, call `DeleteByDocumentIdAsync` (or your ingestor's document-level delete) for every document that previously produced `PerDocument` summaries — this removes that document's stale summary chunks and its leaf chunks together, since both were filed under the same document id. Note that a shorter re-ingest of the same document strands any tail leaves the earlier, longer version produced (a general limitation of the leaf store's upsert-by-index behaviour, not specific to this migration).
2. Re-ingest your documents so their leaves land in the leaf store, or — if the leaves are already there — call `RaptorTreeRebuilder.RebuildAsync` once to build the corpus tree fresh.

## Quick Start

```csharp
// Install: dotnet add package Rag.NET.Raptor
// Rag.NET.Raptor.Store — where SqliteRaptorLeafStore lives — arrives transitively; no separate
// install needed to reference its types.

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
rag.UseRaptor(
    options =>
    {
        options.Enabled = true;                  // Toggle RAPTOR on/off
        options.MinChunksForRaptor = 5;          // Skip for small documents
        options.ReducedDimensionality = 10;      // UMAP target dims — must be greater than 0
        options.MaxClusters = null;              // null = BIC auto-selects; when set, must be greater than 1
        options.MaxTreeDepth = null;             // null = recurse until a level can no longer be usefully split; when set, must be greater than 0
        options.StoreLeafChunks = true;          // Keep originals alongside summaries — must stay true under Corpus scope
        options.SummaryChatClient = cheapModel;  // Optional: cheaper model for summaries
        options.SummaryEmbedder = fastEmbedder;  // Optional: separate embedder
        options.TreeScope = RaptorTreeScope.Corpus;  // Corpus (default) or PerDocument — see Tree Scope
        options.CorpusGrowthThreshold = 0.10;    // Corpus scope only: rebuild once the corpus is this much larger than at the last build
    },
    leafStorePath: "raptor-leaves.db");          // Required under Corpus scope — see Tree Scope
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
    },
    leafStorePath: "raptor-leaves.db");      // Required under the default Corpus scope
```

Retrieval options are independent of tree scope, but `leafStorePath` is still required here because
`TreeScope` defaults to `Corpus`. Pass `options => options.TreeScope = RaptorTreeScope.PerDocument`
instead if you do not want a leaf store.

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

`Boost` and `Filter` **over-fetch before they apply**, controlled by `CandidateMultiplier`
(default `3.0`, a multiple of the query's `TopK`):

```csharp
options.CandidateMultiplier = 3.0;  // fetch 3x TopK, then boost, then take TopK
```

Without it neither mode could do what it says. The behaviour used to receive the already-truncated
top-k, so `Boost` could reorder summaries *within* that set but never promote one *into* it however
large the boost, and `Filter` returned fewer results than you asked for. `Blend` never over-fetches
— it is the default and returns exactly `TopK`.

**Setting `CandidateMultiplier = 1.0` reproduces the pre-over-fetch behaviour exactly**, at any
`TopK`. That exists so the old behaviour stays measurable as a control rather than being kept alive
as a defect; you would not normally set it.

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

## Known Limitations

These apply under `Corpus` scope. Both are open issues, not this guide's suggestion for how to
work around them — there is currently no workaround short of the fixes tracked in the issues
below.

### Deleting a document does not delete its RAPTOR leaves (#338)

`PipelineIngestor.DeleteAsync` clears the vector store, BM25 index, parent store, data manager and
version store for a document — but it never calls `IRaptorLeafStore.RemoveDocumentAsync`. That
method exists on the interface; nothing in the product calls it.

Concretely: ingest a document under `Corpus` scope, then delete it. It disappears from search
immediately, as expected. But its chunks are still sitting in the leaf store, and the next corpus
build (debounced or forced via `RaptorTreeRebuilder.RebuildAsync`) reads that leaf text back out,
sends it to the LLM, and stores a fresh summary under `raptor://corpus-tree`. The deleted document's
content becomes searchable again — through a summary chunk with no document id to trace it back to,
and no delete operation that removes it, because the summary is filed under the corpus id, not the
document's.

A second, related gap: `OverwriteBehavior` deletes a document's vector-store entries before
re-ingesting it, but the leaf store only *upserts* leaves by `(document_id, chunk_index)`. If the
new version of a document is shorter than the old one, the old version's tail leaves (indices past
the new chunk count) are never overwritten and never deleted — they strand in the leaf store and
keep contributing to future corpus builds.

Neither of these can be fixed by adding a call to `RemoveDocumentAsync` from core: core cannot
reference `Rag.NET.Raptor.Store` (the dependency direction runs the other way), so a real fix needs
a new abstraction core can depend on. That is out of scope for this phase; #338 tracks it.

### #336 is on by default now that `Corpus` is the default scope

Corpus summaries are filed under the single reserved id `raptor://corpus-tree`.
`StorageBehavior`'s `RemovePreviousAppendOnlyEntries` only removes BM25 postings for
`ctx.Metadata.DocumentId` — the *ingesting* document's id, never the corpus id a summary is
actually filed under — so every ingest-triggered corpus build appends a full extra copy of the
tree's BM25 postings rather than replacing the previous copy. At the default
`CorpusGrowthThreshold = 0.10`, a corpus growing from 100 to 10,000 leaves triggers roughly 48
builds — up to 48 duplicate copies of the tree's postings, each inflating IDF for every term the
summaries contain.

Two more facets of the same root cause:

- **Vector-store orphans accumulate on the ingest path.** Clustering is not stable across runs, so
  a later build can produce fewer summaries than an earlier one. `RaptorTreeRebuilder.RebuildAsync`
  deletes the previous corpus tree before storing the new one for exactly this reason (see its own
  remarks) — but the ingest-triggered build path does not delete first, so any surplus from a
  shrinking tree survives as orphaned chunks that retrieval can still return.
- **`RaptorTreeRebuilder.RebuildAsync` bypasses BM25 entirely.** It writes the rebuilt tree through
  `IVectorStore` directly, with no corresponding BM25 update. A rebuilt tree is therefore invisible
  to keyword and hybrid search, while the stale, duplicated copies the ingest path wrote to BM25
  remain searchable. Earlier in this guide, `RebuildAsync` is offered as the way to force a tree
  current — that remedy carries this caveat: it fixes the vector store's copy of the tree and not
  BM25's.

Not fixed in this phase; #336 tracks it.

### `Corpus` scope cannot build a tree over a corpus of more than roughly a thousand chunks, at the shipped default (#345)

This is user-facing on the **default configuration** — nobody has to opt into anything to hit it.

`MaxClusters` defaults to `null`, so `SelectClusterCount` falls back to
`GaussianMixtureModel.SelectK(reduced, maxK: Min(count, 10))` — every level is capped at **10**
clusters, regardless of how large the corpus is. `SummarizeClusterAsync` then hands the whole
cluster's concatenated text to the LLM in one call: `ConcatenateChunkTexts` joins every child
chunk's text with no length bound at all.

Put a number on it. Chunks are at most 512 **characters** (`ChunkingOptions.MaxChunkSize`), so a
typical chunk is roughly 407 characters — about 100 tokens. A corpus of 17,648 chunks (MultiHop-RAG's
609 articles under the real chunker) split into 10 level-1 clusters puts at least
⌈17,648 / 10⌉ = **1,765 chunks** in the largest one — **≈730,000 characters, ≈183,000 tokens** — in a
single summarisation prompt. Against `openai/gpt-4o-mini`'s 128k-token context window, the floor
case (the *smallest* the largest cluster can be, evenly split) already exceeds the limit by roughly
1.4×. This is not a tail risk: it is the arithmetic of the default cap against any corpus above
roughly a thousand chunks, and it gets worse as the corpus grows because the cap does not.

There is currently no workaround that does not change what is being measured: a smaller corpus, a
model with a longer context window, or setting `MaxClusters` **lower still** all sidestep the
number without fixing the mechanism, and the last one makes it worse (see the corrected advice
under [Troubleshooting](#troubleshooting) below). Not fixed in this phase; #345 tracks it.

## Troubleshooting

**RAPTOR is not creating any summary chunks**
- Check that `Enabled = true` (default)
- Under `Corpus` scope (the default): this is expected on most ingests — see [Tree Scope](#tree-scope). Call `RaptorTreeRebuilder.RebuildAsync` to force a build now.
- Under `PerDocument` scope: ensure your document produces at least `MinChunksForRaptor` chunks (default 5)
- Verify `IChatClient` is registered in DI (or `SummaryChatClient` is set)

**Too many/few clusters**
- `MaxClusters` caps how many clusters a level may split *into* — it does not cap how large any one
  cluster is. A *lower* `MaxClusters` forces the same chunks into **fewer, larger** clusters, not
  smaller ones; if clusters already feel too big, lowering it makes that worse, not better. See
  [#345 above](#corpus-scope-cannot-build-a-tree-over-a-corpus-of-more-than-roughly-a-thousand-chunks-at-the-shipped-default-345)
  for the case — the shipped default — where this is not a tuning nuisance but a hard failure.
- Adjust `ReducedDimensionality` — lower values = coarser clustering

**Summaries are too generic**
- Customize `SummaryPrompt` to be more specific to your domain
- Reduce cluster sizes by increasing the number of clusters

**High ingestion latency**
- Use a cheaper model via `SummaryChatClient`
- Set `MaxTreeDepth = 1` to limit to one summary level
- Increase `MinChunksForRaptor` to skip small documents
