# Microsoft GraphRAG local search: the specification, the gap, and the decision it forces

**Date:** 2026-08-18
**Status:** design — no code written
**Supersedes, in part:** `docs/plans/2026-03-30-graphrag-design.md` (local search section)
**Issues:** #247, #239

## Why this document quotes source instead of describing it

The 2026-03-30 design doc described local search in five steps. Step 3 — *collect* — never
shipped. Step 4 — the PageRank blend — did. The result was a behaviour that re-scored whatever
dense retrieval happened to return, and the −0.02761 nDCG@10 that Milestone 5.2 attributed to
"GraphRAG" turned out to be that blend and nothing else: at `PageRankWeight = 0` the ranking was
identical to the control on **2,255 of 2,255 queries** (`docs/guide/graphrag.md:273`).

A paraphrase is what lost step 3. So this document quotes the implementation, with the paths to
re-fetch it, and marks clearly where Microsoft's prose docs are silent and the code is the only
authority.

## Primary sources

| What | Where | Authority |
|---|---|---|
| Prose overview | `microsoft.github.io/graphrag/query/local_search/` | Names the five candidate categories. Silent on priority and budget. |
| Defaults | `packages/graphrag/graphrag/config/defaults.py` → `LocalSearchDefaults` | The values a default install runs with. |
| The algorithm | `packages/graphrag/graphrag/query/structured_search/local_search/mixed_context.py` → `LocalSearchMixedContext.build_context` | **The specification.** Everything below is read from here. |
| Entity selection | `packages/graphrag/graphrag/query/context_builder/entity_extraction.py` → `map_query_to_entities` | |

Re-fetch any of them with
`gh api "repos/microsoft/graphrag/contents/<path>" --jq '.content' | base64 -d`.

## The specification

Local search **builds a context window**. It does not rank documents, and it does not re-score
anything. The output is a string assembled from four sections under a token budget.

### 1. Entity selection

`map_query_to_entities(query, k=top_k_entities, oversample_scaler=2)` — a similarity search over
**entity-description embeddings**, asking for `k * 2` and then filtering by the exclude/include
lists. Note two things the code does that a paraphrase would smooth over:

- The oversample is **not** truncated back to `k` afterwards. With no exclusions the function
  returns 20 entities for `k = 10`. This is the behaviour, not an approximation of it.
- With an empty query it falls back to the `k` highest-`rank` entities, where `rank` is the
  entity's **relationship count** (degree).

### 2. The budget split

```
community_tokens  = max_context_tokens * community_prop
local_tokens      = max_context_tokens * (1 - community_prop - text_unit_prop)
text_unit_tokens  = max_context_tokens * text_unit_prop
```

with `community_prop + text_unit_prop > 1` a `ValueError`. Conversation history, when present, is
built **first** and its tokens are subtracted from `max_context_tokens` before the three
proportions are applied.

### 3. Community context — `_build_community_context`

Reports for the communities the selected entities belong to, ranked by community rank, filtered by
`min_community_rank`, truncated to `community_tokens`. Uses the full report by default
(`use_community_summary=False`).

### 4. Local context — `_build_local_context`

Entities first, rendered as a table and **always included** — the entity table's tokens are counted
but the loop below cannot evict it. Then, *entity by entity in selection order*, relationships and
covariates are added for the entities accumulated so far, and the moment
`entity_tokens + relationship_tokens + covariate_tokens > max_context_tokens` the loop **reverts to
the previous state and breaks**. Relationships are ranked by `relationship_ranking_attribute`
(default `"rank"`, the combined degree of the two endpoints), capped at `top_k_relationships`.

### 5. Text-unit context — `_build_text_unit_context`

The source chunks that produced the selected entities, via `entity.text_unit_ids`, deduplicated,
and sorted by `(entity_order, -num_relationships)` — that is: **the first selected entity's chunks
come first**, and within one entity, chunks covered by more of that entity's relationships come
first. Truncated to `text_unit_tokens`, `shuffle_data=False`.

### 6. Assembly order

`[conversation history] + [communities] + [entities, relationships, covariates] + [text units]`

### 7. Defaults

| Setting | `LocalSearchDefaults` | `build_context` signature |
|---|---|---|
| `text_unit_prop` | 0.5 | 0.5 |
| `community_prop` | 0.15 | **0.25** |
| `max_context_tokens` | 12,000 | **8,000** |
| `top_k_entities` | 10 | 10 (`top_k_mapped_entities`) |
| `top_k_relationships` | 10 | 10 |
| `conversation_history_max_turns` | 5 | 5 |

The two disagree. The config values win in a real run — the factory passes them — so
**0.15 / 0.5 / 12,000** is the specification and the signature defaults are dead. Worth recording
because copying the signature is the easy mistake.

### 8. What is *not* in it

No centrality score is blended into any similarity score anywhere in `build_context` or its four
helpers. Entity `rank` (degree) appears in exactly two places: sorting the empty-query fallback,
and an optional *display column* (`include_entity_rank`, default `False`). PageRank does not appear
in local search at all.

## What Rag.NET does today

`GraphLocalSearchBehavior` (after #312) fetches the top-`LocalTopEntities` entity chunks from the
graph's own store, walks their neighbours to collect PageRank scores, blends those into the
candidate scores from dense retrieval, deduplicates on `(DocumentId, ChunkIndex)`, and returns the
list.

## Gap

| Specification | Rag.NET | |
|---|---|---|
| Select entities by description-embedding similarity, `k×2` oversample | Selects by similarity, no oversample, truncates at `k` | partial |
| Community reports as a budgeted context section | Reports reach the model only if they win a dense-retrieval slot | **missing** |
| Entity table always in context | Entities compete for rank against article chunks | **missing** |
| Relationships, ranked by endpoint degree, capped at 10 | **Never reach the model at all** | **missing** |
| Covariates (claims) | Not extracted; no prompt, no table, no store column | **missing** |
| Source chunks via `entity.text_unit_ids`, ordered by entity then relationship coverage | Chunks come from dense retrieval, unrelated to the selected entities | **missing** |
| Token budget, split 0.15 / 0.35 / 0.5 | No budget concept | **missing** |
| Conversation history folded into the query and the context | Not plumbed | **missing** |
| — | PageRank blended into similarity scores | **not in the specification** |
| — | `CollectTopEntities` is dead code since #312 | remove |

Every "missing" row is the same missing thing: **the context builder**. Rag.NET implemented the
ranking arithmetic and skipped the assembly the arithmetic was supposed to serve.

## The decision this forces

`IRetrievalBehavior` returns `IReadOnlyList<SearchResult>` — a ranked candidate list. Microsoft's
local search returns a **rendered context string**. That is not a detail to paper over; it is why
the behaviour ended up as a re-ranker in the first place, and any implementation that keeps the
re-ranker shape will lose the specification again.

Three ways out:

**(a) Render, and inject as one synthetic chunk.** Local search stays an `IRetrievalBehavior`,
assembles the four sections, and returns a single `SearchResult` carrying the whole context.
Cheapest; but it makes one chunk that is 12,000 tokens wide, defeats every downstream reranker and
budget, and reports a chunk that corresponds to no document.

**(b) Return the assembled records as ordered `SearchResult`s.** The budget selects *what*, the
pipeline renders it. Keeps composability and the existing contract. Costs faithfulness: the
section structure and delimiters — which the local-search *prompt* is written against — are lost
unless the generation step is taught to rebuild them.

**(c) A separate entry point, `IGraphRagSearch`, outside the retrieval pipeline.** Mirrors
Microsoft's own structure, where `LocalSearch` is a search *strategy*, not a step in someone
else's. Faithful, and it stops pretending graph search is a filter over dense results. Costs a
second public surface, and GraphRAG stops composing with hybrid search and reranking.

**Recommendation: (c).** Local search is not a re-ranker. Two attempts to express it as one have
now produced a behaviour that cost −0.02761 and a behaviour that costs nothing because it has
nothing to do. (b) is the honest fallback if keeping one pipeline matters more than fidelity.

**This is the operator's call and nothing below is written until it is made.**

## Proposed phases, after that decision

| Phase | Content | Verifiable by |
|---|---|---|
| 6.x.1 | Delete the PageRank blend and `CollectTopEntities`; `PageRankWeight` obsoleted with the measurement in the message | unit + the existing harness reproducing the control |
| 6.x.2 | Context builder: entity + community + text-unit sections under a token budget, ordered per §6 | unit, with a fixture graph asserting section order and eviction |
| 6.x.3 | Relationships: store-side ranked query by endpoint degree, capped, budgeted | unit + integration against `SqliteGraphStore` |
| 6.x.4 | `entity.text_unit_ids` — requires entity→chunk provenance to survive ingestion, which `source_chunk_ids` already carries | unit |
| 6.x.5 | Covariates: extraction prompt, store table, context section. **Optional** — Microsoft ships it off by default | integration |
| 6.x.6 | Conversation history | unit |
| 6.x.7 | Re-measure on MultiHop-RAG against the same control as 5.2 | benchmark |

Phase 6.x.7 is the point of all of it. The 5.2 finding — "GraphRAG does not help on this corpus" —
was measured against a local search that had never implemented local search. It is not yet an
answer about GraphRAG.

## Open questions

1. **Does the answer prompt change?** Microsoft's local-search prompt is written against the
   section headers and `|` delimiters. Faithfulness to the context format is worth little if the
   prompt reading it is a different one.
2. **Tokenizer.** The budget is in tokens, and Rag.NET has no tokenizer in this path. A character
   estimate makes the proportions approximate; `Microsoft.ML.Tokenizers` makes them exact and adds
   a dependency to `Rag.NET.GraphRag`.
3. **Covariates cost an extra extraction pass over the whole corpus.** Given #300's measured
   152.9 s cold extraction on 609 documents, this is not free, and Microsoft defaults it off.
