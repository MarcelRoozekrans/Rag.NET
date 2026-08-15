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

- [ ] **Phases 5.1–5.4 complete, sub-phases included.** 5.5 deliberately schedules nothing and is
      outside this box by design. *(Was ticked 2026-08-15, when 5.2's comparative run finished and
      closed the last open phase. This box stayed open for months on the half of 5.2 that was
      expensive, deliberately: closing it on the cheap half — "does GraphRAG function" — would have
      been the same move as the `✅ Done` that stood over a package nothing had ever executed. The
      run answered "does it help", and it answered **no**: GraphRAG's local search scored
      nDCG@10 = 0.56897 against **0.59658** for the candidate-set control that was handed the same
      dense top-500 over the same 321,151-chunk store, so the graph behaviour costs **−0.02761**
      where the only variable is the behaviour itself. A finding is a completion; the box is about
      whether the question was asked, not about which way it came out.
      **Re-opened the same day, on purpose, when Phase 5.2.1 was added** to act on that run — #232
      decomposes the deficit the run measured, #174 re-takes the wall clock it was told to, #226
      fixes the sequential report loop it had to pay for. The operator's call was to finish this
      milestone whole rather than carry three open issues from an evaluation milestone into a
      hardening one. **5.2.1 closed 2026-08-15** with #174, the last of its three PRs — #232 measured, #226 done, #174 done — **and the box stays open for 5.2.2**, added the same day at the operator's request to score answers rather than rankings; it re-ticks when 5.2.2 closes.)*
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
      *(Met for TREC-COVID, 2026-08-12: descriptor with every count taken from the archive;
      `BeirRunBudget` timing measured (1 h 3 m + 47 m) rather than derived, across all ten
      protocols; published reference pinned to MTEB's `ndcg_at_10` 0.47232 at dataset revision
      `bb9466ba`; licence read from `allenai/cord19` and NIST directly, with the Hugging Face card's
      contradicting `cc-by-sa-4.0` tag explicitly rejected; figure pinned in `BeirReproduction` at
      `Tolerance = 0.005`.
      Re-checked and still met for **MultiHop-RAG**, 2026-08-12: 609 documents, 2,556 queries of
      which 2,255 are judged, and 5,908 qrels rows, every count measured from the pinned revision
      `71ac0d0b…` rather than taken from the paper; timing measured (600.2 s Real, 41.1 s parity
      control) and flagged an upper bound because the machine was under load — **re-taken idle and
      cold, twice, 2026-08-15 (#174): 343.5 s and 388.5 s Real, figures identical**; licence `odc-by` read
      from the authors' own Hugging Face repository; nDCG@10 = 0.63967 pinned in `BeirReproduction`.
      **The "published reference where one exists" clause is what this dataset tests**, and the
      answer is that none exists for our configuration — the paper's Table 5 has no MiniLM row and
      reports MAP/MRR/Hit rather than nDCG, MTEB does not carry the dataset at all — so the target
      holds `double.NaN`, which admits no measurement rather than quietly admitting any. Re-check
      this box if NFCorpus or EnronQA later land.)*
- [ ] **All test projects passing; solution builds 0 warnings / 0 errors from a clean restore.**
      A close-time check: true on 2026-08-11. 5.3, both halves of 5.2 and all of 5.2.1 have landed
      since, and none of it has been re-checked from a clean restore, so this stays open until the
      milestone closes. **With 5.2.1 closed this is again the milestone's only open box**, and it is
      the close audit itself: a clean restore, a 0/0 build and every test project, on the `main`
      that holds #234, #235 and #237.

## Phases

> **This table names the issues each phase is waiting on, and it did not before.** 5.2 sat at
> `partial` for three days while #172 and #173 — the two things it was actually waiting on — were
> invisible from here, so "what is left" could only be recovered by reading the ROADMAP entry. The
> issue column is not decoration; a phase that is not complete must say what would complete it.

| Phase | Name | Issues | Status |
|---|---|---|---|
| 5.1 | Library Performance Comparison | — | **complete** 2026-08-10 — full matrix gated and published |
| 5.1.1 | The Cost Figure Read Back | — | **complete** 2026-08-11 — optimised, verified and republished |
| 5.2 | Multi-Hop Retrieval | #168 (functions), #172 (real reports), #173 (comparative) — all resolved | **complete** 2026-08-15 — MultiHop-RAG landed and measured; GraphRAG proved to function and six library defects fixed; and the comparative run answered "does it help" with **no**: nDCG@10 0.56897 against 0.59658 for the candidate-set control, **−0.02761**, needing #231 to be reproducible |
| 5.2.1 | The GraphRAG Deficit Read Back | #232 (decompose the −0.043) — **measured**; #226 (parallel reports) — **done**; #174 (cold wall clock, twice) — **done** | **complete** 2026-08-15 — all three landed the day the phase was added, so the milestone finishes whole. #232: depth costs **0.00000** (the depth control reproduces the Real leg to five decimals), so the −0.04309 is store pollution entire. #226: `CommunityReportConcurrency` (default 4), deterministic by construction, measured 4.62 → 1.13 → 0.63 s per report at 1 → 4 → 8 in flight with zero retries. #174: Real leg 343.5 s and 388.5 s idle-cold against 600.2 s under load, figures identical, `FitsTheNightly` decided and kept false because the nightly runs on a 4-vCPU runner this figure says nothing about. |
| 5.2.2 | Does GraphRAG Help Answers? | — (design: `docs/plans/2026-08-15-graphrag-answer-level-evaluation.md`; #239 informs arm B′) | **pending** — added 2026-08-15 at the operator's request after 5.2's finding. Three arms (dense, GraphRAG local as shipped, GraphRAG global) answer all 2,255 judged queries with one model, judged by the paper's own `qa_evaluate.py` rule against the gold answers; pilot 100 queries (~$1) before the full run ($20–30 derived) |
| 5.3 | Deferred Datasets — NFCorpus, TREC-COVID, EnronQA | — | **complete** 2026-08-12 — TREC-COVID landed; NFCorpus declined on its licence; EnronQA blocked, undeclared |
| 5.4 | Precision@k and MAP | #75 | **implemented** 2026-08-09 |
| 5.5 | Tier 3 Suites | — | recorded — deliberately not scheduled |

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
  graded-gain criterion.~~ **5.3 closed 2026-08-12.** TREC-COVID landed and met the graded-gain
  criterion; NFCorpus was declined, because upstream asks non-academic users to contact the author
  rather than offering a licence to read, and TREC-COVID had already taken the biomedical and
  graded-relevance ground it was wanted for; EnronQA stays blocked, its licence re-verified absent
  against the HuggingFace API the same day. **5.2 closed 2026-08-15.** Its first half landed
  2026-08-12 — MultiHop-RAG in, GraphRAG run end to end for the first time — and its second half,
  the comparative run over the whole corpus, finished on 2026-08-15 in 43 m 29 s after the roughly
  41,000 extraction calls and 3,587 report calls were bought and cached. **The answer is that
  GraphRAG does not help on this corpus; it hurts**, by −0.02761 of nDCG@10 against the
  candidate-set control. The full account, including why the control rather than the dense 0.63967
  is the comparison that carries the claim, is in `BeirReproduction`'s `multihop-rag` / `GraphRag`
  cell and in the ROADMAP's Phase 5.2 entry. **The run left three things owed, and they are
  scheduled rather than carried: Phase 5.2.1**, added 2026-08-15 — #232 (the −0.04309 between the
  control and the Real leg is store pollution or candidate depth, and one twenty-minute run without
  an LLM says which), #174 (the 600.2 s Real wall clock was taken on a loaded machine and
  `FitsTheNightly` cannot be reconsidered until it is taken cold, twice), and #226 (community
  reports are generated one at a time — 3,587 round trips on this corpus — which is the seventh
  library defect the run surfaced and the only one not fixed in-phase).
- **Running GraphRAG once cost six library fixes, and the tests are the finding.** `6f86f0a7`,
  `e9178aee`, `929d45a3`, `46ff566b`, `c34d270e`, `49da36ae` + `2abc17e4`, all in packages published
  at 0.1.0. Three tests had been written *around* the broken behaviour rather than through it —
  `Detect_ThreeCliquesWithBridges_FindsThreeCommunities` asserted `Count >= 1` under a comment
  conceding "Leiden may merge communities", and two assertions in the new guard were weakened on
  first writing to accommodate degenerate output. Each was defensible alone; together they held a
  green suite over a pipeline with 90% of the graph in one community and a global search that never
  executed. Full account in the ROADMAP's Phase 5.2 entry, which also carries the five things that
  phase recorded and did not fix.
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
