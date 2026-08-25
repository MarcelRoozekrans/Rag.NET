# Session State

**Last updated:** 2026-08-25 (6.2.5-6.2.11 merged; RAPTOR Task 5 ran and reversed the pilot's headline)
**Written by:** `project-orchestration` — first `STATE.md` this project has had. Milestones 1–5 ran
without one, which is why every session so far re-derived its position from `ROADMAP.md` and
`MILESTONE.md` and twice acted on a debt that had already closed.

## Current Position

**Milestone:** 6 — Hardening & v1.0 — Battle-Tested (active since 2026-08-15)
**Phase:** 6.2.1 — Retrieval & Answer Sweep (active; its RAPTOR measurement is at Task 5).

**2026-08-25 moved five phases.** All verified on `main` by content rather than by a PR's MERGED
label:

| Phase | State |
| --- | --- |
| 6.2.5 — contract defects | complete, #372 / #373 / #374 |
| 6.2.6 — package boundaries | complete, #376 |
| 6.2.7 — named pipelines | complete, #381 |
| 6.2.8 — requested DX | complete, #378 (three of four items; #353 split into 6.2.10) |
| 6.2.9 — `Umap.Fit` at corpus scale | complete, #382 |
| 6.2.10 — vector-store initialisation | complete, branch `feat/353-vector-store-init` |
| 6.2.11 — HTML structure and a Guid seam | pending, added 2026-08-25 |

**Issue sweep, 2026-08-25.** Every open issue checked against `main` by content. **#365** (Tool
message) and **#354** (Azure Document Intelligence cassette) were done and are now closed with the
evidence. **#355** is half done — `Failed` shipped, fail-fast never implemented or answered, and the
issue now carries a question rather than an assumption. **#328 is correctly open**: its commit title
says `(#328)` but the merged fix was `KNearestNeighborsCount`, and the semantic ranker it actually
asks for was deliberately split off pending the score-scale decision. A commit naming an issue is
not evidence the issue is done.

**The roadmap had all of 6.2.5, 6.2.6 and 6.2.8 still marked `pending` while their code was already
on `main`** — corrected 2026-08-25. Statuses are written when a phase is planned and nobody is
editing this file at the moment its PR merges, which is the same failure the Working State branch
field has now had three times.

**Last completed:** **Phase 6.2.9 — `Umap.Fit` at Corpus Scale** (#348), built 2026-08-25.
Measured before changing anything, which is what makes the rest of it quotable: the kNN graph is
**92% of `Umap.Fit`'s time and 98% of its allocation**, so #348 named the right target. Bounded
k-selection replaced the full sort and the row loop parallelises above 512 rows —
**5.2× faster, ~729× less allocated, Gen0/1/2 all to zero**. Two runs per state on an idle machine;
the table is in `ROADMAP.md`'s 6.2.9 entry.

**It also corrected two claims in its own issue.** #348 argued from the ~1,368 s corpus tree build
that this was "real time rather than a micro-optimisation" — the level-1 reduction is ~82 s of that,
about **6%**, since the tree build is dominated by LLM summarisation. And the sort-to-selection
change everyone would call the headline bought **21%** on its own; the distance loop it does not
touch is the real cost, and parallelising *that* bought the 4×.

**Earlier in Milestone 6.2:** **Phase 6.2.3 — Corpus-Level RAPTOR**, merged 2026-08-21 in #340
(squash `c461475d`). Seven tasks, each independently reviewed, plus a whole-branch review and one
fix wave.

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

- ~~Does #345's average-only cluster bound need a post-assignment split?~~ **Answered 2026-08-23 by
  measurement: no.** The first corpus-scale RAPTOR tree (17,648 chunks, 183 summaries, depth 3,
  1,368 s) puts 549 chunks in its largest level-1 cluster against a mean of 99.7 — **5.51x
  imbalance**, so the floor demonstrably does not bound the maximum. It still fits: ~57k tokens
  against 128k, 2.25x headroom, 44% of the imbalance budget consumed. The split stays unbuilt on
  evidence. **The user-facing consequence is that raising `TargetClusterSize` has ~2.25x of room,
  not the ~12.6x "average 100 against a 128k context" implies** — recorded in
  `docs/guide/raptor.md`'s Cluster Size section.

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
- ~~**The #300 follow-up measurement needs an idle machine.**~~ **Done 2026-08-18; this entry was
  stale for a week.** The split is recorded in `BeirRunBudget`'s `GraphRag` cell: measured over the
  real corpus at 50/100/200/400/609 documents, **twice**, with the 609-document graph reproducing
  exactly (62,392 entities, 147,021 relationships). **The recompute was not where the time went** —
  Leiden + PageRank + the score write-back is **2.7 s**, at 0.044 ms per entity, a coefficient stable
  within 6% across both runs and all five sizes. What #302's debounce removed, projected from that
  coefficient, is **13.6 minutes** summed over 609 documents. Extraction and report generation are
  I/O-bound cache replays and no figure is quoted for them, because 152.9 s cold against 18.7 s warm
  is a page-cache artefact of reading 35,176 files rather than a property of extraction.

## Recommended Next Step

**Decide what to do about the corpus-scope default** — see OPEN DECISION below. Task 5 measured it
and the answer was not the one #331 assumed; the decision is a design call, not a measurement one,
and nothing has been changed on the strength of a single corpus.

**The measurement work still open in 6.2.1** is #176 (78.8% singleton communities, worse than
filed) and the 17 Done sections that need a pinned figure with a control.

**There is no measurement run set up and waiting.** RAPTOR Task 5 is done, and the #300 follow-up
was done on 2026-08-18 (see Blockers). The 17 Done sections that still need a pinned figure with a
control mostly need a **new harness arm built first** — there is no `SemanticChunking` or
`LateChunking` protocol in `BeirProtocol`, so those cannot be run, only written. The bottleneck for
6.2.1 is engineering now, not compute.

Historical context for the arms, retained: Historical context for the arms, retained: `RaptorOptions.MaxClusters` defaults to `null`, so before
#345's fix `SelectClusterCount` capped every level at `SelectK(maxK: Min(count, 10))` regardless of
corpus size — over MultiHop-RAG's 17,648 chunks the largest level-1 cluster held at least 1,765
chunks (≈183k tokens, uncapped in `ConcatenateChunkTexts`) against `gpt-4o-mini`'s 128k context, so
the corpus tree could not be built at the shipped default. **#345 merged to `main` 2026-08-22 in
#351 (`bb4c11c7`), verified on `main` by content — `TargetClusterSize` is present in
`RaptorOptions.cs` there — rather than by the PR's MERGED label.** `TargetClusterSize` floors the
cluster count; see `docs/guide/raptor.md`'s Cluster Size section for what it guarantees (an average
bound, not a per-cluster maximum). Task 4's pilot is the next thing to run.

**6.2.4 completed 2026-08-21** (#344), so `raptorboost` now measures a `Boost` that works.

Three things govern the run, all in the plan:

1. **`Corpus`-scope ingestion bypasses `RaptorIngestionBehavior.HandleAsync` entirely, then
   `RaptorTreeRebuilder.RebuildAsync()` is called exactly once.** Suppressing the growth debounce
   and letting ingestion run normally was the first approach and it does not work for a bulk load —
   the debounce's baseline resets to whatever the corpus held at the last build, so at the shipped
   `CorpusGrowthThreshold = 0.10` a 609-article corpus still triggers a rebuild partway through, and
   the trigger point depends on document order. `RaptorRun` instead writes each document's chunks
   straight to the leaf store and the vector store during ingestion, and the single rebuild after
   ingestion finishes is the only tree this run can produce. A fast-tier test asserts
   `RaptorRun.CorpusRebuildCount == 1` (not `TreeBuildCount` — the member is named
   `CorpusRebuildCount`) so a regression fails in milliseconds rather than in dollars; because that
   counter is set to 1 beside the one `RebuildAsync` call by construction, `LeafCount` and
   `SummariserCalls` are what actually prove nothing rebuilt along the way.
2. **Task 4's gate is real.** If `raptorfiltered − dense` is not ≈ 0 the corpora diverged and no
   figure means anything — stop, having spent a pilot rather than a sweep.
3. **`raptorcorpus` is RAPTOR's result, not `raptor`.** Publishing the per-document figure would
   repeat 5.2's misattribution, which cost three weeks and a revised published finding.

**Also unblocked and cheap:** deleting `GraphLocalSearchBehavior` and `PageRankWeight`.

### Task 4 completed 2026-08-24 23:49 and the gate HELD.

**`raptorfiltered − dense = +0.0000` on all three scoring rules**, confirmed twice — the gate-only
run at 17:59 and the full five-arm run at 23:49. The corpora did not diverge, so the pilot's figures
measure RAPTOR rather than a setup fault. Merged 2026-08-25 in **#370**; full account in
`docs/plans/2026-08-21-raptor-pilot-notes.md`.

**The finding to carry into Task 5: `raptorcorpus − raptor = +0.0000`.** 6.2.3 shipped corpus-level
clustering as a *breaking* change, and at 50 queries it bought exactly nothing over the per-document
tree. Task 5's 2,556 queries is what decides whether that survives — the pilot's type mix is skewed
(11 temporal questions scoring 0.0000 in every arm, 6 nulls).

**Task 5 is costed from Step 4's counters rather than extrapolated: ~10,000 new generations, zero
tree-construction cost — both trees are cached — and roughly 8 hours.** That 8 hours is an estimate
built on a rate observed during *tree summarisation*, whose prompts are much larger than answer
generation's; it is not a throughput measurement of the work Task 5 actually does.

**No wall-clock figure from the pilot is quotable.** Two orphaned runners contaminated it — the real
pilot got 139 CPU-seconds in 58 minutes while they held 5.6 CPU-hours each. The gate is an accuracy
difference and Step 4's deliverables are counts, so both survive; the timing does not.

**Three defects were found, two of them in the plan itself:**

1. **The plan's `dotnet test --filter` is silently ignored.** This project sets
   `TestingPlatformDotnetTestSupport` with `xunit.v3`, so the VSTest filter is discarded and **all
   25 test classes run** — with `RAGNET_BEIR_LONG_RUNS=1` and `RAGNET_GRAPHRAG_ANSWERS_GENERATE=1`
   set, which unlocks every expensive test in the project. Nothing fails; a run was observed
   executing library-comparison sweeps instead of RAPTOR. **Fixed in the plan** — both Task 4 and
   Task 5 now invoke the runner directly with `-class '*BeirGraphRagAnswerTests*'`, and verify it
   selects 5 methods before running.

2. **The plan's cost model counted only answers.** It estimated "50 queries × 4 arms, most hitting
   cache" (~250 calls) and **omitted tree construction entirely**. The `raptor` arm is the
   per-document control, so it builds **609 trees**, every level an LLM summarisation: 4,739 calls
   in 5 hours at a steady 21/min, with ~15-20 hours and order $10-20 still to go. **Fixed in the
   plan**, and `raptor` is now dropped from Task 4's pilot — the gate needs corpus-scope arms only,
   and the corpus tree is already cached.

3. **Killing a run by `dotnet`/`testhost` does not stop it.** The process is named after the
   assembly. Two "stopped" runs survived and were found 90 minutes later at 5.6 CPU-hours and
   6.2 GB *each*, starving their replacement — which managed 139 CPU-seconds in 58 minutes. This
   was already recorded in memory before it happened, and happened anyway.

**This is not #333 recurring, and that was checked rather than assumed.** `SelectClusterCount`
computes `k = Min(raw, count - 1)` and returns null at `k <= 1`, so every level shrinks strictly and
the loop provably terminates. The `k >= count` degenerate guard remains unreachable. The clustering
is correct; there is simply far more legitimate work than the plan priced.

**Next step is the gate, and it is cheap:** run Task 4 with
`dense,raptorcorpus,raptorfiltered,raptorboost` and check `raptorfiltered − dense ≈ 0`. Only after
it holds does the ~15-20 hour per-document `raptor` build earn its place as a scheduled job.

### Task 5 ran 2026-08-25 and reversed the pilot's headline.

**The validation gate held exactly at full scale.** `raptorfiltered` reproduced the dense arm to
four decimals on all three rules — 0.3499 / 0.2603 / 0.3242, the figures pinned 2026-08-15 — so the
corpora did not diverge and the numbers below measure RAPTOR rather than a setup fault.

| arm | paper | raw | strict | inference |
| --- | --- | --- | --- | --- |
| `raptor` (per-document control) | **0.3734** | **0.2860** | **0.3348** | **0.8309** |
| `raptorcorpus` (shipped default) | 0.3588 | 0.2656 | 0.3322 | 0.7831 |
| `raptorfiltered` (the gate) | 0.3499 | 0.2603 | 0.3242 | 0.7721 |
| `raptorboost` | 0.3450 | 0.2634 | 0.3086 | 0.7757 |

Over the **2,255 judged queries** — the denominator every other pin uses; the 301 nulls are scored
separately as abstention.

**`raptorcorpus − raptor = −0.0146 paper, −0.0204 raw, −0.0027 strict.` Corpus-level clustering is
worse than the per-document tree it replaced.** McNemar over the paired judged queries: paper
p=0.0247 (85 corpus wins against 118 per-document), raw p=0.0006 (62 against 108), strict p=0.7372.
Two of three rules significant, all three signed the same way.

**The 50-query pilot put this at +0.0000 and was underpowered** — which is exactly what Task 5
existed to find out, and the reason the plan insisted on the full sweep rather than trusting the
pilot's headline.

**The gap is inference queries**: 0.7831 against the control's 0.8309, while comparison and temporal
are flat. That is the *opposite* of #331's rationale — corpus-spanning summaries were meant to help
the multi-hop case they measurably hurt.

**`raptorboost − raptorcorpus` = −0.0137 paper (p=0.0073), −0.0235 strict (p=0.0000).** 6.2.4 fixed
`Boost` so it could promote summaries at all; this is the first measurement of what it does once it
works, and it trades accuracy for abstention (51.8% correct null-abstention, the best of the four).

**Cost and shape:** 58 m of generation after a 28 m I/O-bound load, ~5,600 new answers. The plan's
~8 h estimate came from a rate observed during *tree summarisation*, whose prompts are much larger;
the pilot notes flagged that uncertainty explicitly and it was right to.

## OPEN DECISION — what to do about the corpus-scope default

`RaptorTreeScope.Corpus` is the shipped default and a breaking change (#331, phase 6.2.3). It does
not buy accuracy on this corpus; it costs a little. **Nothing has been changed on the strength of
this** — it is one corpus, and the strict rule is a wash. The options, none taken:

1. Revert the default to `PerDocument` and keep `Corpus` opt-in.
2. Keep the default and document the measured cost.
3. Measure a second corpus before deciding, since a single dataset reversing a design decision is
   thin evidence — MultiHop-RAG rewards per-document locality by construction.

## Working State

**Branch:** `chore/roadmap-6-2-11`, cut from `main` at `685ca037`. Bookkeeping only. Both of the
day's feature branches merged and were **verified on `main` by content**, not by their MERGED
labels — `RagPipelineFactory.cs` exists (#381) and `Umap.NearestNeighborsOf` is present (#382).

**Tasks 1-4 of `docs/plans/2026-08-21-raptor-real-protocol-implementation.md` are on `main`** —
Tasks 1-3 in #347 (`c2d83075`), Task 4 in #370 (`2de9c5c9`); verified by content, not by a MERGED
label. **Task 5 is unblocked and not started.**

**This field has now named a stale branch three times** (`chore/complete-phase-6-2-3`,
`bench/raptor-measurement`, `bench/raptor-real-protocol-measurement` — each still named here after
its PR merged). It goes stale at exactly the moment its branch merges, which is the moment nobody is
editing this file. **Re-read it against `git branch --show-current` before trusting it**, and treat
a mismatch as evidence the rest of this file may also predate the last merge — on 2026-08-25 it did,
by five phases.

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
