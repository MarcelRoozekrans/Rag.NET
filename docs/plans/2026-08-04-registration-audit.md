# Registration audit — default-path reachability of the four extraction clusters

**Date:** 2026-08-04
**Branch:** `feature/package-decomposition`
**Gates:** the core extraction tasks of the package-decomposition phase. A cluster may only
be extracted if doing so cannot change what `AddRagNet()` composes by default — or, where it
does, the change is recorded here first.

**Method:** read the composition code, then measured it. A throwaway console app (scratchpad,
not committed) called `AddRagNet()` with no configure delegate and no opt-in methods —
registering only what every consumer registers (an `IVectorStore`, an `IEmbeddingGenerator`) —
resolved both default pipelines, executed a retrieval, and reported per-cluster state and
loaded assemblies. Probe output is quoted per cluster below.

The default path is defined by:

- `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs:20` — `AddRagNet()`
- `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs:12-30` — default retrieval `_types` (16 behaviors)
- `src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs:11-24` — default ingestion `_types` (11 behaviors)

Both builders consume `_types` identically — `RetrievalPipelineBuilder.cs:75`:

```csharp
var behavior = (IRetrievalBehavior)sp.GetRequiredService(_types[i]);
```

Every listed type is unconditionally resolved and instantiated when the pipeline singleton is
first requested. Nothing is filtered out; nothing fails resolution (measured — see caching).

---

## 1. Caching — `Microsoft.Extensions.Caching.Hybrid` (7 transitive packages)

**Reachable by default? YES — both behaviors are instantiated in every default pipeline, and
no-op.** The answer to the decisive question is **(a)**, with a measured refinement that
changes Task 5's shape (below).

### Evidence

In the default list — `RetrievalPipelineBuilder.cs:15` and `:26`:

```csharp
typeof(ResultCacheBehavior),      // index 1 of 16
...
typeof(EmbeddingCacheBehavior),   // index 12 of 16
```

Both behaviors declare their cache dependency optional — `ResultCacheBehavior.cs:13-14`
(identical in `EmbeddingCacheBehavior.cs:13-14`):

```csharp
[Inject(Required = false)] public HybridCache? Cache { get; set; }
[Inject(Required = false)] public CachingOptions? CachingOptions { get; set; }
```

The ZeroAlloc-generated registration honours `Required = false` with `GetService`, not
`GetRequiredService` — `obj/generated/.../ZeroAlloc.Inject.ServiceCollectionExtensions.g.cs:331-334`:

```csharp
var instance = new global::Rag.NET.Retrieval.Behaviors.ResultCacheBehavior();
instance.Cache = sp.GetService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
instance.CachingOptions = sp.GetService<global::Rag.NET.Models.Options.CachingOptions>();
```

So resolution succeeds with `Cache = null`, and at run time the behavior forwards immediately —
`ResultCacheBehavior.cs:20-21` (mirrored at `EmbeddingCacheBehavior.cs:22-25`):

```csharp
if (!ctx.Options.UseCacheResult || Cache is null || CachingOptions is null)
    return await next(ctx, ct).ConfigureAwait(false);
```

`HybridCache` is only ever registered by the opt-in — `RagBuilder.cs:171-178` (`UseCaching`):

```csharp
Services.AddSingleton(options);
Services.AddHybridCache();
```

### Measured (probe)

```
ResultCacheBehavior instantiated: True; Cache is null: True; CachingOptions is null: True
EmbeddingCacheBehavior instantiated: True; Cache is null: True; CachingOptions is null: True
HybridCache registered in container: False
retrieval executed on default path, results: 0
```

### The refinement the probe found: the behaviors do not depend on the Hybrid package's assembly

After full default-path construction plus one retrieval execution:

```
assembly loaded: Microsoft.Extensions.Caching.Hybrid = False
loaded assemblies matching cluster keywords:
  Microsoft.Extensions.Caching.Abstractions
typeof(HybridCache).Assembly: Microsoft.Extensions.Caching.Abstractions
```

The `HybridCache` *type* lives in `Microsoft.Extensions.Caching.Abstractions`. The
`Microsoft.Extensions.Caching.Hybrid` *package* (the 7-package cluster) is needed only for the
`AddHybridCache()` implementation call inside `UseCaching()`. Today Caching.Abstractions
reaches core's closure solely through the Hybrid package (`dotnet nuget why`):

```
[net10.0]
└── Microsoft.Extensions.Caching.Hybrid (v10.8.0)
    ├── Microsoft.Extensions.Caching.Abstractions (v10.0.10)
    └── Microsoft.Extensions.Caching.Memory (v10.0.10)
        └── Microsoft.Extensions.Caching.Abstractions (v10.0.10)
```

### Verdict — extract, without touching the default pipeline

Answer **(a)** — the behaviors are instantiated and no-op. But extraction does **not** require
removing them from `_types`, because their only compile-time need is the abstract `HybridCache`
type. Task 5 must therefore:

1. Keep `ResultCacheBehavior`, `EmbeddingCacheBehavior`, `CacheKeyGenerator`, and
   `CachingOptions` in core. Replace core's `Microsoft.Extensions.Caching.Hybrid` package
   reference with a direct `Microsoft.Extensions.Caching.Abstractions` reference (it is not
   otherwise in the closure — see the `nuget why` output above; omitting it breaks the build).
2. Move only `UseCaching()` (the `AddHybridCache()` call, `RagBuilder.cs:171-178`) to the
   satellite package.
3. As a consequence the default `_types` list is untouched: pipeline composition, behavior
   indices, and `Add(after:/before: typeof(ResultCacheBehavior))` anchoring all keep working.
   The alternative (removing the behaviors from `_types`) would silently break any consumer
   using them as insertion anchors — `RetrievalPipelineBuilder.Add` falls back to *append* when
   the anchor type is absent (`RetrievalPipelineBuilder.cs:35-41`), a silent reordering. Do not
   take that route.
4. A consumer who calls `UseCaching()` from the satellite gets identical behavior to today:
   the same two in-pipeline behaviors light up when `HybridCache` + `CachingOptions` appear in
   the container.

Net saving: the Hybrid package and its 6 transitive dependencies leave the default closure;
Caching.Abstractions (plus its small dependency set) stays.

---

## 2. SQLite — `Microsoft.Data.Sqlite` + `SQLitePCLRaw` (6 transitive packages)

**Reachable by default? NO.** Confirmed clean, with two gates the going-in table did not list.

### Evidence

No `Sqlite*` type appears in either default `_types` list
(`RetrievalPipelineBuilder.cs:12-30`, `IngestionPipelineBuilder.cs:11-24`), and no `Sqlite*`
type carries `[Singleton]` (verified by grep over `src/Rag.NET`: the attribute appears only on
pipeline behaviors, parsers, chunking strategy, `PipelineIngestor`, `PipelineRetriever`), so
the generated `AddRagNETServices()` never registers one.

Every construction site of a `Sqlite*` type in core sits behind an opt-in:

- `RagBuilder.cs:245` — `UseSqlitePersistence()`:
  ```csharp
  Services.AddSingleton<SqliteBm25Index>(sp => new SqliteBm25Index(dbPath, collectionName, sp.GetService<SynonymMap>()));
  ```
  and `RagBuilder.cs:248` — `new SqliteParentChunkStore(dbPath, collectionName)`.
- `RagBuilder.cs:274` — `UseContentHashRecordManager()`: `new SqliteContentHashStore(dbPath)`.
- `RagBuilderExtensions.cs:132` — `UseEmbeddingVersioning()`: `new SqliteEmbeddingVersionStore(...)`. **Not in the going-in table.**
- `RagBuilderExtensions.cs:355` — `UseCostBudgeting()`: `new SqliteCostLedger(options.DatabasePath, ...)`. **Not in the going-in table.**
- `SqliteDocumentStore` is never registered by any builder method — user-constructed only.

The default fallback is explicitly in-memory — `ServiceCollectionExtensions.cs:67`:

```csharp
services.TryAddSingleton<IBm25Index>(sp => sp.GetRequiredService<InMemoryBm25Index>());
```

### Measured (probe)

```
assembly loaded: Microsoft.Data.Sqlite = False
assembly loaded: SQLitePCLRaw.core = False
```

### Verdict — extract

Not reachable by default; extraction is invisible on the default path. The extraction task must
move **all five** gates (`UseSqlitePersistence`, `UseContentHashRecordManager`,
`UseEmbeddingVersioning`, `UseCostBudgeting`'s default ledger, and `SqliteDocumentStore`) with
the seven `Sqlite*` files in `src/Rag.NET/Storage/`, not just the two the going-in table named —
otherwise `Microsoft.Data.Sqlite` stays referenced and the extraction saves nothing. Note the
`UseCostBudgeting` entanglement: it is a resilience-cluster method whose default ledger is
SQLite (see cluster 3).

---

## 3. Resilience — `Microsoft.Extensions.Resilience` + Polly (15 transitive packages)

**Reachable by default? NO.** Confirmed clean.

### Evidence

No `Resilient*`, `RateLimited*`, `CostTracking*`, or `FallbackChatClient` type appears in
either default `_types` list, and none carries `[Singleton]` (same grep as above). Every
construction site sits behind an opt-in:

- `RagBuilder.cs:331` — `ConfigureResilience()` is the only caller of
  `Services.AddResiliencePipeline(...)` (`RagBuilder.cs:353`) and the only registrar of
  `ResilientEmbeddingGenerator` / `ResilientVectorStore` (`RagBuilder.cs:362-372`). Its doc
  states the contract outright — `RagBuilder.cs:296`:
  ```
  Not calling this method leaves the container graph completely undecorated.
  ```
- `RagBuilderExtensions.cs:197` — `UseFallbackChain()`: `new FallbackChatClient(chain, ...)`.
- `RagBuilderExtensions.cs:270-280` — `UseRateLimiting()`: `RateLimitedChatClient` /
  `RateLimitedEmbeddingGenerator`.
- `RagBuilderExtensions.cs:359-368` — `UseCostBudgeting()`: `CostTrackingChatClient` /
  `CostTrackingEmbeddingGenerator`.

### Measured (probe)

```
assembly loaded: Microsoft.Extensions.Resilience = False
assembly loaded: Polly.Core = False
```

### Verdict — extract

Not reachable by default; extraction is invisible on the default path. One boundary note:
`System.Threading.RateLimiting` is referenced explicitly by core
(`Rag.NET.csproj:32`, with a comment that it is *not* in the shared framework and only
otherwise reachable via Polly) for the token-bucket limiter — if `UseRateLimiting` moves to the
satellite, that reference moves with it. Cross-cluster: `CostTracking*` also depends on the
tokenizer cluster (via `CostAccounting`) and `UseCostBudgeting` on the SQLite cluster (via
`SqliteCostLedger`) — the cost-budgeting surface must land in a package whose dependency set
covers all three, or be its own satellite.

---

## 4. Tokenizer — `Microsoft.ML.Tokenizers` + `Data.Cl100kBase` (3 transitive packages)

**Reachable by default? NO — but extraction saves nothing. Do not extract.**

### Evidence (not reachable by default)

Only two core types touch the tokenizer:

- `Resilience/CostAccounting.cs:21`:
  ```csharp
  private static readonly Tokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
  ```
  `CostAccounting` is `internal static` and its only callers are `CostTrackingChatClient` and
  `CostTrackingEmbeddingGenerator` (grep-verified) — both behind `UseCostBudgeting()`. The
  static field initialises on first class touch, so the vocabulary never loads by default.
- `Memory/ConversationMemoryPipeline.cs:33`:
  ```csharp
  _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
  ```
  Constructed only inside `UseConversationMemory()` (`RagBuilder.cs:435` and `:445`).

Probe: `IConversationMemory registered: False`, `assembly loaded: Microsoft.ML.Tokenizers = False`.

### Why extraction still saves nothing

`dotnet nuget why src/Rag.NET/Rag.NET.csproj Microsoft.ML.Tokenizers`:

```
[net10.0]
├── Microsoft.ML.Tokenizers (v0.22.0)
├── Microsoft.ML.Tokenizers.Data.Cl100kBase (v0.22.0)
│   └── Microsoft.ML.Tokenizers (v0.22.0)
└── Rag.NET.QueryTechniques (v1.0.0)
    ├── Microsoft.ML.Tokenizers (v0.22.0)
    └── Microsoft.ML.Tokenizers.Data.Cl100kBase (v0.22.0)
        └── Microsoft.ML.Tokenizers (v0.22.0)
```

Core hard-references `Rag.NET.QueryTechniques` (`Rag.NET.csproj:50`), and QueryTechniques
independently pulls both `Microsoft.ML.Tokenizers` and `Data.Cl100kBase`. Removing core's own
two package references (`Rag.NET.csproj:28` and `:33`) leaves the consumer's transitive closure
byte-for-byte identical — the same two packages arrive through the QueryTechniques edge.

### Verdict — stays in core

Do not extract. The honest saving is zero while core references QueryTechniques. If a later
phase decouples core from QueryTechniques, this verdict must be revisited — record that as the
reopening condition, not as pending work in this phase.

---

## Corrections to the going-in table

1. **Caching row, "index 12 of 17"**: the default retrieval list has **16** entries
   (indices 0–15), not 17. `ResultCacheBehavior` is index 1, `EmbeddingCacheBehavior` index 12
   — both confirmed by the probe's printed list.
2. **Caching row, the risk**: real but narrower than feared. (a) is true, yet extraction does
   not have to remove the behaviors from the default pipeline at all — the type dependency is
   on `Caching.Abstractions`, not the Hybrid package (measured via
   `typeof(HybridCache).Assembly`). Task 5 as specified ("extraction removes two behaviours
   from the default pipeline") should be re-scoped per cluster 1 above.
3. **SQLite row**: two additional gates construct `Sqlite*` types —
   `UseEmbeddingVersioning()` (`RagBuilderExtensions.cs:132`) and `UseCostBudgeting()`
   (`RagBuilderExtensions.cs:355`) — and `SqliteDocumentStore` is user-constructed with no
   builder gate. All must move for the extraction to actually drop the dependency.
4. **Resilience/SQLite/Tokenizer rows are not independent**: `UseCostBudgeting` spans all
   three clusters (`CostTracking*` → `CostAccounting` → tokenizer; default ledger → SQLite).

## Verdict summary

| Cluster | Default-reachable | Verdict |
|---|---|---|
| Caching (7 pkgs) | Yes — instantiated, no-op **(a)** | **Extract** `UseCaching()` only; behaviors stay in core on a direct `Caching.Abstractions` reference; default pipeline untouched |
| SQLite (6 pkgs) | No | **Extract** — all five gates plus `SqliteDocumentStore`, not just the two originally listed |
| Resilience (15 pkgs) | No | **Extract** — `System.Threading.RateLimiting` moves with `UseRateLimiting`; cost budgeting spans three clusters |
| Tokenizer (3 pkgs) | No | **Do not extract** — QueryTechniques keeps both packages in the closure; zero saving. Revisit only if core↔QueryTechniques decouples |
