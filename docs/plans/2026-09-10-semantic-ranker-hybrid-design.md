# The Ranker Belongs Where the Text Is — design for Phase 6.2.36

**Issue:** #539 (reported by @StefH), reopening the half of #328 that #536 got wrong. **Phase:** 6.2.36. **Written:** 2026-09-10.

## 0. What 6.2.34 got wrong, and how long it survived

**Three hours.** #536 merged at 10:02; #539 was filed at 11:30 against a real Azure resource.

Semantic ranking needs query text. Microsoft is explicit: *"A query with `search=*` or an empty
search string … won't work because there's nothing to measure semantic relevance against."*

The dense path has no text, **by interface contract**:

```csharp
Task<IReadOnlyList<SearchResult>> SearchAsync(
    ReadOnlyMemory<float> queryEmbedding,   // no text
    SearchOptions options,                  // TopK, MinScore, MetadataFilter — no text
    CancellationToken cancellationToken = default);
```

So `EnableSemanticRanking` on the dense path **cannot rank on any tier, in any region**. It is not a
wiring bug to be fixed in place; the feature was put on the one path that cannot carry it. The only
path that can is the one 6.2.34 explicitly excluded:

```csharp
Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
    string textQuery,                       // here it is
    ReadOnlyMemory<float> queryEmbedding,
    SearchOptions options,
    CancellationToken cancellationToken = default);
```

**The reason 6.2.34 gave for excluding the hybrid path was not wrong, it was just not a reason to
prefer the dense one.** It said stacking the reranker on Azure's own fusion "raises a separate
question — what score comes back from ranking an already-fused result — that this store does not yet
answer". True. But the dense path does not answer an easier question; it answers none, because it
cannot run the query at all.

## 1. The guard is the reason this is an issue and not a silent corruption

Without the throw #536 shipped, @StefH would have received **ordinary cosine similarities relabelled
as reranker scores**, with the store declaring them `OpaqueRanking` and skipping `MinScore`. Wrong
numbers, presented confidently, on a path advertised as reranked.

**The guard caught a defect its own author did not, within three hours.** That is the strongest
evidence this milestone has produced for its own rule — *where a guard is cheap, prefer a throw to a
tolerant return* — and it is worth recording precisely because the phase that wrote the guard is the
phase the guard indicted.

**The `<VerifiedByReason>` was also right.** It said everything downstream of the guard was
unverifiable without a billable resource. A billable resource found it immediately. The gap was
declared honestly and the declaration was load-bearing, not decoration.

## 2. Decided: the ranker moves to the hybrid path

`HybridSearchAsync` already carries `textQuery`. `IHybridSearchable.HybridScoreScale` is **already**
`ScoreScale.OpaqueRanking` (6.2.33), so replacing a fused score with a reranker score needs **no
scale change at all** — both are ordinal, neither may be thresholded. `MinScore` is already forced to
`0.0` on that path. The move is smaller than it sounds, and it lands the ranker where the existing
declarations already describe it.

**What happens to the dense path.** `EnableSemanticRanking` stops affecting `SearchAsync` entirely.
It cannot be refused at registration — one instance serves both paths, and a caller who enables
ranking for hybrid still legitimately uses dense search — so the dense path simply keeps its genuine
cosine similarity.

**Which raises a question this phase must answer rather than inherit:** with the ranker no longer
touching `SearchAsync`, `IScoreScaleAware.ScoreScale` returns `Similarity` unconditionally, and
6.2.34's whole reason for implementing that interface disappears. `Similarity` is documented in
`ScoreScale`'s own remarks as the assumed scale for stores that do not implement it, so the
implementation becomes exactly equivalent to its own absence. **Keep it as a discoverable
declaration, or remove it as vestigial?** Settle before implementation.

## 3. The silent path, which is the same defect one level up

**Decided 2026-09-10: throw.** A request for ranking that cannot be honoured is an error, not a
downgrade.

Moving to hybrid does not put the ranker in the caller's hands — it puts it behind
`EnsembleBehavior`'s dispatch, which takes the native path only when **all** of these hold:

```csharp
if (VectorStore is IHybridSearchable nativeStore && CanDispatchNatively(opts))

private bool CanDispatchNatively(RetrievalOptions opts) =>
    opts.EnsembleOptions is null
    && opts.MinScore is 0.0
    && !SparseArmWouldRun(opts);
```

Violate any one and the query is fused client-side by RRF — **correct results, no ranking, no
error**. A user who sets `MinScore = 0.7` and enables the ranker gets neither a threshold applied to
a ranked score nor a ranking; they get RRF and silence. That is precisely the "code that succeeds
while doing nothing" family this milestone was built to remove, reappearing one layer above the
guard that removes it.

So `EnsembleBehavior` must detect *ranking is enabled but this query cannot reach the native path*
and throw, naming which condition blocked it.

**The design cost is a coupling**, and it should be paid deliberately: the behaviour has to ask the
store whether ranking is on. `_semanticRankingEnabled` is private and Azure-specific. **The probe
must be a general capability, not an Azure flag** — something on `IHybridSearchable` meaning *client-
side fusion would silently lose something this store does natively*, defaulted so every existing
implementer stays correct without change. Naming and exact shape to settle in the plan; 6.2.33's
`HybridScoreScale` is the precedent for adding a defaulted member to that interface pre-1.0.

## 4. A fifth condition nobody has written down, and it is not the ranker's fault

**`ResilientVectorStore` does not implement `IHybridSearchable`.** `EnsembleBehavior` injects
`IVectorStore` — the **decorated** instance when resilience is registered — and probes
`VectorStore is IHybridSearchable`. That probe is `false` under resilience, so **native hybrid
dispatch never happens at all when resilience is enabled**, on every store that supports it.

This is pre-existing and independent of semantic ranking: it silently downgrades native hybrid search
to client-side RRF for anyone using `Rag.NET.Resilience` with Azure AI Search or Weaviate today.

**The repository already solved this exact problem once, for the sibling capability, and the class
doc says so:**

> `ISparseSearchable`, so an `is ISparseSearchable` probe on the resolved `IVectorStore` stays honest
> after decoration.

There is a `ResilientSparseVectorStore`. There is no resilient hybrid variant. The reasoning was
applied to sparse and not to hybrid.

**Two consequences for this phase.** First, it should be **filed separately** — it is not the
ranker's defect and fixing it here would smuggle an unrelated change into a fix for #539. Second,
**§3's throw will surface it immediately and painfully**: resilience plus semantic ranking will throw
on every query, and the user cannot resolve it except by disabling resilience. The plan must decide
whether 6.2.36 ships before or after that separate fix, or whether the throw's message names
resilience specifically as a known cause.

## 5. Scope

In:

1. **Move the ranker to `HybridSearchAsync`** — `QueryType.Semantic`, the semantic configuration,
   `expectRerankerScore: true`, and the existing guard, all on the path that has text.
2. **Dense path stops reading the flag.** `SearchAsync` keeps genuine cosine similarity.
3. **Settle `IScoreScaleAware`** — keep as a declaration or remove as vestigial (§2).
4. **The `EnsembleBehavior` throw** with a general capability probe (§3).
5. **Docs** — `vector-stores.md`'s semantic-ranking section is currently **wrong in the same way the
   code was** and must be rewritten, not amended: it documents the dense path throughout.
6. **The `k >= 50` guard stays.** It governs the vector arm's recall, which the hybrid path still
   uses.

Out:

- **The resilience/hybrid gap (§4)** — file it, do not fix it here.
- **Reverting 6.2.34.** The `k` guard, the scale declarations, the semantic index configuration and
  the guard itself are all correct and independently useful. Only the *path* was wrong.

## 6. Verifiability, stated plainly because it is the same trap

**The simulator still lies.** It accepts `queryType=semantic` and returns no `rerankerScore`, so the
guard's throw is testable locally and everything past it is not — unchanged from 6.2.34, and the
reason 6.2.34's error survived review.

**What is different this time is that the shape of the mistake is now known.** The failure was not
"we could not test the ranker"; it was "we did not check whether the path we chose could carry a text
query", which needed **no** Azure resource to catch — only reading
`IVectorStore.SearchAsync`'s signature against Microsoft's sentence. **A local test could not have
caught this, and a careful reading of two documents could.** Record that distinction rather than
concluding the account gap is to blame.
