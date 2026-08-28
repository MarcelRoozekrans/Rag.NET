# Session State

**Last updated:** 2026-08-27 (RAPTOR Task 6 merged in #412, and the pipeline-parity test's fast leg
built on `feat/pipeline-parity-test` — not yet merged)
**Written by:** `project-orchestration` — first `STATE.md` this project has had. Milestones 1–5 ran
without one, which is why every session so far re-derived its position from `ROADMAP.md` and
`MILESTONE.md` and twice acted on a debt that had already closed.

## Current Position

**Milestone:** 6 — Hardening & v1.0 — Battle-Tested (active since 2026-08-15)
**Phase:** 6.2.1 — Retrieval & Answer Sweep (active; RAPTOR Task 5 is done and pinned in #389,
**#176 closed 2026-08-26 in #405** and the **PageRank blend deleted 2026-08-27 in #408** — all four
named debts are closed and only the sweep itself remains). **RAPTOR Task 6 closed 2026-08-27 in
#412, so RAPTOR is the sweep's first completed technique** — measured, pinned, and now written down
at `VerifiedBy=benchmark`. **The pipeline-parity test's fast leg is built and green, 2026-08-27**,
satisfying the exit condition's *"the pipeline-parity test is in the fast tier"* clause; its real
leg exists but has never run on this machine (no ONNX model, no BEIR cache) and is verified by
reading only. Neither closes the phase — see `ROADMAP.md`'s 6.2.1 block for what still remains.

**2026-08-26 shipped 6.2.12 — the first external user's defects.** Seven merged PRs, all verified
on `main` by content. Its full record is in `ROADMAP.md`; the three findings worth carrying here:

1. **Silent data loss, live at shipped defaults.** `CleanupMode.Full` deletes what a run did not
   see. A provider listing failure was collected into `Errors` and dropped, so the entries behind
   it were never seen and were deleted as disappeared — **one failed sitemap page removed every
   document behind it, and the run reported success.** Fixed in #402. It is the same hazard #394
   guarded for `StopOnFirstError`, through a door that guard did not cover, so the single
   `stoppedEarly` bool became a `CleanupBlocked` reason: every way a run can fail to see an entry
   now has to be named rather than defaulting to "safe to delete".

2. **Two of the defects were caused by fixes earlier in the same phase.** #390's fix deadlocked
   Blazor (#396), and #396's fix still hung on unrelated host singletons (#400) because forwarding
   resolved *every* eligible root registration eagerly. Fixed in #403 by forwarding lazily. The
   trade was measured rather than argued: an instance descriptor is disposed 0 times by the child
   and a factory descriptor once, so laziness costs a second `Dispose` at shutdown for services the
   pipeline actually used — against a hang at startup.

3. **The reported "lock" was an unconditional sleep.** `AzureAISearchVectorStore.StoreAsync` ended
   with `await Task.Delay(1s)` — once per document, so a 500-page ingest spent over eight minutes
   asleep, buying nothing, since Azure gives no read-after-write guarantee at any fixed delay.
   Removed in #401 with a solution-wide sweep; waits that poll a real condition against a bounded
   timeout stayed.

**None of the four had a test, and the suite was green throughout** — the same shape as 6.2.3's
RAPTOR finding, where no test had ever built a tree deeper than one level. Every fix landed with a
test that fails against the previous code, mutation-checked with the mutation verified to compile
first.

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
| 6.2.11 — HTML structure and a Guid seam | complete, #385 / #386 |
| 6.2.12 — dogfooding defects | complete, #391 / #397 / #398 / #399 / #401 / #402 / #403 |

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

**Last completed:** **the pipeline-parity test's fast leg, 2026-08-27, on `feat/pipeline-parity-test`
(not yet merged)** — `OrderingEmbeddingGenerator`, `PipelineParity` and `PipelineParityTests`
compare a real `AddRagNet` pipeline against the harness's dense row with exact score equality; the
mutation check ran and failed with a named-rank, both-ids-and-scores message; the real SciFact leg
was written and reviewed but has never run on this machine and is verified by reading only. Before
it, **RAPTOR Task 6** (#412, 2026-08-27) — RAPTOR is the sweep's first completed technique,
measured, pinned, and written down at `VerifiedBy=benchmark`. Before that, **#176, answered
2026-08-26 and shipped in #405** — see Phase state below; the finding is that the singletons are
honest and the obvious fix would make the graph worse. Before it, **Phase 6.2.9 — `Umap.Fit` at
Corpus Scale** (#348), built 2026-08-25.
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

**Phase state: 6.2.1's four named debts are all closed.** #239 and #200 on 2026-08-17, #247 on
2026-08-18 (pinned at 0.3494 in #280), and **#176 on 2026-08-26 in #405**.

**#176 was answered by reading names, not by moving a number — and the answer is that it is not a
defect worth fixing.** The counts were already understood: 853 of 16,403 relationships (5.20%) are
dropped because an endpoint resolves to no extracted entity, stranding 123 entities that do have
edges, and 273 + 123 is **exactly** the pinned slice's 396 singletons. What nobody had checked is
what those endpoints are *called*. They are **565 distinct names**, and they are not entities the
extractor missed — `content policies` (10), `tasks` (10), `smart plug` (9), `handy tool` (8), `film`
(7), `ceremony` (6), `death` (5) — common nouns, mixed with paraphrases of things that *are*
extracted: `Falun Gong practitioners` beside the entity `Falun Gong`, `Rachel's husband`.

**That rules out the obvious fix.** Promoting an unresolved endpoint into an entity drives the
singleton share down while adding 565 junk nodes named after common nouns — a better-looking number
over a worse graph. **The singleton count is precisely the metric easiest to move without helping
anything**, which is the transferable part. Nothing was changed on the strength of it. Any real fix
belongs in the extraction prompt and must be measured against retrieval. The full-corpus 78.8%
(2,816 of 3,573) stands as a documented property rather than an open debt. Cost: zero model calls —
the extraction cache was replayed refuse-on-miss.

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

**~~RAPTOR Task 6~~ — DONE 2026-08-27. RAPTOR is the sweep's first completed technique.** The
ledger the Task 5 measurement earned is now written: `docs/guide/raptor.md` has a `## Measured`
section, `Rag.NET.Raptor.csproj` is `<VerifiedBy>benchmark</VerifiedBy>`, and
`docs/reference/features.md`'s RAPTOR row points at `MultiHopRagAnswerReproduction` instead of
saying *"Not yet `benchmark`"*. `dotnet build Rag.NET.slnx` 0 warnings; RepoConventions 94 passed,
0 failed.

**The two Phase 6.0 guards still report `[SKIP]`, and that is by design rather than a pass being
claimed for them.** `EveryDoneSectionSaysWhatExercisesIt` and `NoPackageStaysAtBareUnit` skip while
their allowlists are non-empty and fail on any *unlisted* violation — the "failing behind a work
list" shape 6.0 built. RAPTOR was in neither allowlist, so nothing was removed from one; what did
run and pass is the well-formedness assertion on the new `benchmark` pointer, plus both staleness
twins. **Do not read those two skips as green.**

**The guide states the hold, not just the number** — corpus scope measured *worse* than the
per-document tree it replaced, and the default nevertheless stays `Corpus` pending a second corpus
(see DECIDED 2026-08-27). Rather than a verdict, the guide gives a corpus-shaped rule: if your
questions resemble MultiHop-RAG's, set `PerDocument`; if your documents genuinely share themes,
`Corpus` is the paper's mechanism; either way measure your own corpus.

**What is next is a choice between threads, and nothing forces the order.** The phase now owes
HyDE, reranking, hybrid BM25, late chunking, SPLADE, the three answer engines as arms, every vector
store through the SciFact parity leg, the second-corpus RAPTOR arm, and local search's unexplained
yes/no abstention. ~~**The recommendation is the pipeline-parity test**: it is fast-tier, needs no
corpus run and no model calls, closes the gap 5.2.2 named explicitly, and is the one remaining DoD
clause that is pure engineering.~~ **Built 2026-08-27 on `feat/pipeline-parity-test` (not yet
merged) — the fast leg only.** `OrderingEmbeddingGenerator`, `PipelineParity` and
`PipelineParityTests` compare a real `AddRagNet` pipeline against the harness's own dense row at
exact score equality; the fast leg runs a synthetic corpus on every push and passes. The mutation
check ran: the plan's suggested mutation, `UseMmr`, is a mathematical no-op on this fixture (the
query vector equals doc-0's vector by construction, so MMR's relevance and diversity terms cancel
exactly and reproduce the harness's order) — `UseRedundancyFilter = true` was used instead, since
adjacent fixture documents sit at cosine ≈0.975, above the 0.95 default threshold, and the check
failed with a named-rank, both-ids-and-scores message before the mutation was reverted. **The real
SciFact leg exists but has never run on this machine** — no ONNX model, no BEIR cache — and is
verified by reading only; a review caught it passing vacuously with zero hits on both sides before
merge, fixed with explicit corpus-landed and depth-of-hits assertions. **The next recommendation is
HyDE and reranking's re-measurement under the Real protocol** — both already have parity-corpus
cells, so they are re-measurements rather than new harness arms, and remain the cheapest
*measurement* threads open in the phase. `LateChunking` remains the most expensive: it has no
protocol and needs the token-level embedding path built before one can be written.

**~~Delete `GraphLocalSearchBehavior` and `PageRankWeight`~~ — MERGED 2026-08-27 in #408
(`c3e4aa94`), verified on `main` by content rather than by the PR's label.** The blend, its three
options properties
(`PageRankWeight`, `LocalSearchDepth`, `LocalTopEntities`) and its DI registration are gone from the
package; `GraphRagRetrievalOptions` is renamed `GraphRagGlobalSearchOptions`.

**The three pinned figures survived, and that was the whole design.** A frozen copy,
`LegacyPageRankLocalSearch`, lives in the measurement harness, and the figures were re-measured
through it *before* the original was deleted — the only moment that comparison was possible:
**0.56897/0.56897, 0.2102/0.2102, 2,255-of-2,255**, zero skips, zero model calls (35,296 extraction
requests replayed, embedding cache 325,661 hits / 0 misses). All three are now machine-asserted;
the ablation's was only *printed* before, so nothing would have failed if it regressed.

**The file count in the issue and in this file was wrong three times** — "17 files in four projects"
here, 19 in the design doc, **21 across five** in fact. Each low count came from grepping the two
*named* members; only the union of all seven targets finds `GraphGlobalSearchBehavior` and its
tests, which touch the renamed options type but never the deleted members.

**One item is unverified and gates nothing else:** the `Rag.NET.E2ETests` GraphRag tests never ran —
no Docker daemon on this machine. Their local-search assertion was rewritten (it demanded an entity
chunk that `GraphChunkRoutingBehavior` provably strips, with a failure message stating a diagnosis
#247's store separation had already made false) and that rewrite is verified only by reading.

**The corpus-scope default is decided** (2026-08-27, option 3 — see DECIDED below) and is no longer
blocking on a person. It is now scheduled work: a second-corpus RAPTOR arm.

**The measurement work still open in 6.2.1** is the 17 Done sections that need a pinned figure with
a control. **#176 is no longer on this list** — it was answered 2026-08-26 in #405, and the answer
needed no new measurement at all: the counts were already recorded and what was missing was reading
the dropped endpoints' *names*.

**There is no measurement run set up and waiting.** RAPTOR Task 5 is done, and the #300 follow-up
was done on 2026-08-18 (see Blockers). The 17 Done sections that still need a pinned figure with a
control mostly need a **new harness arm built first**. `SemanticChunking` **now exists** — #393 built it
and measured it, and the result is that it depends on document length: SciFact -0.00042, FiQA
-0.02577, ArguAna -0.02930, **TREC-COVID +0.06769**. `LateChunking` still has no protocol, and
needs the token-level embedding path before one can be written. The bottleneck for
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

~~**Also unblocked and cheap:** deleting `GraphLocalSearchBehavior` and `PageRankWeight`.~~
**Merged 2026-08-27 in #408** — and it was not cheap: 21 files across five projects, six tasks, and
a plan that was wrong three times in ways only implementation exposed.

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

## DECIDED 2026-08-27 — the corpus-scope default waits on a second corpus

`RaptorTreeScope.Corpus` is the shipped default and a breaking change (#331, phase 6.2.3). Task 5
measured it as **worse** than the per-document tree it replaced — −0.0146 paper (p=0.0247), −0.0204
raw (p=0.0006), strict a wash (p=0.7372) — with the gap concentrated in **inference** queries
(0.7831 against the control's 0.8309), which is the exact multi-hop case #331 argued it would help.

**The operator's decision, 2026-08-27, is option 3: measure a second corpus before changing
anything.** The three options were:

1. Revert the default to `PerDocument` and keep `Corpus` opt-in. *(Not taken.)*
2. Keep the default and document the measured cost. *(Not taken.)*
3. **Measure a second corpus before deciding.** ← **taken**

**The reasoning is the reason the other two were refused, and it is worth keeping:** a single
dataset reversing a design decision is thin evidence, and **MultiHop-RAG rewards per-document
locality by construction** — its questions are built by composing facts drawn from identifiable
source articles, so a per-document tree is measuring on home ground. Two of three rules signing the
same way is real, but it is real *on this corpus*, and the corpus is not neutral about the thing
being tested. Reverting a breaking default on it would be acting on the least neutral evidence
available.

**So the default stays `Corpus` for now, and that is a hold rather than an endorsement.** Nothing
has been changed on the strength of the Task 5 numbers, and nothing should be until a second corpus
reports. Two of three rules are significant against it; if the second corpus signs the same way, the
revert becomes well-founded rather than corpus-shaped.

**What this adds to 6.2.1:** a second-corpus RAPTOR arm, needing a dataset whose questions are not
constructed per-document. `BeirDatasetDescriptor` already has the shape. This is now a named thread
in the phase, not an open question — the question is answered and the work is scheduled.

**Cost note carried from Task 5:** the full sweep was 58 m of generation after a 28 m I/O-bound load
for ~5,600 new answers. A second corpus is that order again, not the ~8 h the original plan
estimated — that figure came from a rate observed during tree *summarisation*, whose prompts are
much larger than answer generation's.

## Working State

**Branch:** `feat/pipeline-parity-test`, cut from `origin/main` (`ab87d156`) on 2026-08-27. This
task: `docs/planning/ROADMAP.md` and this file only, recording the pipeline-parity thread built by
the five tasks before it — no code.

**It was stale again when this session opened, for the sixth time.** The field named
`chore/reconcile-408-and-raptor-task-6` while the checkout was already on
`feat/pipeline-parity-test`, and that earlier branch had *also* already shipped: it squash-merged to
`main` as **#412** (`ab87d156`) on 2026-08-27 — the same PR that, per its own commit message, was
itself fixing the field's *fifth* stale occurrence. Verified on `main` by content — `## Measured` is
in `docs/guide/raptor.md`, `<VerifiedBy>benchmark</VerifiedBy>` is in `Rag.NET.Raptor.csproj` — rather
than by the PR's MERGED label. **Six for six: this field has now gone stale every single time the
branch it named has merged, and only ever at that moment, because that is the one moment nobody is
editing this file.** Read it against `git branch --show-current` and against `origin/main` by
content before trusting either — a session that trusts this field's story of its own branch is
trusting the one claim in the file structurally guaranteed to be checked last.

**It was stale again when this session's predecessor opened, for the fifth time — and so was the
step below it.** The field named `chore/planning-176-and-phase-table` while the checkout was on
`refactor/delete-pagerank-local-search`, and that branch had *also* already shipped: **PR #408
squash-merged to `main` as `c3e4aa94` on 2026-08-27**, verified on `main` by content —
`GraphRagGlobalSearchOptions.cs` present, `PageRankWeight` gone from `src/` (the one surviving
mention is a doc comment in `LocalSearchContextBuilder`), `LegacyPageRankLocalSearch` named in the
GraphRag csproj's IVT comment — rather than by the PR's MERGED label. **The pattern is now exact
and worth naming: this field, and the Recommended Next Step under it, both go stale at the moment
the branch they describe merges, which is the one moment nobody is editing this file.** Read both
against `git branch --show-current` and against `origin/main` by content before trusting either.

**The fourth time, recorded 2026-08-27, read as follows.** It named
`chore/roadmap-6-2-11` while the checkout was on `research/176-dropped-endpoints`, and *that* branch
had already shipped: its two commits were squash-merged as **#404** and **#405** under different
SHAs, so `git log origin/<branch>..HEAD` showed nothing pushed while the work was on `main` all
along. **Verified on `main` by content** — `DescribeDroppedEndpoints` is in
`GraphRagFunctionsTests.cs` and `Phase 6.2.12` is in `ROADMAP.md` there — rather than by either PR's
label. The lesson generalises the one already recorded: a branch that *looks* unpushed is no more
trustworthy than a PR that *looks* merged. Diff the branch against `origin/main` by content before
concluding either way; here the only difference was main's own newer anglesharp bump (#406).

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
