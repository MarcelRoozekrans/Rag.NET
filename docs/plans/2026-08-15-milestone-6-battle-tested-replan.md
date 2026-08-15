# Milestone 6 re-plan — "battle-tested": every feature exercised for real before v1.0 (2026-08-15)

**Status:** proposal, written the day Milestone 5's last phase closed, at the owner's request:
*"Can we make Milestone 6 about this — test all available features to be sure we are battle
tested?"* Adopted when Milestone 5 completes; the ROADMAP's Milestone 6 section is rewritten then,
from this.

## Why this, and why now

Milestone 6 already says "every package exercised beyond fakes, or a recorded reason" (#199) — but
its unit is the *package*, and this week showed the unit that matters is the **feature**.
`Rag.NET.GraphRag` was `VerifiedBy=unit`, `✅ Done` in `features.md`, green in every suite, published
at 0.1.0 — and running it once end to end found **eight defects** (six in #168, #209, #230), then a
mis-scaled blend (#239), a per-process shuffle seed (#241), a sequential report loop (#226), and a
store design that costs answers −0.21 (#247). Not one was found by a unit test, a review or a user.
Every one was found by *running the real thing on a real corpus and looking at a number*.

The dense path is the counter-example: calibrated against four published figures to ±0.003, and
every defect ever found in it (3.13, 3.16, the reranker's `[UNK]`, the surrogate pairs) was found
**by** that calibration. Battle-tested means: every feature has its own version of that.

**The ledger today:** 71 packages — **62 `VerifiedBy=unit`**, 9 `container`, 0 `recorded`,
0 `benchmark`. Six test projects gated on secrets. `features.md`: 56 rows marked `✅ Done`, none of
which carries a pointer to what exercises it. So today's honest answer to "are we battle-tested" is
no, for 62 of 71 — and the answer is unknowable for most of the 56, which is worse.

## What "battle-tested" means, per kind of feature — falsifiably

One definition per category, so the phase cannot satisfy itself by taste. A feature is battle-tested
when it has **one of the following, named in the ledger, checked by a test**:

| Kind | Exercised by | Evidence pinned |
|---|---|---|
| Retrieval technique (dense, hybrid, HyDE, reranking, GraphRAG local/global, RAPTOR, MMR-successors, late chunking, SPLADE) | a run over a real corpus with a real model | a figure in `BeirReproduction`-style table at ±0.005, with a control it is differenced against |
| Answer engine (MapReduce, Refine, FLARE, dispatching) | the 5.2.2 answer harness, one arm per engine, over MultiHop-RAG's gold answers | an accuracy in `MultiHopRagAnswerReproduction`, judged by the paper's rule |
| Vector store / index (InMemory, Qdrant, Weaviate, PgVector, Chroma, Pinecone, AzureAISearch, BM25) | the parity leg (SciFact) through the store instead of `InMemoryVectorStore` | the parity figure reproduces to ±0.005 through that store — the *same* number, which is the whole test |
| Chunker / parser (Recursive, Semantic, late, Markdown, PDF, DOCX, email …) | one real document of its kind through the real path, counts asserted | shape assertions (units, max per document, nothing empty) — the `Chunking_Splits…` pattern |
| Ingestion / connectors (Service Bus, SaaS connectors, Docker-tier) | a container or a recorded exchange | `VerifiedBy=container` or `recorded` (6.1) |
| Live services with no credentials | — | `VerifiedBy=unit` **plus** `<VerifiedByReason>` naming the service and the gap (6.1's rule) |
| Pipeline plumbing (`AddRagNet`, behaviour placement, decorators, options) | **pipeline-parity**: the same query through a real `AddRagNet` pipeline and through the harness, top-k identical | a fast-tier test, every push |
| Hosting surfaces (API, MCP, CLI) | the E2E suite against a running host | already exist; listed so nothing is assumed |

`features.md` gains an **Exercised by** column, and a conventions test fails a `✅ Done` row whose
column is empty. The ledger test (already there) fails a bare `VerifiedBy=unit`. Those two guards
are the milestone's definition of done, and they are checked, not asserted.

## Phases (proposed)

| Phase | Name | Owns |
|---|---|---|
| **6.0** | The Inventory | The `Exercised by` column; the per-kind definitions above; the ledger values `recorded`/`benchmark`; the two guards; a first pass classifying all 56 rows and 71 packages as *exercised / plan / declared* — so 6.1–6.3 start from a list, not a feeling |
| **6.1** | Recorded Responses (as planned) | ~20 live-service packages: scrubbed dated recordings, or `<VerifiedByReason>` |
| **6.2** | Raise the Floor (as planned, now defined) | the ~41 non-live `unit` packages, each through its row in the table above; parsers/chunkers via real files, stores via the parity leg |
| **6.3** | Retrieval & Answer Sweep — *the GraphRAG method, applied to the rest* | RAPTOR, HyDE, hybrid, reranking, late chunking, SPLADE through the BEIR harness with pins; the three answer engines through the answer harness; #247 fixed and re-measured as the first "battle test found it, fix it, measure again"; #239 decided; the pipeline-parity test |
| **6.4** | Release v1.0 (as planned) | tag, release-please, nuget metadata; both OS matrices green |

Debts already pointing here, absorbed rather than re-listed: #247 store policy, #239 blend/traversal,
#176 singleton communities, #200 usage recording, #246 Service Bus flake, #104 routing (declared, not
built), 3.15's reranker-permutes-only-top-10 note, the reranked cells' cold cost, TREC-COVID's
weakest-agreement note.

## What this costs, honestly

Time, mostly. Each retrieval feature is one BEIR run (10 minutes to an hour, cached embeddings);
each answer engine is one arm of the answer harness (~$3 derived at gpt-4o-mini, replayed after);
each store is the SciFact parity leg through a container (~5 minutes each once wired). The
recordings are the slow part — 20 services, each needing an account or a stated reason. **Order of
magnitude: several weeks of sessions, not one overnight.** The point of 6.0 is to make that a list
with checkboxes rather than a mood.

## What it does not promise

That every feature is *good*. Battle-tested means measured — 5.2 showed a feature can be measured
and found wanting, and that is a completion. It also does not promise coverage of every option on
every feature; one real run per feature at its shipped defaults, differenced against a control, is
the bar. Anything above it is a phase with its own name.

## Decision needed from the owner

Two things the plan cannot decide alone: **whether the six months of live-service recordings (6.1)
stay in scope for v1.0**, or whether v1.0 ships with `<VerifiedByReason>` on the packages without
credentials (the current Milestone 6 text allows the second); and **whether 6.3's fixes** (#247,
#239) are in v1.0 or after it. The plan's recommendation: recordings where credentials exist, reasons
where they don't, and #247 fixed before v1.0 — shipping a graph package whose measured best
configuration is "don't put it in the same store" is the kind of thing this milestone exists to stop.
