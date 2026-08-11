# Milestone 5: Evaluation Depth

**Status:** active
**Started:** 2026-08-10 (first phase delivered; opened formally 2026-08-11)

Completed milestones are archived under `docs/planning/milestones/`. Milestone 4 was archived
there on 2026-08-11, after being audited against its definition of done —
`docs/plans/2026-08-11-milestone-4-audit.md`, verdict PASS on all six criteria.

> **This file drifted twice before, and the second time is why the close was audited rather than
> asserted.** It went unrewritten when Milestone 4 opened on 2026-08-02, and then declared itself
> `active` for nine days after the ROADMAP recorded that milestone complete — while this milestone
> had already delivered two phases and shipped 0.1.0 to nuget.org. Two sources of truth
> disagreeing with one silently stale is the same shape as the changelog that claimed a release no
> tag ever matched, corrected the same day. **The ROADMAP is authoritative for the DoD below; the
> two must agree, and when they do not, this file is the one that is wrong.**

## Goal

Extend the evaluation programme along the axes Milestone 3 deliberately did not take: what each
library **costs** rather than what it scores, multi-hop retrieval, graded relevance and the
datasets declined at Milestone 3's close, and the two IR metrics `IrMetrics` does not compute.

## Definition of Done

Authoritative copy in the ROADMAP's Milestone 5 section. Written in the falsifiable style Phase
4.0 established — every criterion can be false, and something checks it.

- [ ] **Phases 5.1–5.4 complete.** 5.5 deliberately schedules nothing and is outside this box by
      design.
- [x] **No cross-ecosystem latency figure is published without the confound statement beside it.**
      *(Met 2026-08-10; re-verified 2026-08-11 when the table was republished after the
      dense-search optimisation. The latency table is cross-ecosystem and carries the
      default-in-memory-store caveat inline plus the startup exclusion; indexing publishes as two
      per-ecosystem tables labelled non-comparable, with the reason stated between them.)*
- [ ] **`IrMetrics`' graded gain has scored a real dataset** — at least one dataset whose qrels
      carry a grade above 1, through `Evaluate`, with the FiQA-qrels contradiction settled by
      reading the cached `qrels/test.tsv`. Fixture-only exercise of `2^rel − 1` fails this.
- [ ] **Every dataset this milestone lands carries the full Milestone 3 per-dataset checklist** —
      descriptor, `BeirRunBudget` timing, a revision-pinned published reference where one exists,
      a licence determination from upstream rather than a mirror, and every published figure
      pinned in `BeirReproduction` at ±0.005 on the fast tier.
- [ ] **All test projects passing; solution builds 0 warnings / 0 errors from a clean restore.**
      A close-time check: true on 2026-08-11, and to be re-run when 5.2 and 5.3 land.

## Phases

| Phase | Name | Status |
|---|---|---|
| 5.1 | Library Performance Comparison | **complete** 2026-08-10 — full matrix gated and published |
| 5.1.1 | The Cost Figure Read Back | **complete** 2026-08-11 — optimised, verified and republished |
| 5.2 | Multi-Hop Retrieval | pending |
| 5.3 | Deferred Datasets — NFCorpus, TREC-COVID, EnronQA | pending |
| 5.4 | Precision@k and MAP | **implemented** 2026-08-09 (#75) |
| 5.5 | Tier 3 Suites | recorded — deliberately not scheduled |

## Known debt carried into this milestone

- **A single-session five-entrant cost sweep is owed.** The published latency table's .NET rows
  were re-measured on 2026-08-11 after the optimisation, in two idle sessions, while the Python
  rows are from 2026-08-10. §2.2 wants one machine in one session. The page labels the split and
  says why it is tolerable — Semantic Kernel's unchanged code moved only ±20% between the two
  sessions while the control moved 4–5× — but the sweep collapses the union ranges back to
  three-run spreads and removes the caveat. Recorded in Phase 5.1.1.
- **5.2 and 5.3 are the milestone's remaining substance**, and 5.3 is what would satisfy the
  graded-gain criterion, since it brings the datasets whose qrels carry grades above 1.

## Explicitly not in scope

- **v1.0.** The tag belongs to Milestone 6, moved there on 2026-08-03 so it follows the hardening
  that finds what a green build does not. 0.1.0 shipping on 2026-08-11 does not change that.
- **Anything on the "Beyond v1.0: Recorded, Not Scheduled" list.** That section exists so ideas
  can be kept without being dressed up as commitments.
