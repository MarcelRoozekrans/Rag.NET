# Design: the semantic ranker, and the simulator that lies about it

**Issue:** #328 · **Phase:** 6.2.34 · **Date:** 2026-09-09

**Origin:** §4 of `docs/plans/2026-09-09-hybrid-score-scale-design.md`, approved as part 2 of 6.2.33
and split out on verifiability. **This document corrects three things in that section** — see §1.
Everything else there stands.

## 0. What is being built

@StefH asked (#328) for Azure AI Search's semantic ranker: a `SemanticSearch` configuration on the
index and `QueryType.Semantic` on the query, so the service reranks results with its own model. The
issue also asks for `KNearestNeighborsCount` to be settable. **That half is already shipped** —
6.2.5's #374 added `AzureAISearchOptions.KNearestNeighborsCount`, reachable through
`UseAzureAISearch(..., configure)` and validated eagerly. An earlier draft of this design said it
"becomes settable"; that was wrong, and reading the code rather than the issue is what caught it.

6.2.5 split the ranker off "pending the score-scale decision". 6.2.33 took that decision:
`IHybridSearchable.HybridScoreScale` now declares a fused path's scale, and the rule is that an
ordinal score is never thresholded. **The ranker is the third case of that rule**, not a new
question.

## 1. Three corrections to §4 of the 6.2.33 design

**§4 said the store "implements `IScoreScaleAware`" when the ranker is on and "does not implement
the interface at all" when off. That is not expressible in C#** — a class implements an interface or
it does not; the decision is compile-time.

**The store implements it unconditionally and returns a value fixed at construction:**
`OpaqueRanking` when the ranker is on, `Similarity` when off. This satisfies the interface's
"constant for the lifetime of the instance" contract exactly, and it is **behaviour-preserving when
off**: `PersistentConversationMemory` tests `is not IScoreScaleAware { ScoreScale: OpaqueRanking }`
(`:66`), so a store declaring `Similarity` and a store not implementing the interface take the same
branch. Nothing changes for anyone who does not opt in.

**§4 implied verification is wholly account-blocked. It is not, and the simulator's own defect is
why.** Measured 2026-09-09: the simulator accepts a semantic index configuration and
`queryType: semantic`, returns HTTP 200 with results, and returns **no `rerankerScore`**. That makes
it a *fixture for the guard*: requesting the ranker against the simulator must throw, and that is a
real local test of the thing most likely to be got wrong. What genuinely needs a resource is
narrower — see §5.

**§4 did not mention the index migration, and Azure turns out not to have the hazard Redis had.**
`InitializeAsync` calls `CreateOrUpdateIndexAsync` with a freshly built definition (`:87`), so an
existing index is **reconciled** to include the new semantic configuration on the next
initialisation. Contrast 6.2.31, where `RedisVectorStore.InitializeAsync` deliberately leaves an
existing index alone — because dropping it would discard every stored vector — and therefore needed
an `FT.INFO` guard. **No equivalent guard is needed here**, and the reason is worth writing down so
nobody adds one by analogy.

## 2. Shape

### 2.1 Opt-in, per instance

A constructor parameter, defaulted off, threaded through the three public constructors to the
private one. Per instance rather than per request **because it reshapes the score of the ordinary
`SearchAsync` path**, whose scale `IScoreScaleAware` requires to be constant for the instance's
lifetime. A per-request toggle would make that vary, and callers may cache the probe.

The semantic configuration's name is an implementation detail, not a knob: one configuration, named
by the store, built from the fields the store already defines.

### 2.2 The index gains a semantic configuration

`BuildIndex` adds a `SemanticSearch` with one `SemanticConfiguration` prioritising the `text` field
as content. Added **only when the ranker is enabled** — an index carrying a configuration nothing
uses is clutter, and adding it unconditionally would change every existing index on next
initialisation for no benefit.

### 2.3 The query asks for it

When enabled, `SearchAsync` sets `QueryType = SearchQueryType.Semantic` and
`SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = ... }`.

**Scope: the dense path only.** `HybridSearchAsync` is left alone. Combining the ranker with native
hybrid fusion is a second question about what score comes back from two rerankers stacked, and
answering it here would double the unverifiable surface.

### 2.4 The score

`SearchResult<T>.SemanticSearch.RerankerScore` is returned **as it comes**, and the store declares
`OpaqueRanking`. It is not rescaled from Azure's 0–4 into a [0,1] similarity: an invented similarity
is exactly what #56 was about, and `ScoreScale` exists so a store can say "ordinal" instead of
faking a number.

**`MinScore` is therefore not applied** when the ranker is on, for the same reason and by the same
rule as 6.2.33's hybrid path.

### 2.5 The guard

**When the ranker is enabled and a result carries no `RerankerScore`, throw**, naming the index and
saying the service did not perform semantic ranking.

This is the whole reason the feature can be shipped honestly. The simulator returns HTTP 200 with
results and no reranker score; a real service will do the same if the tier does not include semantic
ranking, if the region does not support it, or if the configuration name does not match. **Every one
of those is a silent downgrade to ordinary scoring** — the caller gets plausible results, ranked the
old way, believing they were reranked. The guard converts all of them into one loud failure.

### 2.6 `k` and the ranker interact, and the option's own remarks already say how

`KNearestNeighborsCount` is already settable, so nothing is added there. What is added is a **guard
on the combination**, because the two settings interact in a documented way that fails quietly.

That option's remarks already quote Microsoft: *"Whenever you use semantic ranking with vectors, set
`k` to 50. Semantic ranker uses up to 50 matches as input. Specifying less than 50 deprives the
semantic ranking models of necessary inputs."* They end by noting semantic ranking is not
implemented here yet and that the advice is for anyone configuring the index themselves. **This
phase is when that stops being hypothetical.**

So **enabling the ranker with `KNearestNeighborsCount` set below 50 is rejected at registration**,
in `ValidateConfigured`, beside the existing `k < 1` check. Leaving it `null` stays valid and
remains the right default — omitting the parameter is what makes Azure apply its own 50.

**Rejected rather than warned, unlike §2.4's `MinScore` decision**, and the difference is the point:
`MinScore` arrives on shared per-request options that a caller may have set for an entirely
different path, so throwing would punish an innocent configuration. `k` and the ranker are both
deliberate, set on the same options object, at registration, by the same person. Nobody combines
them by accident — and the damage is invisible: worse ranking, no error.

The option's remarks are updated to say the guard exists rather than that the feature does not.

## 3. What is deliberately not in scope

- **Semantic ranking on the hybrid path** (§2.3).
- **Semantic captions, answers, or highlights.** #328 does not ask for them and each is its own
  response shape.
- **Rescaling the reranker score.** §2.4.
- **A tier or region probe.** The guard catches the consequence without the store having to model
  Azure's SKU matrix.

## 4. How it will be proved

Locally, against the simulator and without any account:

| claim | test |
| --- | --- |
| the guard fires when the service does not rerank | enable the ranker, search, assert the throw — **the simulator produces this condition naturally** |
| the index gains a semantic configuration when enabled | read the index definition back and assert it |
| the index has none when disabled | same, asserting absence |
| the store declares `OpaqueRanking` when enabled | property assertion |
| the store declares `Similarity` when disabled | property assertion — pins the behaviour-preserving default |
| enabling the ranker with `k` below 50 is rejected at registration | `UseAzureAISearch` with both set, assert the throw names both |
| enabling the ranker with `k` null or at least 50 is accepted | the same, asserting no throw |
| the dense path is unchanged when the ranker is off | the existing suite, which must stay green |

**Mutation-checked**, including the two that matter most: delete the guard (the enabled-path test
must go red) and flip the declared scale (the property tests must go red).

## 5. What cannot be verified without a resource, stated precisely

**One claim: that Azure's ranker populates `RerankerScore` and reorders results.** That is Azure's
behaviour, not this library's wiring, and every part of the wiring is testable without it.

`<VerifiedByReason>` should say exactly that rather than implying the feature is untested. Basic tier
or higher, billable, region-limited.

**The honest position: if the service silently does not rank, this code throws rather than
publishing a number it cannot describe.** That is a weaker promise than "the ranker works", and it
is the one that can be kept from here.

## 6. Consequences

**Additive and off by default.** No existing caller's behaviour changes: the constructor parameter
defaults off, the index gains nothing, the query is unchanged, and the declared scale is
`Similarity`, which is the same branch every consumer already took.

**Opting in is a deliberate trade**: better ranking from the service, in exchange for a score that
can only be ordered and not thresholded — declared, not hidden.

**The public API grows**: one option, one constructor parameter and one interface implementation on
`AzureAISearchVectorStore`. Pre-1.0, and cheaper now than after.

**One configuration becomes invalid**: the ranker enabled together with an explicit `k` below 50.
Nobody can be in that state today, because the ranker does not exist yet.
