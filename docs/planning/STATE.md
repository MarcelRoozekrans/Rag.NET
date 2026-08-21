# Session State

**Last updated:** 2026-08-20
**Written by:** `project-orchestration` — first `STATE.md` this project has had. Milestones 1–5 ran
without one, which is why every session so far re-derived its position from `ROADMAP.md` and
`MILESTONE.md` and twice acted on a debt that had already closed.

## Current Position

**Milestone:** 6 — Hardening & v1.0 — Battle-Tested (active since 2026-08-15)
**Phase:** 6.2.3 — Corpus-Level RAPTOR (pending, added 2026-08-20, **gates v1.0**)
**Also active:** 6.2.1 — Retrieval & Answer Sweep, whose RAPTOR measurement is queued behind 6.2.3

**Last completed:** the GraphRAG local-search thread — spec sub-phases 6.x.1, 6.x.6 and 6.x.7 of
`docs/plans/2026-08-19-graphrag-local-search-completion-implementation.md`. Tasks 1–5 merged in
#323; Task 6's measurement ran overnight and merged in #326 (merge commit `2067f7f6` on `main`,
verified by content, not by the PR's MERGED label).

That measurement **revised a published Milestone 5 finding**. 5.2 concluded "GraphRAG does not help
on this corpus" from a `local` arm scoring 0.2102 — a PageRank blend that is not in Microsoft's
local search at all. Measured properly: **0.3459 overall, 0.8603 on inference** — the strongest
entity-question result this project has recorded, above global's 0.8444 and dense's 0.7721. Level
with dense overall (−0.0040) only because it abstains on yes/no questions.

**Phase state:** 6.2.1's four named debts are down to one. #239 and #200 closed 2026-08-17, **#247
closed 2026-08-18** (fixed twice over — #311 hid graph chunks from retrieval by default, #312 gave
them their own store; pinned at 0.3494 in #280). **#176 remains, and is worse than filed**: 78.8%
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

**Open the PR for `phase/6.2.3-corpus-level-raptor`, and read the three parked residuals below
before merging.** The phase is implemented and reviewed; the merge is the operator's.

After the merge: **6.2.1's RAPTOR measurement**, whose design at
`docs/plans/2026-08-20-raptor-real-protocol-design.md` has been waiting since 2026-08-20.
It can now price both tree scopes in one run, because 6.2.3 kept `PerDocument` selectable rather
than deleting it.

**Also still unblocked and cheap:** deleting `GraphLocalSearchBehavior` and `PageRankWeight`. They
were kept alive until 6.x.7 published its replacement figure, which it did on 2026-08-20.

## Working State

**Branch:** `phase/6.2.3-corpus-level-raptor`, cut from `main` at `3f5d14fb`. **22 commits, nothing
pushed, no PR open.** `bench/graphrag-localspec-measurement` is merged and safe to delete.

**Phase 6.2.3 is implemented and reviewed but NOT merged.** ROADMAP and MILESTONE still show it
`pending` deliberately — it is promoted to `complete` after the merge, not before. Do not mark it
complete from this file.

**What the branch carries:** the state-file refresh; two design specs (6.2.1's measurement, 6.2.3's
fix); the implementation plan; and seven implemented tasks — the #332 collision fix, the
`Rag.NET.Raptor.Store` package, `TreeScope`, corpus clustering with a growth debounce,
`RaptorTreeRebuilder`, the #333 `SelectK` fix, and the breaking default flip.

**Issues filed this session:** #331 and #332 (both fixed here), #333 (fixed here), #336, #337, #338
(all three filed, documented as known limitations, deliberately not fixed).

**Three residuals parked for the human reviewer**, none affecting correctness:
1. `TestProjectTierTests.cs:16` says "65 test projects"; the true count is 76. It was already wrong
   at 64 and the fix applied a literal increment. A corrected comment that is still untrue.
2. `docs/guide/raptor.md`'s Ingestion-Options and Retrieval-Options samples still call `UseRaptor`
   without `leafStorePath` under `Corpus` scope, so copying them throws. Every nuget-shipping README
   is clean; these two guide blocks are not.
3. `docs/reference/opentelemetry.md` lists `ragnet.raptor.build` unconditionally, but
   `RaptorTreeRebuilder`'s path emits no span.
