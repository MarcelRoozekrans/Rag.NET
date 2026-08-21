# Session State

**Last updated:** 2026-08-21 (phase 6.2.4 added)
**Written by:** `project-orchestration` — first `STATE.md` this project has had. Milestones 1–5 ran
without one, which is why every session so far re-derived its position from `ROADMAP.md` and
`MILESTONE.md` and twice acted on a debt that had already closed.

## Current Position

**Milestone:** 6 — Hardening & v1.0 — Battle-Tested (active since 2026-08-15)
**Phase:** 6.2.1 — Retrieval & Answer Sweep (active). **6.2.3 completed 2026-08-21**, merged in
#340 and verified on `main` by content rather than by the PR's MERGED label.

**Last completed:** **Phase 6.2.3 — Corpus-Level RAPTOR**, merged 2026-08-21 in #340 (squash
`c461475d`). Seven tasks, each independently reviewed, plus a whole-branch review and one fix wave.

`Rag.NET.Raptor` built its tree **per document**, which is not the RAPTOR paper's mechanism — a
per-document tree cannot contain a node spanning two documents. It now clusters over the corpus by
default (`RaptorTreeScope`, a breaking change), backed by a new `Rag.NET.Raptor.Store` package
holding leaf chunks *with their vectors*, debounced on growth with an on-demand `RaptorTreeRebuilder`
— #302's shape, for #302's reason.

**Two further defects were found by reading the package, and neither had ever been reachable by a
test:** #332, summary chunks colliding on `ChunkIndex` across levels; and #333, `SelectK` returning
k=n so a level never reduced and the tree loop **never terminated**, at one LLM call per cluster per
level — an unbounded spend at shipped defaults, in a published package.

**Why the suite was green throughout is the finding worth keeping.** A mock embedder constructed
`new Random(123)` *inside* its callback, so every summary embedding was byte-identical; identical
points collapse to k=1 and the loop exits after one level. **No test had ever built a RAPTOR tree
deeper than one level**, and both defects need depth ≥ 2. Two more fixtures of the same shape were
found while fixing it. The review loop also caught a first attempt at #333's fix that would have let
**one stray chunk switch clustering off for an entire corpus**, and a #332 regression test that had
become provably vacuous — it passed against the unfixed code.

**Phase state:** 6.2.1's four named debts are down to one. #239 and #200 closed 2026-08-17, #247
closed 2026-08-18 (pinned at 0.3494 in #280). **#176 remains, and is worse than filed**: 78.8%
singletons on the full corpus against the 65% the issue carries.

## Open Decisions

- ~~Does 6.1's live-service recording gate v1.0?~~ **Decided 2026-08-20: yes, it gates.** Against
  the re-plan's own recommendation, which had argued for `<VerifiedByReason>` on the grounds that a
  criterion satisfiable only by credentials that may never arrive is not falsifiable. 6.1's *work*
  is postponed behind 6.2.3; its *gate* is kept. The trade-off was raised and accepted: **v1.0 now
  waits on 18 cassettes whose blocker is accounts rather than effort.** Nothing in the codebase can
  move this — if the accounts do not arrive, the tag does not either. Worth revisiting if 6.2.3
  lands and 6.1 is still the only thing outstanding.
- **Where local search's yes/no abstention comes from.** It commits on 8.8% of comparison and 4.3%
  of temporal questions, while global search scores 0.4953 and 0.3928 on the same ones. A
  characterisation nobody has explained; it needs a home in 6.2.1 or an explicit deferral.
- **#298 — graph store backends beyond SQLite.** Recorded answer: *not yet*, and weaker now than
  when asked. Both costs once attributed to storage were a missing index and a per-document
  recompute, fixed without changing engines. Concurrency is the only surviving argument and nobody
  has stated that requirement.

## Blockers

- **6.1 is blocked on accounts, not on work — and as of 2026-08-20 it gates v1.0.** The harness
  works as of #290; 1 of 19 cassettes is recorded (GitHub, unauthenticated, 17 KB). #283 carries
  the corrected instructions for the remaining 18 and is marked help-wanted. No amount of local
  effort moves this, so it is the milestone's only blocker that engineering cannot clear.
- **The #300 follow-up measurement needs an idle machine.** Three timing runs on 2026-08-17
  disagreed by 6× on identical inputs, so no coefficient was claimed anywhere. It is still
  outstanding: the split of `BeirRunBudget`'s 22 m 18 s graph construction between LLM extraction,
  the `O(occurrences²)` description rewriting, and the recompute #302 debounced.

## Recommended Next Step

**Execute `docs/plans/2026-08-21-raptor-real-protocol-implementation.md`** — 6.2.1's RAPTOR
measurement, the first this technique has ever had. Six tasks; Tasks 1-3 are code, Tasks 4-5 spend
real money and Task 5 is an overnight run.

**6.2.4 completed 2026-08-21** (#344), so `raptorboost` now measures a `Boost` that works.

Three things govern the run, all in the plan:

1. **Ingest with the debounce suppressed, then `RebuildAsync()` once.** At the shipped
   `CorpusGrowthThreshold = 0.10`, 609 articles trigger 48 whole-corpus rebuilds. A fast-tier test
   asserts `TreeBuildCount == 1` so a regression fails in milliseconds rather than in dollars.
2. **Task 4's gate is real.** If `raptorfiltered − dense` is not ≈ 0 the corpora diverged and no
   figure means anything — stop, having spent a pilot rather than a sweep.
3. **`raptorcorpus` is RAPTOR's result, not `raptor`.** Publishing the per-document figure would
   repeat 5.2's misattribution, which cost three weeks and a revised published finding.

**Also unblocked and cheap:** deleting `GraphLocalSearchBehavior` and `PageRankWeight`.

## Working State

**Branch:** `chore/complete-phase-6-2-3`, cut from `main` at `c461475d`. Carries only this state
update. `phase/6.2.3-corpus-level-raptor` was merged (squash, `c461475d`) and deleted after the
content was verified on `main`.

**Issues from the 6.2.3 work:** #331, #332, #333 fixed and auto-closed on merge. **#336, #337 and
#338 remain open by decision**, each documented in `docs/guide/raptor.md`'s Known Limitations:

- **#338 is the one that matters most.** `DeleteAsync` does not touch the leaf store, so a deleted
  document's text can be read back, summarised, and stored as searchable content under
  `raptor://corpus-tree` — untraceable and undeletable. Live on the default path. A real fix needs
  an abstraction in core.
- **#336** — corpus summaries accumulate in the BM25 index on every ingest-triggered rebuild, and
  `RebuildAsync` bypasses BM25 entirely.
- **#337** — the variance floor is an absolute `1e-6`, so near-duplicate vectors still score as a
  near-perfect fit.

**Carry this into 6.1 and 6.2's remaining `unit` packages.** 6.2.3 found three separate test-fixture
defects, each of which made a real failure unreachable while the suite stayed green. `VerifiedBy=unit`
did not mean *untested*; it meant *the fakes could not produce inputs that fail*. Two shipped
defects and one unbounded-spend infinite loop survived in a published package because of it.
