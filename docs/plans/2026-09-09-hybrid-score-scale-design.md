# Design: a fused score declares itself, and stops being thresholded

**Issues:** #530, then #328 · **Phase:** 6.2.33 · **Date:** 2026-09-09

## 0. What is actually broken

`ScoreScale.OpaqueRanking` exists in this library for one purpose, and its own remarks name it:

> Scores are an opaque ranking signal: ordinal only… **fixed thresholds must not be applied.**
> Reciprocal Rank Fusion (whose scores peak near `1 / (k + 1)` per contributing store) and
> unbounded backend hybrid scores are both on this scale.

`IScoreScaleAware` states that absence means `Similarity` — *"which is what every score threshold in
the library already assumes."*

**Two stores return backend-fused hybrid scores, declare nothing, and threshold on them.**

| store | hybrid score | declares a scale | applies `MinScore` to it |
| --- | --- | --- | --- |
| `AzureAISearchVectorStore` | service-side fusion of BM25 and HNSW (`:369`) | no | **yes** (`:564-565`) |
| `WeaviateVectorStore` | `_additional.score`, Weaviate's own fusion (`:142`) | no | **yes** (`:412`) |

So a caller setting `MinScore = 0.5` — an ordinary cosine threshold — has it applied to a score
whose magnitude means something else entirely. It presents as "native hybrid search returns nothing
useful", with nothing logged and nothing thrown.

**The pattern was available and followed next door.** `FederatedVectorStore` (`:33`, `:85`) declares
`OpaqueRanking` because it fuses with RRF itself, and `PersistentConversationMemory` (`:66`) probes
for that declaration and skips its own threshold when it finds it. The one component that fuses
*inside* this library declares its scale correctly; the two that let a *backend* fuse for them
declare nothing.

**And the opposite call was made deliberately one store over.** `RedisVectorStore` (`:47-51`)
declines `IHybridSearchable` entirely, because a store advertising it "would be fusing a score it
cannot describe" — #86 asked for exactly that judgement. Azure and Weaviate shipped the feature
without making it.

**Nobody has reported this.** Found by reading, in the class of #56, where a Redis distance
published as a similarity would have inverted every ranking.

## 1. Why the obvious fixes do not work

**`IScoreScaleAware` requires a constant.** *"Must be a constant for the lifetime of the instance:
callers probe it once and may cache the answer."* That is not a detail to work around — a caller
that caches a per-request answer would be wrong half the time.

**But one instance serves two paths.** The same `AzureAISearchVectorStore` answers
`IVectorStore.SearchAsync` with a genuine cosine similarity and `IHybridSearchable.HybridSearchAsync`
with a fused score. **No single value is honest**, which is why the decision 6.2.5 deferred could not
simply be taken as posed.

Two routes were considered and rejected:

**Declare `OpaqueRanking` unconditionally** whenever the store implements `IHybridSearchable`.
Constant, one line, and it throws away working behaviour: `MinScore` is meaningful on these stores'
dense path, and every dense caller would silently stop being able to threshold. It trades a wrong
answer on one path for a lost capability on the other.

**Declare nothing; just stop thresholding on the hybrid path and document it.** Fixes the wrong
results and leaves the scale undiscoverable, so a caller still cannot tell what a hybrid score
means. That is most of the complaint, unaddressed.

## 2. The decision — the declaration goes per path

`IHybridSearchable` gains a scale for the path it owns, defaulted:

```csharp
ScoreScale HybridScoreScale => ScoreScale.OpaqueRanking;
```

`IScoreScaleAware` keeps its present meaning: the scale of `IVectorStore.SearchAsync`. Each property
is constant for the instance, so the contract holds exactly as written.

**The default is the correct answer for every implementer that exists.** A native hybrid fuses; a
fused score is ordinal. A future store that genuinely returns similarities from a hybrid query
overrides it and says so.

**Now is when this is cheap.** `IScoreScaleAware` has one implementer and `IHybridSearchable` has
two. Adding a member with a default costs nothing today and is a breaking change to a public
interface after v1.0. That is the same argument 6.2.6 and 6.2.23 made for taking their breaks
pre-tag.

## 3. Shape

### 3.1 The interface

`IHybridSearchable` gains `HybridScoreScale`, defaulted to `OpaqueRanking`, documented with the
reason rather than the mechanics: a native hybrid returns a fused score, and a fused score is not
comparable to a similarity threshold.

### 3.2 The two stores

Neither store needs to override the default — that is the point of defaulting it — but both gain
remarks explaining what their backend fuses and why the score is ordinal, in the style
`RedisVectorStore` already uses for its distance-to-similarity conversion.

### 3.3 `MinScore` is not applied to a fused score

Both stores stop passing `options.MinScore` into their hybrid result mapping.

**It is ignored rather than refused, and that is a deliberate choice against this project's usual
posture.** A throw would be louder and would match #490 and #521. It is wrong here: `MinScore` lives
on shared `RetrievalOptions` and is copied into every path's `SearchOptions`, so a caller who sets a
perfectly sensible threshold for dense retrieval would start crashing the moment a store advertised
hybrid. **The silence being removed is "we filtered you wrongly"; what replaces it is a declaration,
not an exception.**

The remarks on both hybrid methods say plainly that `MinScore` does not apply on this path and why,
so the behaviour is discoverable at the call site as well as through the capability probe.

### 3.4 What consumers do

`PersistentConversationMemory` already probes `IScoreScaleAware` and skips its threshold on
`OpaqueRanking`. Nothing in this phase changes consumer code: the stores stop producing wrong
results, and the new declaration gives callers a way to ask. Whether any consumer should start
probing `HybridScoreScale` is left to whoever needs it, deliberately — building a consumer for a
capability nobody has asked for is the speculative generality this repository flags elsewhere.

## 4. #328 lands on top as the third case

Once a fused path can declare itself, the semantic ranker is no longer a special decision:

- Opt-in **per instance**, and that is forced rather than chosen. Semantic ranking reshapes the
  score of the ordinary `SearchAsync` path, whose scale is `IScoreScaleAware.ScoreScale` — the
  property the interface requires to be constant for the instance's lifetime. A per-request toggle
  would make it vary, so the opt-in belongs on the constructor.
- With semantic ranking enabled, the store **implements `IScoreScaleAware` and returns
  `OpaqueRanking`**, because its dense path now returns Azure's `RerankerScore`; with it disabled,
  the store does not implement the interface at all and the path keeps its genuine cosine
  similarity. Constant either way, and true either way.
- The reranker score is returned as it comes, not rescaled from 0–4 into a fabricated similarity —
  an invented similarity is precisely what #56 was about.
- `KNearestNeighborsCount` becomes settable — the other half of #328, independent of the ranker and
  useful under RRF regardless.

**And it carries a guard, because the simulator lies.** Measured 2026-09-09: the local Azure AI
Search simulator accepts a `semantic` index configuration (HTTP 201, echoed back), accepts
`"queryType": "semantic"` with a `semanticConfiguration`, returns HTTP 200 and results — and returns
**no `rerankerScore` at all**. A test written the obvious way would pass whether or not semantic
ranking happened. So: when semantic ranking is requested and the response carries no reranker score,
**throw**. That converts a silent no-op into an error, and it is the only thing standing between
this feature and a green test that checks nothing.

## 5. What is deliberately not in scope

- **Changing `IScoreScaleAware` itself.** Its meaning is unchanged; only a sibling is added.
- **Consumer changes.** No caller is taught to probe `HybridScoreScale` in this phase.
- **Weaviate's fusion type.** Weaviate can fuse by ranked or relative-score fusion; both are ordinal,
  so the declaration is the same and the choice is not this phase's business.
- **Normalising fused scores into similarities.** Any mapping would be invented, and an invented
  similarity is exactly what #56 was about.

## 6. How it will be proved

Locally, in full, for #530's half — no Azure account required:

| claim | test |
| --- | --- |
| Azure declares `OpaqueRanking` for hybrid | property assertion |
| Weaviate declares `OpaqueRanking` for hybrid | property assertion |
| Azure hybrid does not filter by `MinScore` | store two chunks against the simulator, hybrid-search with `MinScore = 0.9`, assert both return |
| Weaviate hybrid does not filter by `MinScore` | same against the Weaviate container |
| the dense path still filters by `MinScore` | both stores — the capability that must **not** regress |

**Mutation-checked**, because a declaration is exactly the kind of thing that can be deleted without
any test noticing: revert each store's `MinScore` skip and each declaration in turn and confirm a
named test goes red. A declaration whose removal changes nothing is not protected by anything.

#328's half is verifiable only against a real resource. It ships behind its guard with
`<VerifiedByReason>` naming the gap, in the same position as 6.1's account-blocked cassettes.

## 7. Consequences

**Behavioural change, and it is a fix rather than a break in effect**: hybrid searches that
previously returned few or no results under a similarity-shaped `MinScore` will start returning the
page the backend actually ranked. A caller who had tuned a threshold against the old behaviour will
see more results, not fewer — the direction that fails safe.

**A public interface gains a member.** Defaulted, so no implementer breaks. Taken now because after
v1.0 it cannot be.

**Redis's refusal is left standing and is now better supported.** It declined to advertise hybrid
because it could not describe its score. After this phase a store *can* describe it — but Redis's
objection was to the score's provenance, not to the missing vocabulary, so the decision does not
reopen.
