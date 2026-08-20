# Session State

**Last updated:** 2026-08-20
**Written by:** `project-orchestration` — first `STATE.md` this project has had. Milestones 1–5 ran
without one, which is why every session so far re-derived its position from `ROADMAP.md` and
`MILESTONE.md` and twice acted on a debt that had already closed.

## Current Position

**Milestone:** 6 — Hardening & v1.0 — Battle-Tested (active since 2026-08-15)
**Phase:** 6.2.1 — Retrieval & Answer Sweep (active since 2026-08-20)

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

- **Does 6.1's live-service recording gate v1.0, or does `<VerifiedByReason>` where no credentials
  exist?** The operator's, from the 2026-08-15 re-plan, still open. The phases carry the note's
  recommendation (a reason suffices) until it is decided. This is the one thing standing between
  6.1 and a closeable state, and therefore between the milestone and its tag.
- **Where local search's yes/no abstention comes from.** It commits on 8.8% of comparison and 4.3%
  of temporal questions, while global search scores 0.4953 and 0.3928 on the same ones. A
  characterisation nobody has explained; it needs a home in 6.2.1 or an explicit deferral.
- **#298 — graph store backends beyond SQLite.** Recorded answer: *not yet*, and weaker now than
  when asked. Both costs once attributed to storage were a missing index and a per-document
  recompute, fixed without changing engines. Concurrency is the only surviving argument and nobody
  has stated that requirement.

## Blockers

- **6.1 is blocked on accounts, not on work.** The harness works as of #290; 1 of 19 cassettes is
  recorded (GitHub, unauthenticated, 17 KB). #283 carries the corrected instructions for the
  remaining 18 and is marked help-wanted. No amount of local effort moves this.
- **The #300 follow-up measurement needs an idle machine.** Three timing runs on 2026-08-17
  disagreed by 6× on identical inputs, so no coefficient was claimed anywhere. It is still
  outstanding: the split of `BeirRunBudget`'s 22 m 18 s graph construction between LLM extraction,
  the `O(occurrences²)` description rewriting, and the recompute #302 debounced.

## Recommended Next Step

**RAPTOR under the Real protocol** — 6.2.1's next thread, chosen 2026-08-20.

RAPTOR indexes synthetic summaries beside real chunks in one store, which is structurally the same
mistake #247 measured at −0.043 nDCG and −0.21 answer accuracy in the graph path. #247's issue text
predicted it would take the same fix, and that fix is now built and measured once: the
discriminator, over-fetch-and-drop, and the separate-store option all exist as worked examples. The
thread needs a design spec first — none exists — then a plan, then one real run on a real corpus
differenced against a control.

**Also now unblocked, and cheap:** deleting `GraphLocalSearchBehavior` and `PageRankWeight`. They
were kept alive deliberately — `[Obsolete]` would have been a build error under
`TreatWarningsAsErrors` across 17 files, and deleting them would have made three pinned figures
unreproducible — on the stated condition that 6.x.7 publish the replacement figure. It published on
2026-08-20.

## Working State

**Branch:** `chore/roadmap-refresh-6-2-1`, cut from `main` at `3f5d14fb`.
`bench/graphrag-localspec-measurement` is merged and safe to delete.
**Tree:** the two state files above, nothing else.
