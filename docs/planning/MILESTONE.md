# Milestone 6: Hardening & v1.0 — Battle-Tested

**Status:** active
**Started:** 2026-08-15, the day Milestone 5 was audited and archived

Completed milestones are archived under `docs/planning/milestones/`. Milestone 5 was archived
there on 2026-08-15 after its close audit — `docs/plans/2026-08-15-milestone-5-audit.md`, verdict
PASS on all five criteria.

> **The ROADMAP is authoritative for the DoD below; the two must agree, and when they do not, this
> file is the one that is wrong.** This file drifted twice in Milestone 4/5 and was caught by an
> audit both times; it is rewritten at every milestone boundary for that reason.

> **This milestone was re-planned before it opened**, at the operator's request on 2026-08-15 —
> *"make Milestone 6 about testing every available feature so we are battle-tested"* — from
> `docs/plans/2026-08-15-milestone-6-battle-tested-replan.md`. The ROADMAP's Milestone 6 section
> is rewritten from that note. **Two decisions in that note are the operator's and are still
> open**: whether 6.1's live-service recordings gate v1.0 or `<VerifiedByReason>` does where no
> credentials exist; and whether the #247 / #239 fixes ship in v1.0. Until they are decided, the
> phases below carry the note's recommendation and say so.

## Goal

Every shipped feature exercised for real — a real model, a real corpus, a real store, a real
file, a real service or a recorded one — with the evidence pinned and checked by a test, before
v1.0. Milestone 5 showed why: `Rag.NET.GraphRag` was `VerifiedBy=unit`, `✅ Done`, green, published,
and running it once found eight defects. The dense path, calibrated against four published figures,
is the counter-example: every defect ever found in it was found by that calibration.

## Definition of Done

Authoritative copy in the ROADMAP's Milestone 6 section, in Phase 4.0's falsifiable style.

- [x] **Milestone 5 complete** — closed 2026-08-15 by audit, verdict PASS.
- [ ] **All planned phases complete** — 6.0 Inventory, 6.1 Recorded Responses, 6.2 Raise the
      Floor, 6.2.1 Retrieval & Answer Sweep, 6.2.2 Requested Features, 6.3 Release v1.0.
- [ ] **Every `✅ Done` row in `features.md` names what exercises it** — an *Exercised by* column,
      pointing at a test or benchmark that runs the real thing, and a conventions test that fails a
      ✅ row with an empty column. Today: 56 rows, 0 pointers.
- [ ] **No package remains at bare `VerifiedBy=unit`** — each is `integration`, `container`,
      `recorded`, `benchmark`, or carries `<VerifiedByReason>` naming the service and the gap; the
      ledger test fails a bare `unit`. Today: **32 of 71**, down from 62 when this milestone opened.
      *(`integration` was added 2026-08-16 in Phase 6.2. The level set enumerated here could not
      express "exercised against something real that is not an external service" — a real file, a
      real process, a real host over its real transport, a real store reopened — which is exactly
      what §2 of the 6.2 design defines as the bar for twenty-five of these packages. They met it
      and the ledger had no way to say so. Amending a DoD mid-milestone is not free, and the
      alternative was worse: leaving twenty-five packages at a `unit` that had become false, or
      spending the `<VerifiedByReason>` escape hatch on packages that are exercised.)*
- [ ] **Every retrieval technique and answer engine has a pinned figure with a control** — the
      GraphRAG method (5.2 / 5.2.1 / 5.2.2) applied to RAPTOR, HyDE, hybrid, reranking, late
      chunking, SPLADE, and the three answer engines; every vector store reproduces the SciFact
      parity figure through itself; a pipeline-parity test holds a real `AddRagNet` pipeline to the
      harness's top-k on every push.
- [ ] **The release commit is green on both `ci.yml` matrices**, the Docker tier and the latest
      nightly green on Linux, stated as such.
- [ ] **Release tagged v1.0.**

## Phases

| Phase | Name | Issues | Status |
|---|---|---|---|
| 6.0 | The Inventory | — | **complete** 2026-08-15 — both guards on every push, failing behind a work list: 5 packages at `benchmark`, 57 at bare `unit` owned by 6.1/6.2/6.2.1; 51 Done sections, 2 exercised, 49 owned |
| 6.1 | Recorded Responses | — | pending — as planned since 2026-08-03 |
| 6.2 | Raise the Floor on Unit-Only Packages | — | pending — now defined per kind: parsers/chunkers via a real file, stores via the parity leg, utilities via one real run |
| 6.2.1 | Retrieval & Answer Sweep | #247, #239, #176, #200 | pending — the GraphRAG method applied to the rest; #247 fixed and re-measured first; the pipeline-parity test |
| 6.2.2 | Requested Features | #252 | **complete** 2026-08-16 — #252 built, both open design questions settled, exercised in the fast tier and over a real HTTP server |
| 6.3 | Release v1.0 | — | pending — as planned; first work is the nuget.org account, key and 70 reserved IDs |

## Known debt carried into this milestone

- **#247** one shared store for article and graph-derived chunks — measured −0.043 nDCG, −0.21
  answer accuracy; the largest lever any Milestone 5 measurement found. 6.2.1, first.
- **#239** the PageRank blend on the wrong scale and the discarded traversal — measured, decision
  pending. 6.2.1.
- **#176** 65% singleton communities; **#200** no usage recording; **#246** Service Bus emulator
  lock race on a loaded runner. *(**#104** — routing declared not built — was listed here in
  error: it was closed 2026-08-10, before this milestone opened. Removed 2026-08-16 by the issue
  triage that also assigned #239/#246/#247 to the GitHub milestone.)*
- **#252** `SitemapDataProvider` cannot skip URLs — reported 2026-08-15 against a shipped package.
  6.2.2. **Closed 2026-08-16**: `SitemapOptions` with prefix and regex exclusions, applied to
  nested index links as well as page URLs.
- **TREC-COVID's weakest-of-the-datasets agreement** (−0.018 in ±0.02) — nothing yet looks into
  it; 6.2's parity-through-stores runs the same leg many more times and is where it would show.
- **Generation lives in the answer test class rather than the tool** (5.2.2's stated deviation).
- From Milestone 4: the Azure Document Intelligence live half, the AzureAISearch OData filter path,
  the Pinecone live sparse-write verification — all 6.1's.

## Explicitly not in scope

- **Making every feature good.** Battle-tested means measured; a feature measured and found wanting
  is a completion, as 5.2 was. *(6.2.2 is the one deliberate exception, and is scoped to named,
  reported requests against shipped packages — not to improvement in general. It exists because
  this is the terminal milestone, so a request filed against a published package has nowhere
  later to land.)*
- **Every option on every feature.** One real run per feature at its shipped defaults, differenced
  against a control, is the bar; anything above it is a phase with its own name.
- **Anything on the "Beyond v1.0: Recorded, Not Scheduled" list.**
