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
- [x] **`IrMetrics`' graded gain has scored a real dataset** *(Met 2026-08-12. TREC-COVID's qrels
      carry 10,456 rows at grade 1 and **14,217 at grade 2**, counted from the downloaded archive,
      and its parity leg ran the full embed → store → retrieve path through `Evaluate` to
      nDCG@10 = 0.45427. The FiQA contradiction is settled and it settled against FiQA: all 17,110
      of its judgements are exactly 1, so no FiQA run could ever have exercised `2^rel − 1` beyond
      its rel=1 case — which is why this criterion needed a new dataset rather than a new run. Two
      TREC-COVID rows carry grade −1; they are harmless because every `IrMetrics` consumer gates on
      `grade > 0`, checked rather than assumed.)*
- [x] **Every dataset this milestone lands carries the full Milestone 3 per-dataset checklist**
      *(Met for TREC-COVID, the only dataset landed so far, 2026-08-12: descriptor with every count
      taken from the archive; `BeirRunBudget` timing measured (1 h 3 m + 47 m) rather than derived,
      across all ten protocols; published reference pinned to MTEB's `ndcg_at_10` 0.47232 at dataset
      revision `bb9466ba`; licence read from `allenai/cord19` and NIST directly, with the Hugging
      Face card's contradicting `cc-by-sa-4.0` tag explicitly rejected; figure pinned in
      `BeirReproduction` at `Tolerance = 0.005`. Re-check this box if NFCorpus or EnronQA later
      land.)*
- [ ] **All test projects passing; solution builds 0 warnings / 0 errors from a clean restore.**
      A close-time check: true on 2026-08-11, and to be re-run when 5.2 and 5.3 land.

## Phases

| Phase | Name | Status |
|---|---|---|
| 5.1 | Library Performance Comparison | **complete** 2026-08-10 — full matrix gated and published |
| 5.1.1 | The Cost Figure Read Back | **complete** 2026-08-11 — optimised, verified and republished |
| 5.2 | Multi-Hop Retrieval | pending |
| 5.3 | Deferred Datasets — NFCorpus, TREC-COVID, EnronQA | **TREC-COVID complete** 2026-08-12; NFCorpus blocked on a licence decision; EnronQA blocked (no declared licence) |
| 5.4 | Precision@k and MAP | **implemented** 2026-08-09 (#75) |
| 5.5 | Tier 3 Suites | recorded — deliberately not scheduled |

## Known debt carried into this milestone

- ~~**A single-session five-entrant cost sweep is owed.**~~ **Paid 2026-08-12.** All twelve cells,
  five entrants interleaved, three gated repeats each, on one machine in one session. The union
  ranges are gone and every published figure is now a three-run spread; the confound caveat is
  removed from `docs/reference/library-comparison.md`. Semantic Kernel — unchanged code, so a pure
  read on session conditions — landed inside both earlier sessions' envelopes and slightly below
  them on FiQA, so the sweep is not merely single-session but measurably no noisier than the
  sessions it replaces. It also corrected a claim: FiQA's control indexing reads 0.10–0.11 s rather
  than 0.11–0.19 s, so most of the "index construction got slower" delta was session noise, not the
  optimisation's cost.
  **It took five attempts, and the four discards are the useful part.** Two died of machine
  contention. The third died because a descriptor added for Phase 5.3 silently joined the
  comparison control's theory and hit a cold cache seven minutes in. The fourth is the one worth
  remembering: stopping the third did not stop the processes it had spawned, so two test runs
  contended for the same run files — and it still exited **0**, having written nine of twelve cells
  per repeat, missing the Semantic Kernel entrant entirely. A sweep that reports success while
  omitting the entrant that calibrates it is the inert-guard shape this repository keeps finding.
  Recorded in Phase 5.1.1.
- ~~**5.2 and 5.3 are the milestone's remaining substance**, and 5.3 is what would satisfy the
  graded-gain criterion.~~ **5.3 delivered TREC-COVID on 2026-08-12 and the graded-gain criterion
  is met.** What remains of 5.3 is blocked rather than pending: NFCorpus needs a licence decision
  that is the maintainer's to make ("academic use only" plus CC BY-NC), and EnronQA declares no
  licence at all. **5.2 is now the milestone's only remaining substance.**
- **TREC-COVID's agreement with published is the weakest of the four datasets and nothing is
  scheduled to look into it.** −0.01805 against a ±0.02 band, where the other three sit within
  +0.003. It passes, and it passes with 0.0023 to spare on a single run. Recorded here rather than
  in a completion note because "it went green" is exactly how a number like this stops being
  looked at.

## Explicitly not in scope

- **v1.0.** The tag belongs to Milestone 6, moved there on 2026-08-03 so it follows the hardening
  that finds what a green build does not. 0.1.0 shipping on 2026-08-11 does not change that.
- **Anything on the "Beyond v1.0: Recorded, Not Scheduled" list.** That section exists so ideas
  can be kept without being dressed up as commitments.
