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
3. **GMM clustering** — soft-cluster chunks using Gaussian Mixture Models; BIC selects the cluster count, floored by `TargetClusterSize` so a level's average cluster stays within budget regardless of corpus size — see [Cluster Size](#cluster-size)
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

## Measured

RAPTOR has been run for real, and this section says what it bought. Measured **2026-08-25** on the
full **609-article MultiHop-RAG corpus** (17,648 leaf chunks), `openai/gpt-4o-mini` at temperature
0, top-6 context, four arms over 2,556 queries and 10,224 scored answers. Accuracy is over the
**2,255 judged queries** — the 301 unanswerable nulls are scored separately as abstention. Both
trees were already built and cached, so the run paid for answers only.

| Arm | What it is | Paper rule | Raw | Strict | Inference |
|---|---|---|---|---|---|
| `raptor` | per-document tree (the control) | **0.3734** | **0.2860** | **0.3348** | **0.8309** |
| `raptorcorpus` | corpus-wide tree — **the shipped default** | 0.3588 | 0.2656 | 0.3322 | 0.7831 |
| `raptorfiltered` | summaries filtered out — leaves only | 0.3499 | 0.2603 | 0.3242 | 0.7721 |
| `raptorboost` | corpus tree, `Boost` mode | 0.3450 | 0.2634 | 0.3086 | 0.7757 |

**The run validated itself before it reported anything.** `raptorfiltered` reproduces the dense
arm's separately pinned figures to four decimals on all three rules — 0.3499 / 0.2603 / 0.3242 —
which is the evidence that the RAPTOR corpus and the dense corpus are the same corpus. Without that
gate holding, none of the differences below would mean anything.

**Summaries help a little.** `raptorcorpus − raptorfiltered = +0.0089` on the paper rule (McNemar
p=0.0293), +0.0053 raw (p=0.1416), +0.0080 strict (p=0.0795) — significant on one rule of three,
and small on all three. That is what the tree adds over the leaf chunks alone.

**Corpus scope — the default — measured worse than the per-document tree it replaced.**
`raptorcorpus − raptor = −0.0146` paper (McNemar p=0.0247, 85 corpus wins against 118
per-document), **−0.0204 raw** (p=0.0006), −0.0027 strict (p=0.7372, a wash). Two of three rules
significant, all three signed the same way. **The gap is entirely inference queries** — 0.7831
against the control's 0.8309, while comparison and temporal are flat — which is the opposite of the
argument for making `Corpus` the default: corpus-spanning summaries were meant to help exactly the
multi-hop case they measurably hurt here.

**`Boost` trades accuracy for abstention.** `raptorboost − raptorcorpus = −0.0137` paper
(p=0.0073), −0.0235 strict (p=0.0000) — while abstaining correctly on **51.8%** of the 301
unanswerable nulls, the best of the four arms. If you would rather the model decline than guess,
that trade is available and it is a real one; it is not free.

### What this means for your choice of scope

**The default stays `Corpus`, and that is a hold rather than an endorsement** (decided 2026-08-27).
One dataset reversing a shipped default is thin evidence, and **MultiHop-RAG is not a neutral
referee here**: its questions are built by composing facts drawn from identifiable source articles,
so a per-document tree is being measured on home ground. Two of three rules signing against the
default is a real result *on this corpus*, and reverting a breaking default on the least neutral
evidence available would be the wrong move. A second corpus, with questions not constructed per
document, is what settles it.

So, concretely:

- **If your corpus resembles MultiHop-RAG** — questions answered by composing facts that each live
  in one identifiable document — the measurement here says `PerDocument` is the better arm, and it
  is one line to set (see [When to choose `PerDocument`](#when-to-choose-perdocument)).
- **If your documents genuinely share themes across the corpus**, `Corpus` is the mechanism the
  paper describes and the case this measurement cannot speak to.
- **Either way, measure it on your own corpus** rather than inheriting this number. The four arms
  above are what that looks like.

The figures are pinned and machine-asserted in `MultiHopRagAnswerReproduction` (arms `raptor`,
`raptorcorpus`, `raptorfiltered`, `raptorboost` under `multihop-rag`), so a regression fails a test
rather than going unnoticed; each pin carries the full reading in its own note. The protocol is
`docs/plans/2026-08-21-raptor-real-protocol-implementation.md`, and the pilot that preceded it —
which put `raptorcorpus − raptor` at +0.0000 on 50 queries and was simply underpowered — is in
`docs/plans/2026-08-21-raptor-pilot-notes.md`.

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
        options.MaxClusters = null;              // null = BIC auto-selects; when set, must be greater than 1 — yields to TargetClusterSize if honouring it would exceed the target
        options.TargetClusterSize = 100;         // Floor on cluster count — bounds the average cluster size, not each cluster's max; must be greater than 1
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
| 100+ chunks | about `ceil(count / TargetClusterSize)` | about `ceil(count / TargetClusterSize)` | plus `ceil(level-1 count / TargetClusterSize)` |

The last row used to read "5-10 clusters, 5-10 LLM calls" — that was the old `BicMaxK = 10` cap
this package removed (#345), not a bound that still holds. Past `TargetClusterSize` (100 chunks by
default) the cluster count grows with the level rather than capping at 10.

**A second level costs far less than the first, not double it**, because it clusters the *summaries*
the first level produced, not the chunks. Worked through at the default target of 100 over a
17,648-chunk corpus:

| Level | Input | Calls |
|---|---|---|
| 1 | 17,648 chunks | 177 |
| 2 | 177 summaries | 2 |
| **Total** | | **179** |

So the old ~10-40 total calls become ~179 — an order of magnitude more, and worth budgeting for, but
the growth is in level 1 alone. Size your LLM budget from `TargetClusterSize`, not from this table's
earlier rows.

*"About"* rather than *"at least"* is deliberate: past `BicMaxK` the cluster count is set to
`ceil(count / TargetClusterSize)` exactly, and an empty GMM component can leave one fewer cluster
than that. See [Cluster Size](#cluster-size) for what the floor does and does not guarantee.

**Mitigation strategies:**
- **Raise `TargetClusterSize`** — it is the primary cost lever past the default cluster count: doubling it roughly halves the number of LLM calls a large level makes, at the cost of a larger average cluster per summary
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

## Cluster Size

`RaptorOptions.TargetClusterSize` is a floor on how many clusters a level splits into, so that a
summarisation prompt does not grow unboundedly with the corpus. Default: 100 chunks.

**What it guarantees, precisely — a floor on the count, not a cap on the size.**
`SelectClusterCount` computes `ceil(count / TargetClusterSize)` and never chooses a cluster count
`k` below it, which guarantees at least that many components are fitted and therefore an *average*
cluster size at or under the target. It does not guarantee every individual cluster is: GMM
assignment is free to put a disproportionate share of a level's chunks into one component and
spread the rest thinly, and nothing here stops that — an individual cluster may still exceed the
target when assignment is unbalanced. It also does not guarantee the delivered cluster *count*
matches the floor exactly: a component no point was assigned to vanishes silently, so the delivered
count can come in below the floor. A hard per-cluster bound would need clusters split after
assignment, which this deliberately does not do. Whether that is needed in practice is a question
for measurement on real corpora, not an assumption to make ahead of the data.

**How the average-versus-maximum gap is observed in practice.** `raptor.cluster.count` alone cannot
tell an even split from one lopsided cluster absorbing most of the level — both produce the same
count. The `ragnet.raptor.summarize` span also carries `raptor.cluster.max.size`, the largest
delivered cluster's chunk count, specifically so that gap is visible rather than assumed: if it
tracks close to `count / raptor.cluster.count` across real corpora, the floor is sufficient on its
own; if it runs far above that, that is evidence a hard per-cluster split is needed. See
[OpenTelemetry Integration](../reference/opentelemetry.md#satellite-spans).

**What that measurement found — the gap is real, and the floor still holds.** Measured 2026-08-23
over MultiHop-RAG's 17,648 chunks at the default `TargetClusterSize = 100`, the first corpus-scale
RAPTOR tree this package has built:

| level | chunks in | clusters out | mean | largest | imbalance |
|---|---|---|---|---|---|
| 1 | 17,648 | 177 | 99.7 | **549** | **5.51x** |
| 2 | 177 | 4 | 44.2 | 61 | 1.38x |
| 3 | 4 | 2 | 2.0 | 2 | 1.00x |

The largest level-1 cluster holds **5.5x the mean**, so the average bound is emphatically not a
maximum — real corpora do not cluster evenly. It nonetheless fits: 549 chunks is roughly 227,000
characters, about **57,000 tokens against a 128,000-token context**, leaving 2.25x headroom. **No
post-assignment split is needed at the default**, which is why one is still not implemented.

**Read this before raising `TargetClusterSize`.** The overflow point at this corpus's chunk size is
a largest cluster of roughly 1,259 chunks, so the measured 5.51x consumes about **44% of the
available imbalance budget** — not the ~12.6x a reader would infer from "the average is 100
against a 128k context". Budget for the largest cluster at several times the target, not for the
target itself. The figure is one corpus at one chunk size; a corpus that clusters more lopsidedly,
or larger chunks, moves it.

**Why it exists.** Before it, `k` was capped at 10 per level regardless of the level's size, and
the joined cluster text had no bound at all. On a 17,648-chunk corpus the smallest possible largest
cluster was 1,765 chunks — about 730,000 characters, roughly 183,000 tokens against a 128,000-token
context. The tree could not be built at any `k` the cap allowed (#345). The floor materially
reduces the expected maximum — on a balanced split, from ~1,765 chunks down to ~100 at this
option's default — even though it cannot guarantee it.

**It counts chunks, not tokens.** At the stock `ChunkingOptions.MaxChunkSize` of 512 characters,
100 chunks is at most ~51,000 characters — comfortably inside a 128,000-token context, assuming a
roughly balanced split. A larger chunk size, a model with a smaller context, or evidence of
unbalanced clustering all want a smaller target.

**Below the target, nothing changes.** `SelectClusterCount`'s floor is 1 below
`TargetClusterSize`'s threshold; BIC picks `k` exactly as it did before this option existed.

```csharp
options.TargetClusterSize = 100; // Floor on cluster count — must be greater than 1
```

## Known Limitations

These apply under `Corpus` scope. **Most of what this section once listed is now fixed.** The
entries are kept rather than deleted, each with what its fix could *not* do, because a store built
before a fix can still carry the consequences. One facet remains genuinely open and is marked so.

### Deletion reaches the leaves (#338, fixed)

**Resolved in Phase 6.2.14.** `IRaptorLeafStore` extends `IDocumentScopedStore`
(`Rag.NET.Abstractions`), and both `PipelineIngestor.DeleteAsync` and
`StorageBehavior` clear every registered document-scoped store — so deleting a document removes its
leaves, and re-ingesting a shorter one strands none. The purge on re-ingest is unconditional rather
than gated on `Overwrite`, which places leaves alongside the BM25 index rather than alongside the
vector store's deliberately-stranded tail.

**One thing the fix cannot do: clean up retroactively.** Summaries already written under
`raptor://corpus-tree` from documents deleted *before* this landed carry no document id, so nothing
can identify which of them came from deleted material. A store built before the fix needs its tree
rebuilt — `RaptorTreeRebuilder.RebuildAsync` after the deleted documents are gone from the leaf
store — for those summaries to disappear.

### The corpus tree and its two stores (#336 and #487 fixed; one facet open)

Corpus summaries are filed under the single reserved id `raptor://corpus-tree`, which is not the id
of any document being ingested. Three consequences followed from that; **two are fixed and one is
not.**

**Fixed in Phase 6.2.15 — BM25 postings no longer accumulate.**
`StorageBehavior` used to purge previous append-only entries for `ctx.Metadata.DocumentId` only, so
every ingest-triggered build appended a full extra copy of the tree's postings instead of replacing
the previous one. At the default `CorpusGrowthThreshold = 0.10` a corpus growing from 100 to 10,000
leaves triggers roughly 48 builds — up to 48 duplicate copies, each inflating IDF for every term the
summaries contain. `IngestionContext.AdditionalAppendOnlyPurgeIds` now lets the RAPTOR behaviour
name the corpus id, and it does so only when a build actually produced a tree.

**Still open — vector-store orphans on the ingest path.** Clustering is not stable across runs, so a
later build can produce fewer summaries than an earlier one. `RaptorTreeRebuilder.RebuildAsync`
deletes the previous corpus tree before storing the new one for exactly this reason; the
ingest-triggered path does not, so surplus summaries from a shrinking tree survive as orphans that
retrieval can still return. The 6.2.15 fix deliberately did **not** touch the vector store — it
upserts on `(DocumentId, ChunkIndex)` and was never the half that accumulated without bound — so
this facet is unchanged.

**Fixed in Phase 6.2.17 — `RebuildAsync` now writes BM25 ([#487]).** It used to write the rebuilt
tree through `IVectorStore` directly with no corresponding BM25 update, so after a rebuild the two
stores disagreed: the vector store held the new tree, BM25 held whatever the ingest path last wrote.
Earlier in this guide `RebuildAsync` is offered as the way to force a tree current, and **that
remedy no longer carries this caveat** — it now makes both copies current, removing and re-adding
the corpus id so a shrinking tree leaves no surplus postings behind.

**What blocked it was one level down, and worse than this entry.** Fixing it meant deciding where
BM25 doc ids come from when no ingest is in progress — and the answer turned out to be that the
caller should never have supplied them. The allocator lived in `PipelineIngestor`, counting from 0
each process, while a persisted index reloads the ids it wrote; after a restart it handed out ids
the index already held, and `Add` silently dropped the chunk on collision ([#490]). At shipped
defaults with `UseSqlitePersistence`, **every document ingested after a restart was missing from
keyword and hybrid search**. `IBm25Index.Add` now takes a chunk and returns the id it assigned, so
there is no id for a caller to get wrong.

[#487]: https://github.com/MarcelRoozekrans/Rag.NET/issues/487
[#490]: https://github.com/MarcelRoozekrans/Rag.NET/issues/490

