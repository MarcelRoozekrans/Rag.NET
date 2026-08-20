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

**Phase 6.2.3 — Corpus-Level RAPTOR (#331).** Brainstorm the fix design, then plan, then build. It
is architectural: it needs a new persistent store for leaf embeddings, so it does not skip
brainstorming.

**How this became the next step, because the order was deliberately reversed.** The thread started
as "RAPTOR under the Real protocol", 6.2.1's next measurement. Reading the package before spending
anything found three shipped defects, and the largest is structural: RAPTOR builds its tree **per
document**, not over the corpus, so it does not implement the paper's central mechanism. The design
at `docs/superpowers/specs/2026-08-20-raptor-real-protocol-design.md` first deferred that fix on a
measured trigger, following #247's measure-then-fix order.

**The operator reversed it on 2026-08-20** — *"fix it first before spending again"* — on the
grounds that the defect was established by reading `IngestionContext`, not inferred from a figure,
so a paid sweep would buy evidence for something already known. The reversal costs a pinned figure
for the shipped state, and **keeping both tree scopes selectable is what gives it back**: one later
run prices old and new together, per #323's precedent for keeping `GraphLocalSearchBehavior` alive.

**So 6.2.1's RAPTOR measurement is not cancelled — it is queued behind 6.2.3**, and its design spec
already exists and stays valid apart from the ordering in §2.

**Also now unblocked, and cheap:** deleting `GraphLocalSearchBehavior` and `PageRankWeight`. They
were kept alive deliberately — `[Obsolete]` would have been a build error under
`TreatWarningsAsErrors` across 17 files, and deleting them would have made three pinned figures
unreproducible — on the stated condition that 6.x.7 publish the replacement figure. It published on
2026-08-20. *(Note the tension with 6.2.3's decision to keep both RAPTOR scopes: the same
reproducibility argument that retired these members only once their replacement figure existed is
why RAPTOR's per-document path must not be deleted before its replacement figure exists.)*

## Working State

**Branch:** `chore/roadmap-refresh-6-2-1`, cut from `main` at `3f5d14fb`. Six commits, nothing
pushed, no PR open. `bench/graphrag-localspec-measurement` is merged and safe to delete.

**On the branch:** the state-file refresh; `docs/superpowers/specs/2026-08-20-raptor-real-protocol-design.md`
(6.2.1's measurement, now queued behind 6.2.3); the #331 disposition; phase 6.2.3; the 6.1
postponement and the decision that closed with it; and
`docs/superpowers/specs/2026-08-20-corpus-level-raptor-design.md` (6.2.3's design).

**Filed this session:** #331 (per-document tree scope) and #332 (summary chunks collide on
`ChunkIndex` across tree levels — shipped, fires at default settings, independent of #331).
