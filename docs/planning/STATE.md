# Session State

**Last updated:** 2026-09-05 (THE TECHNIQUE SWEEP IS COMPLETE — five techniques, three corpora each,
fifteen cells, every figure pinned and reproduced on an idle machine. What remains of the phase is
four allowlist entries, all LLM-funded; Self-Query is measured and discharged)
Seventeen PRs merged across three days, each verified on `main` by content)
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

**Last completed:** **`mapreduce` measured and pinned at 0.6483, 2026-08-31 — a null result, and the
DoD's answer-engine clause now closes.** Both controls held on the run (`dense` 0.3499 / 0.2603 /
0.3242, `chatengine` exactly its pinned 0.6341, both replaying from cache), so nothing drifted.
**`mapreduce − chatengine` = +0.0142, McNemar p=0.2955 on 462 wins against 430 — not significant.**
The map/reduce mechanism buys nothing measurable over a single call on this corpus; a feature
measured and found unremarkable is a completion, as 5.2 was. **The 400-query subset put the same
difference at +0.0340**, which would have read as a win: earlier pilot-to-scale misses moved a
magnitude, this one moves the conclusion — **a subset can carry a direction and cannot carry a
significance.** Contract compliance 2,553/2,556, up from 2,333. `UnmeasuredEngineArms` is now
**empty**: every arm carries a figure, reached through three separate failures of the guard that each
named the arm and the list to update. Third timing miss too — 30 minutes against a projected 6.4
hours. Full account in `docs/plans/2026-08-31-mapreduce-refusal-filter-findings.md`.

Before it, **the MapReduce refusal-filter defect, found and fixed 2026-08-31 — and it
overturns what the sweep concluded about that engine.** MapReduce drops `not found` partials by an
**exact** match before the reduce; a caller system prompt that reshapes replies defeats it, so under
the extraction contract refusals arrived as `Not found. The answer to the question is "not found".`,
survived the filter, and the reduce **discarded the one correct partial** as contradicted. A logged
transcript shows a map returning `The answer to the question is "Microsoft".` and the reduce throwing
it away. Fixed by appending a map protocol after the caller's prompt on **map calls only**; two
fast-tier regression tests, mutation-checked. **Validated on 400 queries: `mapreduce` 0.1898 →
0.6487, contract compliance to 400/400, "not found" answers from the majority to 1 of 353, and
`mapreduce − chatengine` = +0.0340** — ahead of the single-shot control rather than 0.43 behind.
**This retires the "apparatus failure / cannot be measured / per-chunk calls extract rather than
answer" reading**, which was elaborate and wrong; it was one defect. **The DoD clause is now
closable** — MapReduce was the only blocker. **Not yet pinned**: 400 queries is validation, and the
pin needs the full 2,556. **And `refine`'s pinned −0.1055 needs re-examination** — it shares the
per-chunk shape that just hid a 0.46 defect. Full account in
`docs/plans/2026-08-31-mapreduce-refusal-filter-findings.md`.

Before it, **the full answer-engine sweep on the corrected apparatus, 2026-08-30 — two clean
findings, and the phase's first real engine result.** 15,336 records, 5.5 hours, 18 tests / 0 failed.
**Gate 0 held on all three rules** (`dense` reproduced 0.3499 / 0.2603 / 0.3242 exactly).
**(1) Sequential refinement is significantly worse than answering once** — `refine − chatengine` =
**−0.1055**, `p<0.0001`, 132 wins against 370, on an uncontaminated comparison (identical prompt, path
and passes). **(2) FLARE's lookahead helps by under a percentage point** — `flare − flarefixed` =
**+0.0075**, p=0.0135, the only direct measurement of FLARE's mechanism. Labelled not-clean: the
FLARE arms' ~+0.11 over `chatengine` is confounded by a post-loop formatting call no other arm gets,
and `chatengine − dense` = +0.2843 is one sentence of prompt. **Pinned:** `chatengine` 0.6341,
`refine` 0.5286, `flarefixed` 0.7428, `flare` 0.7503. **`mapreduce` is not pinned** — it ran, and its
figure measures a known-broken setup. **Two of three named engines now have a figure with a control;
the DoD clause is still not met.** Full account in
`docs/plans/2026-08-30-answer-engine-sweep-results.md`.

Before it, **the contract split three ways by granularity, and its 400-query validation,
2026-08-30 — four arms became comparable and `mapreduce` was proven not measurable here.** Grounding
to every arm, abstention to `dense` alone, terminal extraction reaching FLARE only after assembly.
**`PromptTemplate`'s byte-identity is proven**: `dense` returned 0.3484 / 0.2635 / 0.3201 with
222/353 and 21/47 abstentions, identical to the previous subset digit for digit, replayed wholly from
cache — pin and Gate 0 intact. Against a properly-instructed `chatengine` control: `flare` +0.1417,
`flarefixed` +0.1332, `refine` −0.0680, `mapreduce` −0.4249; FLARE moved for the first time now that
grounding reaches it. **`mapreduce` stays broken for a structural reason** — grounding is no more
portable to per-chunk maps than abstention was, because those calls *extract facts* rather than
answer the question, so no "answer the question" instruction fits them. Third instance of the
granularity class, and the one proving it is not about any particular rule. **A clean full sweep on
this basis does NOT close the DoD clause**, which names MapReduce among the three engines. Full
account in `docs/plans/2026-08-30-engine-granularity-findings.md`.

Before it, **the engine contract fix and its first 400-query validation subset, 2026-08-30 — it
fixed two arms, broke a third and missed a fourth.** `AnswerContract` names all three of
`PromptTemplate`'s instructions and `EngineAnswerOptions` passes the whole of it; `PromptTemplate`
composes to the same bytes so `dense`'s cache and pin survive. **For `chatengine`, `mapreduce` and
`refine` it worked** — abstentions appeared where there had been none (0 of 301 before, 13-26 of 47
now) and `chatengine − dense` collapsed from **+0.4204 to −0.1104**. **But `mapreduce` fell to
0.0142**, answering the literal `"not found"`: the abstention rule reaches its per-chunk maps, and a
single chunk lacks the answer even when six together contain it. **And the FLARE arms never received
the contract at all** (0 of 47) — a gap in #419's own Task 4. **The finding, named as a class:
there is no single instruction string that means the same thing to a single-shot engine and to one
that decomposes its context.** Fifth occurrence of that shape in this phase. The 400-query subset
cost ~$3 against ~$20 and found three problems, two of them new. Full account in
`docs/plans/2026-08-30-engine-contract-subset-findings.md`.

Before it, **the full 2,556-query answer-engine sweep, 2026-08-30 — it ran, and its accuracy
figures are not an engine comparison.** 15,336 records, 6.5 hours, 15 tests / 0 failed / 0 skipped.
**Gate 0 held exactly** — `dense` reproduced its pinned 0.3499 / 0.2603 / 0.3242 to four decimals, so
the corpora did not diverge and the run is sound. Then the control moved: `chatengine` shares
`dense`'s retrieval verbatim yet scored **+0.4204 paper and −0.0541 raw** against it. The cause is
that **`PromptTemplate` carries three instructions — grounding, abstention, extraction — and
`EngineAnswerOptions` passes only the third**; #418 found one of three. `dense` abstains on 61.8% of
answerable queries because it was told to, and **every engine arm abstains 0 of 301 on the
unanswerable ones**, five times over. Nothing is pinned; the DoD's answer-engine clause is still
unmet. The re-run is deferred — see Recommended Next Step. Full account in
`docs/plans/2026-08-30-answer-engine-sweep-findings.md`.

Before it, **the FLARE contract-and-cache fix, 2026-08-29, merged to `main` as `50221812`
in #419** — #418 (merged to `main`
2026-08-29 as `e7563873`) gave every engine arm the judge's extraction contract and broke FLARE doing
it: a terminal `SystemPrompt` fighting FLARE's own one-sentence-at-a-time protocol produced an
86,091-byte runaway (23× the historical maximum), reachable because `CachedGraphRagClient` also
discarded FLARE's `MaxOutputTokens` guard. **The third time in this phase a fix has caused the next
defect** (6.2.12 had #390 → #396 → #400). Five commits (`d8b86bba`..`1d9f4f2b`) fix FLARE's fragment
protocol, the cache key (new optional field, omitted not emptied, zero regeneration across 86,510
entries), the client's option-forwarding, and the harness's contract application. A re-run pilot,
2026-08-29 — 15 tests, 0 failed, 0 skipped, 469 new cache entries — found every engine arm meeting the
extraction contract on 8 or 9 of 9 queries (up from 0 of 9), three arms at 8 rather than 9. **This
does not close Phase 6.2.1's answer-engine DoD clause**, which still needs the full 2,556-query sweep.
Full account in `ROADMAP.md`'s 6.2.1 block and `docs/plans/2026-08-29-flare-contract-pilot-notes.md`.

Before it, **the answer-engine arms, 2026-08-28, merged in #416 (`d2d96b0d`)** — five arms sharing
dense retrieval and varying only generation (`chatengine` the control, `mapreduce`, `refine`,
`flarefixed`, `flare`), three pilot gates (context identity, call shape, lookahead firing), and a
corrected cost model (~$4 realistic / ~$21 worst case for the 2,556-query sweep, dominated by FLARE's
sentence count). `flare` shipped with a real retriever because #414 merged mid-implementation as
`641e27f0`. A 10-query pilot then ran 2026-08-28 and found every non-`dense` arm missing the judge's
extraction contract entirely (0 of 9) — the defect #418 fixed, and the fix that broke FLARE above.
Before that, **the pipeline-parity test's fast leg, 2026-08-27** — now
merged as **#414** (`641e27f0`), verified on `main` by content (`PipelineParity.cs` present) rather
than by the PR's label — `OrderingEmbeddingGenerator`, `PipelineParity` and `PipelineParityTests`
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


**2026-09-05 — the technique sweep is COMPLETE. Five techniques, three corpora each, fifteen cells,
every figure pinned and reproduced.**

| corpus | HyDE | Reranking | Hybrid BM25 | Late chunking | SPLADE |
| --- | --- | --- | --- | --- | --- |
| SciFact | +0.03647 | +0.01266 | +0.01880 | −0.02232 | +0.01276 |
| FiQA | −0.00886 | −0.00951 | −0.04185 | **+0.02800** | −0.05527 |
| ArguAna | −0.02053 | −0.06938 | **+0.03978** | +0.01429 | +0.01258 |

**Corpus dominates technique.** SciFact is helped by four of five; FiQA harmed by four of five and
helped only by late chunking; ArguAna splits on the kind of matching. **No row is a recommendation
without naming the corpus** — the phase's finding, not a caveat on it.

**The ArguAna prediction, written before SPLADE ran, is confirmed at +0.01258.** Both term-matching
techniques help that corpus; both dense-path techniques harm it. The surviving explanation — that
the harm is specific to matching a document-shaped or semantically-rescored query against fragments
— survived a test that could have refuted it.

**SPLADE was blocked on provisioning, not capability.** `Qdrant/Splade_PP_en_v1`, 508 MB, pinned with
its digest in `docs/reference/ci.md`; the canonical NAVER model publishes no ONNX export at all. The
shipped encoder had never run against a real model in this repository before 2026-09-04.

**Measured on an idle machine, deliberately.** All three cells in 2 h 59 m; a first attempt took ~80
minutes for SciFact alone under load. **The per-dataset `elapsed` lines understate these cells** —
encoding happens before the harness's stopwatch starts.

**2026-09-03, fifth change — late chunking measured on three corpora, and it found a shipped defect
on the way.**

| corpus | HyDE | Reranking | Hybrid BM25 | Late chunking |
| --- | --- | --- | --- | --- |
| SciFact | +0.03647 | +0.01266 | +0.01880 | **−0.02232** |
| FiQA | −0.00886 | −0.00951 | −0.04185 | **+0.02800** |
| ArguAna | −0.02053 | −0.06938 | +0.03978 | **+0.01429** |

**Late chunking is anti-correlated with the other three** — the only one negative on SciFact and
positive on both others. **The first retrieval-quality figure it has ever had**: the allowlist entry
claimed Phase 3.7 measured it, and 3.7 measured the parity dense anchor instead.

**THE SHIPPED DEFECT, and it is the session's most consequential find.**
`OnnxTokenEmbeddingOptions.MaxTokens` defaulted to **8192** where its sibling in the same package
defaults to **256**, for the same model. Windowing therefore never triggered, ONNX threw at the
position-embedding node, `LateChunkingStrategy` swallowed it, and `EmbeddingBehavior` backfilled
ordinary embeddings — so **`UseLateChunking()` silently did nothing on every document long enough to
need it**, with no error and no log. 1,401 of 9,506 SciFact units before the fix. Fixed to 256, and
guarded by a test pinning the **relationship** between the two encoders' limits rather than the
number.

**It was found only because the benchmark seam refuses to fall back.** Nothing else in the repo
would have surfaced it.

**Two claims of mine retired by this thread**, both written as statements about corpora when the
evidence only supported statements about the techniques measured so far: ArguAna's fragmentation
explanation (refuted by hybrid), and "FiQA is the corpus nothing helps" (refuted by late chunking).

**One framing error corrected.** Three places said the cell "keeps the Real protocol's boundaries and
changes only how their vectors are computed". It varies **both** — late chunking windows at its own
256 tokens, producing 9,507 / 73,014 / 11,137 units against the Real cells' 20,155 / 121,236 /
24,003. The unit counts were available before the run. **So no figure here isolates whole-document
context**; separating it needs a control with late chunking's boundaries and ordinary embeddings,
which no cell runs.

**And a guard of mine was weakened after it fired, deliberately and with arithmetic.** The original
asserted no excluded document is judged-relevant; SciFact's 15319019 falsified it. It was replaced by
a bounded check — worst case `affectedQueries / judgedQueries` = **0.00333 against a ±0.005 band** —
which refuses outright above the band. Weakening "none" to "bounded" is defensible; weakening it to
"reported" would not have been.

**Costs 430.9 s / 2,295.7 s / 494.0 s, with NO warm speedup** — the only cell in the table where a
re-run is not cheaper, because `EmbeddingCache` is keyed on text and a late-chunked vector is not a
function of chunk text alone. Its budget entry declined to derive a cost beforehand, and it is the
only cost entry in the phase that needed no correction after.

**2026-09-03, fourth change — hybrid BM25 measured on three corpora, and it refuted an explanation
this phase had asserted twice.**

| corpus | HyDE | Reranking | Hybrid BM25 |
| --- | --- | --- | --- |
| SciFact | +0.03647 | +0.01266 | +0.01880 |
| FiQA | −0.00886 | −0.00951 | **−0.04185** |
| ArguAna | −0.02053 | −0.06938 | **+0.03978** |

**Corpus dominates technique.** SciFact was helped by everything measured at the time; ~~**FiQA is
harmed by everything measured**~~ — **RETIRED 2026-09-03 by late chunking at +0.02800**, the largest
positive effect any technique has had on FiQA; ArguAna splits on the *kind* of matching.
has no answer here without knowing the corpus — that is the phase's finding, not a caveat on it.

**The refutation, and it was set up in writing before the run.** ArguAna's harm under HyDE and
reranking had been attributed to its whole-argument relevance making 512-character fragments the
wrong unit. This cell's own pre-run text said: *"BM25 is the test of that explanation: if fragments
are the problem, a term-frequency model over the same fragments should suffer too."* **It does not
suffer — +0.03978, the best Real-protocol figure ArguAna has**, above its Real dense control and
above its parity dense. Fragmentation alone is not the cause. What survives is narrower and untested:
the harm is specific to matching a document-shaped or semantically-rescored query against fragments.
Both places the old claim was asserted are now struck and annotated in `ROADMAP.md`.

**Sign held on all three hybrid pairs.** Across nine (technique, corpus) pairs measured under both
protocols, **exactly one flips** — FiQA reranking. Parity predicts the sign eight times in nine and
the magnitude never.

**Costs: 172.5 s / 308.6 s / 1,164.4 s, no model calls.** The cheapest technique, as predicted.
FiQA came in *below* its own ~58 m parity sibling, opposite to its derivation: BM25 indexing is
term-count work, and many short chunks hold roughly the same terms as fewer long documents while each
posting list is shorter. **Fourth cost derivation in this phase to miss.** ArguAna's held, and the
difference is that it reasoned from a mechanism — query count drives these cells — rather than
scaling a number from another corpus.

**Two guards earned their keep on this branch.** Wiring the cells without `Describe` and `Filter`
arms failed four fast-tier tests immediately — including `NoCellsDiscriminatorIsContainedInAnothers`
and `TheSkipMessagesCommand_OptsInOnlyTheCasesOwnDataset`, both added in #439 — catching before push
exactly the gap that left #433 red on CI for a day. The parity discriminator needed the same trailing
underscore treatment as Hyde and Reranked did.

**2026-09-03, third change — the `Delivered` blind spot closed the day after it was found.** All
nine sections normalised to `✅ Done`; six took one-line pointers naming tests that already existed,
three took allowlist entries with owning phases because nothing exercises them. **Section allowlist
40 → 43 — worse and truer at once**, since those three were previously counted at zero by a guard
that could not see them. Mutation-checked: removing the Weaviate pointer now fails the guard naming
that section, which was impossible yesterday.

**The status question resolved by evidence, not preference.** `Delivered` was never a status: the
three vector stores sit between two `✅ Done` sections in the same region, and every `Delivered` line
follows one authoring pattern where the status line carries the whole description. Nothing defined
it anywhere.

**And the cost estimate that preceded it was wrong in the useful direction.** The debt entry said
folding these in meant "nine pointer-writing tasks, which is work rather than a one-line fix". Six
were one-line fixes. The estimate was made without checking what already existed — the same
incomplete-look shape as the Late Chunking slip in that entry's first draft, on the same day, in the
same entry. **Check before costing, not after.**

**2026-09-03, second discharge — `Rag.NET.QueryTechniques`, allowlist 19 → 18. 6.2.1 now owns none
of the remaining entries.** `HydePipelineParityTests` holds the shipped `HydeBehavior` to the
harness's `HydeAblationRow`: same three hypotheses, same store, a real `AddRagNet` pipeline with
`UseHyde` on one side and the row's own pooling on the other, identical ids, scores and order. Fast
tier — no model, no corpus, no network. **Mutation-checked**: skewing one component of the shipped
pooled vector by 5% fails it at rank 0 on a 0.0002 score difference.

**This is what the yesterday's warning was about, now closed.** The three HyDE figures executed no
line of the package; the two pooling implementations were arithmetically identical line for line, in
two assemblies, with nothing tying them together. The test ties them, so the figures describe shipped
behaviour rather than a re-implementation.

**Level `integration`, deliberately not `benchmark`.** Enough to say the figures describe the shipped
path; not enough to say a benchmark ran through it, because the parity corpus is six documents in two
dimensions rather than a BEIR corpus in 384. **Claiming `benchmark` would be the same overclaim this
phase has twice retracted.** Running a Real cell through the shipped generator would earn it and was
considered; it was not taken.

**One geometry error worth carrying**, because it cost a cycle and the fix is general: the first
fixture put the three hypotheses on documents 3, 4 and 5, expecting their mean to rank the corpus in
reverse. **The mean of three unit vectors points at the middle one**, so the resultant landed exactly
on document 4 and documents 3 and 5 tied — a ranking decided by sort stability rather than geometry.
The angles are asymmetric now (3.8, 4.3, 5.1 steps, resultant 4.399) and the expected order was
computed from the geometry rather than read off a run. **A pinned expectation derived from the code
under test pins nothing.**

**2026-09-03 — the exit condition's allowlist moved for the first time this milestone.**
`PackagesAllowedToStayUnit` **20 → 19**, `SectionsAwaitingExercise` **42 → 40**, by discharging
`Rag.NET.AnswerEngines`: all three engines carry a pinned figure against a control, so the package
went `unit` → **`benchmark`**, and Map-Reduce and Refine gained `Exercised by:` pointers naming the
arm, the reproduction and the number. Both halves are guard-enforced and were **mutation-checked**,
not assumed: reverting the level fails `NoPackageStaysAtBareUnit`, removing a pointer fails the
exercise guard. 94/94 conventions tests green.

**`Rag.NET.QueryTechniques` was NOT discharged, and this is the trap to avoid next session.** Three
corpora of HyDE figures make it look done. `HydeAblationRow` imports `HydeOptions` and nothing else
from the package — the hypotheticals come from `HypotheticalCache`, written by a separate generation
tool, and the shipped `LlmHypotheticalDocumentGenerator` is exercised only by unit tests. **The
measurements characterise the technique and touch none of the shipped code.** Closing it needs a
test proving the harness row and the shipped generator agree — the same harness-versus-shipped-path
gap the pipeline-parity test closed for retrieval — not another measurement.

**And a guard blind spot was found while doing it**, recorded in `ROADMAP.md`'s follow-up debts and
assigned to this phase: `FeatureExerciseTests` matches the literal `**Status:** ✅ Done`, so the
**nine `**Status:** Delivered` sections are invisible to it** — including HyDE v2, FLARE, SPLADE and
the Weaviate/Chroma/Pinecone stores, six of which are 6.2.1's own threads. The phase cannot claim
"every plan row has its pointer" while nine rows are outside the guard. Deliberately not fixed on
discovery: whether `Delivered` is a distinct status or drift is undetermined, and widening the marker
turns nine sections into nine pointer-writing tasks. **Decide the status question first.**

**HyDE is finished on every corpus where it can be measured.** TREC-COVID is not unscheduled but
**unmeasurable at any budget**: nobody has generated its hypotheticals, so the cell fails on
refuse-on-miss after paying for the chunking. A fourth corpus for HyDE means a **paid generation
run** that has never been costed — a separate piece of work, not a scheduling decision.

**Do not run any cell with `=1`.** It still means every dataset, and TREC-COVID's Real leg has never
been embedded — a `RealHyde` or `RealReranked` run that reaches it chunks and embeds a corpus 33x
SciFact's from cold and then fails on refuse-on-miss, because nobody has generated its hypotheticals.
That is the trap that cost 6 h 18 m on 2026-09-01.

**Three things this session established that the next one should not re-derive:**

1. **A parity-corpus ablation figure overstates what a technique buys a real user.** HyDE +0.055 at
   parity against +0.036 real; reranking +0.039 against +0.013, and negative on FiQA. Two techniques,
   same direction. Every remaining cell in this table is worth measuring under Real for that reason,
   and the parity number is not a substitute.
2. **A green local run over a gated-off suite is not evidence about the gated-off path.** #433 was
   red on CI for a day because `Explain` is only reached when a case is gated *off*, and every local
   run had the opt-in set. Run the fast tier once with no `RAGNET_*` variables before pushing.
3. **Confirm a pin with a second run before trusting its cost.** The two SciFact `RealHyde` runs
   agreed on nDCG to five decimals and disagreed on wall clock by 16x, 199.5 s against 12.5 s. The
   figure survived; the timing would have been published warm.

**Still open in 6.2.1, in rough order of cost:** FiQA/ArguAna/TREC-COVID `RealHyde` and the two unrun
`RealReranked` cells; hybrid BM25, late chunking and SPLADE under the Real protocol; every vector
store through the SciFact parity leg; local search's yes/no abstention, still unexplained; and
`refine`'s −0.1055, whose caveat MapReduce turned into a live question rather than a hedge. The exit
condition also wants every 6.0 *plan* row pinned and the guards' allowlist empty.

**The self-query pilot ran against a real model on 2026-09-05, and it cost about a hundredth of a
cent.** Six queries, six calls; 5 produced a filter and all 5 named the query's own corpus. Both
mechanism gates held, and a replay run afterwards returned 6 cache hits and 0 misses, so the
pay-once-replay-free pattern is proven for this feature rather than assumed. It publishes NO
accuracy figure — six queries cannot support one, and RAPTOR's pilot headline reversed at full
scale.

**Two corrections it forced, both of mine.**

1. **#467 claimed the funded self-query run "would have crashed on ordinary replies"** because an
   object-shaped `filters` is "what a schema-free prompt most often gets back". The crash is real
   and the fix is right, but that frequency claim was speculation and the first evidence
   contradicts it: **all six real replies used the correct array shape.** The one that produced no
   filter returned an empty array, not a malformed one. Nothing so far validates the fix, because
   no reply took the crashing path. Asserting a frequency without evidence is the same habit that
   mispriced two runs in this phase.
2. **The pilot's own cache-empty gate reported a full cache as empty.** `GraphExtractionCache`
   SHARDS entries into subdirectories by key prefix, and the first draft enumerated the top level
   only — the exact hazard that file's own documentation warns about. Six entries on disk, and the
   replay run skipped saying "nothing to replay". Fixed to enumerate recursively.

**And a finding about the shipped behaviour, which reframes what this entry can claim.**
`SelfQueryBehavior` writes its filter into `RetrievalOptions.Filter`, an
`ISpecification<SearchResult>` that `FilterBehavior` applies as `results.Where(...)` AFTER
retrieval — with no over-fetch and no backfill. It never writes `MetadataFilter`, the field
`InMemoryVectorStore` pre-filters on. **So self-query narrows a page of results; it does not scope
the search.** On a two-corpus store a query asking for ten gets ten, discards the foreign ones and
returns fewer. The tag-filtered cell's 0.67742 came from a pre-filter and is therefore not a target
this path can reach, which is worth knowing before the full run is designed around it.

**Self-Query is measured and discharged (2026-09-05), and its figure is not what I predicted.**
300 judged queries, 300 model calls, ~$0.01; a replay run returned 300 cache hits, 0 misses and an
identical figure, so the pin is confirmed. **nDCG@10 0.68247** — against **0.67742** for the same
two-corpus store filtered by hand and **0.67065** unfiltered.

**I predicted it could not reach 0.67742 and it beat it.** The reasoning was that `FilterBehavior`
applies self-query's filter as `results.Where(...)` after retrieval with no backfill, so the page
can only shrink. That part is true — 4,496 hits were discarded across 300 queries. The error was
treating the filter as the whole technique. `SelfQueryBehavior` also REWRITES the query, and the
pipeline embeds the rewrite; a filter can only remove, so with the same query vector the
post-filtered page is a prefix of the pre-filtered ranking and cannot score higher. **The rewrite is
the only mechanism that can explain +0.00505, and the cell measures rewrite and filter together.**

That is also why the row drives `AddRagNet` rather than a hand-composed chain. The first draft
composed generate → search → `Where` by hand and could not apply the rewrite at all, because
`EmbeddingTextOverride` is `internal` to Rag.NET. It would have measured the filter alone, produced
a LOWER number, and carried the technique's name — the same class of error as the HyDE and RRF
harness gaps this phase has been closing.

**The post-filter's structural cost is real and this harness cannot see it.** `BeirHarness:736` sets
`TopK = (Cutoff + …) × maxUnitsPerDocument`, which is 410 deep for a cutoff of 10, so ~15 discards
per query never approach the cutoff. A caller retrieving at `TopK` 10 would see the shrinkage. The
pointer says so rather than letting the figure read as a clean endorsement.

**6.1 remains the milestone's only blocker engineering cannot clear****6.1 remains the milestone's only blocker engineering cannot clear** — 18 cassettes, blocked on
accounts rather than effort, and gating v1.0 by the operator's 2026-08-20 decision.

---

**2026-09-05, second change — three more allowlist entries discharged, `SectionsAwaitingExercise`
41 → 38, and only ONE of the three needed new code.** The pattern from the 2026-09-04 audit held:
entries drift from what the repository already contains.

**BM25 Synonym Expansion needed nothing built.** `InMemoryBm25IndexSynonymTests` already drives the
real index with and without a `SynonymMap` — "Kubernetes" indexed and retrieved by "k8s", a
three-term group matching on all forms, a runtime addition taking effect, and
`Search_NoSynonymMap_ExistingBehaviourUnchanged` pinning the without-expansion case returning
nothing. That **is** the with-and-without pair the entry asked for, at the mechanism level.

**Hierarchical Merger and Domain-Specific Templates needed one new test between them.**
`RealPaperExerciseTests` runs a real markdown paper through the real `MarkdownDocumentParser` into
both strategies. **The parser is not re-implemented**, which is the whole point: both strategies had
unit tests and sat on the allowlist anyway, because those tests hand-build their `DocumentSection`
inputs and so cannot catch a disagreement between strategy and parser about what a heading is.
Mutation-checked — stripping the `##` markers fails both.

**All three pointers state what they do NOT claim.** None carries a retrieval-quality figure. The
entries wanted Real-protocol cells; **no BEIR corpus has headings** (SciFact documents are a title
and an abstract, checked against the corpus rather than assumed) and `SynonymMap` ships empty, so a
synonym cell would have measured whichever vocabulary its author invented. Both entries offered a
second route and both took it. **Whether heading-aware chunking or synonym expansion helps retrieval
is unmeasured and says so in the pointer**, because a mechanism test and a quality figure are not
interchangeable.

**Eight entries remain**, and the shape is now: five needing a paid model under one funding
decision, one compute-only cell (Tag-Based Retrieval Filtering — a filtered parity leg, needing a
decision about what tags), and two operator decisions (Time-Weighted's "declared" route, and
Ensemble/RRF's swap to the library's `RrfMerger`).

**A correction to the 2026-09-04 audit, which called four of these "cheap runs".** Two were not runs
at all: Hierarchical Merger's Real-protocol framing is unsatisfiable on any corpus here, and
Domain-Specific Templates keys on the same absent headings. The audit costed them by reading the
entries rather than checking them against the corpus — better than guessing and still one layer
short.

**What is left in this phase, and it is no longer measurement of techniques.** The exit condition
has three clauses: the pipeline-parity test (met), the package allowlist (no 6.2.1 entries remain),
and every row 6.0 classified as *plan* carrying its pointer and its pin. Only the third is open, and
it is **five `SectionsAwaitingExercise` entries** — thirteen at the 2026-09-04 audit, which found
two stale rather than owed, less the three #462 discharged, Ensemble/RRF, SPLADE, Time-Weighted and
Tag-Based. **All five that remain need a paid model, so the phase's remaining work is one decision
rather than a queue.**

**Time-Weighted took the sanctioned `declared` route on the operator's 2026-09-05 call**, and
checking it went one layer deeper than the entry did. "BEIR carries no timestamps" is true, but
"BEIR carries no metadata" would have been false: SciFact, FiQA and ArguAna ship `metadata: {}`
while TREC-COVID ships `url` and `pubmed_id`. Neither is a date, so the declaration holds — but
resolving those PubMed ids to publication dates would make a pinned figure depend on a third-party
service, which is the reason to decline rather than an oversight, and the pointer says so.

**SPLADE was discharged by writing one line, and finding it was the useful part.** The dictionary
held eight entries while the prose said seven, and the eighth was not an arithmetic slip: #461
measured SPLADE retrieval on three corpora and pinned it — exactly what the entry asked for — but no
pointer was ever added to `features.md`, so the entry stayed and the guard stayed green on it. **The
guard cannot see that shape.** It checks that an entry has no pointer and that a pointer names a
real class; an entry whose work is DONE but unpointed satisfies both and is indistinguishable from
one that is genuinely owed. Only reconciling the count against the dictionary surfaced it. Worth
re-running that reconciliation after any phase that discharges several entries at once.

**They are not seven equal units, which is the point of the audit:**

- **Five need a paid model** — Self-Query, LLM Metadata Extraction, Deep Research Loop, Mind-Map
  Extractor, Conversational Memory. All drive an `IChatClient`. `CachedGraphRagClient` plus the
  on-disk `graph-extractions`, `graph-reports` and `graph-answers` caches are how the GraphRAG work
  paid once and replayed free; the same shape applies. **One funding decision covers all five.**
- **The compute-only group is empty.** It was four. #462 discharged three, two of which turned out
  not to be runs at all, and Tag-Based Retrieval Filtering was measured on 2026-09-05.
- **Time-Weighted is closed** — declared on 2026-09-05, the route the entry itself sanctioned. It
  drops the count without measuring anything, which is what declaring means and what the pointer
  admits: whether recency weighting helps retrieval on a real dated corpus stays unverified.

**Ensemble/RRF is discharged (2026-09-05), and it found something.** `HybridFusionParityTests`
retrieves through a real `IRagPipeline` with `UseHybridSearch` set, so `EnsembleBehavior` fuses a
dense and a lexical arm through the library's own `RrfMerger`, and holds that to the `+BM25` cell's
hand-composed fusion. The two agreed on k and on the 1-based rank formula, and **disagreed on the
weights**: the row weighted each leg 1.0 while `EnsembleOptions` defaults `DenseWeight` and
`Bm25Weight` to 0.5 each, so every harness score was exactly twice the library's. That is a uniform
factor on an RRF sum — it reorders nothing, nDCG cannot see it, and the SciFact cell reproduced its
pinned 0.69622 after the row was brought onto the library's default. It mattered anyway: it is
visible to anything reading the fused score, `MinScore` first, and removing it let the test assert
score equality outright instead of equality-up-to-a-factor.

**The order-only version of that test was worthless and the mutation check is what said so.**
Changing the row's rank constant from 60 to 10 left it green — RRF rankings barely move with k. Only
after the score assertion went in did both that mutation and a 0-based-rank mutation fail. This is
the fourth guard on this branch that looked right and proved nothing until it was mutated; the
pattern is now consistent enough to treat mutation as part of writing the guard, not a review step.

**Tag-Based is measured, and the design choice WAS the job.** BEIR chunks carry no tags, so
filtering on an invented vocabulary would have measured the invention — the trap that emptied three
of the other four cheap entries. The operator chose the corpus a document came from, which is a fact
about the data rather than a choice, and it turned the cell from a score into a TARGET: SciFact
filtered out of a SciFact+FiQA store must reproduce SciFact's standalone figure. **It did, exactly**
— 0.67742 to five decimals, 0 leaked hits over 123,000, against a prediction pinned before the run.
The unfiltered control on the same store scores 0.67065, so the filter was doing work rather than
sitting over a corpus that never competed.

**Two things that run surfaced, neither of them about tagging.** The 26x gap between its two runs
(702.7 s then 26.9 s, identical figures, 0 embedding-cache misses both times) is the OS page cache
over a 141k-unit read — the same artefact that produced three false findings earlier this phase, and
why the cost entry says to budget the cold number. And `SummariseUnits` prints a document count
larger than the dataset's and a NEGATIVE "contributed nothing" figure here, because it assumes
indexed units come from the dataset under measurement. Harmless — the metrics come from qrels — but
a reader meeting "-57587" should know it is a one-corpus assumption meeting a two-corpus store.

**6.1 remains the milestone's only blocker engineering cannot clear** — 18 cassettes, blocked on
accounts, gating v1.0 by the operator's 2026-08-20 decision.

**Three standing cautions, each earned rather than inherited:**

1. **Never run a cell with `RAGNET_BEIR_LONG_RUNS=1`.** It means every dataset. Use a name or a
   comma-separated list; the gate has taken lists since #439.
2. **Confirm every pin with a second run.** Ten cells have been confirmed across this phase and all
   ten reproduced their nDCG to five decimals while **none** reproduced its timing.
3. **Do not derive a cell's cost from another cell.** Five derivations here have missed — high, low,
   by corpus size, by per-query rate, by cost shape. Both entries that declined to derive needed no
   correction.

**And a fourth, earned four times this week:** a benchmark timing taken on a loaded machine is not a
figure this table should carry. SPLADE's SciFact cell read ~80 minutes under load and all three
cells fit in 2 h 59 m idle. The nDCG never moved.


**The text below predates 2026-09-02 and is kept for its reasoning, not its recommendation.** Its
"next step is `MapReduceAnswerEngine`" was carried out: the defect was fixed in #430, the arms were
made comparable in #429, and `mapreduce` was pinned at 0.6483 in #431. Read it for how the three
options were framed, not for what to do next.


**The answer-engine thread has delivered what it can without product work. The next step is
`MapReduceAnswerEngine`, as a shipped-package defect rather than a benchmark chore.**

A caller who sets `RagOptions.SystemPrompt` has it applied to **every per-chunk map**. Instructions
written about the answer — "say so if you don't know", "answer in one word", "end with X" — are false
of a single chunk, and the engine degrades badly: measured at **0.0142** with an abstention rule and
**0.2009** with grounding alone, with the worst extraction-contract compliance of any arm. **This is
the same defect class as the FLARE one fixed in #419**, which was scoped as a shipped-package defect
reachable by any user with a terminal `SystemPrompt`. MapReduce has the identical vulnerability,
unprotected, and a plain user prompt triggers it.

Fixing it protects real users, makes MapReduce measurable, and closes the DoD clause the sweep could
not. `refine` likely needs the same treatment, and its −0.1055 carries a caveat until it gets it.
**Cost: ~$2.50 to re-measure, because only the changed arm re-keys** — 7 calls/query over 2,556
queries. The $20 sweeps are behind, not ahead.

**Validate on a 400-query subset before any full run.** That pattern has now paid for itself twice:
~$3 caught three problems the first time, and predicted every sign of the ~$20 sweep the second.

**That fix is built and merged into the branch, with its guard** — `EngineArmsAnswerUnderTheSameContractAsDense`,
mutation-checked: restoring the previous value compiles at 0 warnings and fails on a string-start
mismatch, in 0.5 s rather than a 6.5-hour paid run. **But the 400-query subset showed the fix is not
sufficient**, so the decision that matters now is which of three ways to make the arms comparable:

1. **Apply grounding and abstention only at each engine's final synthesis step**, leaving fragment
   calls under fragment-appropriate instructions. Correct, and the engines do not expose that seam —
   real work in `Rag.NET.AnswerEngines`, on product surface rather than in the harness.
2. **Drop abstention from the shared contract** (keep grounding and extraction) and score abstention
   separately as its own metric. Smallest change that makes the comparison mean something; slightly
   redefines what the DoD clause measures.
3. **Compare engines only against `chatengine`**, accepting that engine-vs-`dense` mixes in prompt
   effects. Cheapest, and leaves `mapreduce`'s per-chunk problem untouched.

**Validate any of them on a 400-query subset before funding the full sweep.** That pattern has now
paid for itself once: ~$3 found three problems that ~$20 would have found no faster.

**Budget the re-run at 6.5 hours, not 3–4.** The protocol's estimate came from extrapolating the
nine-query pilot's rate and was wrong by ~2×. That is the second time here a pilot rate has failed
to survive extrapolation, after RAPTOR's factor of eight; the pattern is now well enough evidenced
to plan against.

Until then, **HyDE and reranking's re-measurement under the
Real protocol remains the cheapest thread open in the phase that needs no money and no
provisioning** (see below) — it can proceed in parallel with waiting for the pilot machine, not
instead of the pilot.

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
HyDE, reranking, hybrid BM25, late chunking, SPLADE, ~~the three answer engines as arms~~ (**built
2026-08-28**, not yet merged, not yet run — see above), every vector
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

> **This section no longer records a branch name, and that is the fix for the defect described
> below.** A branch name is a *mutable pointer*: it is correct only while its branch is unmerged,
> and it goes wrong at the exact moment the branch merges — which is the one moment nobody is
> editing this file. It went stale **seven times out of seven**, every single time, and three
> separate sessions "fixed" it by writing a fresh name that was itself stale within the day.
>
> **Derive the branch instead — `git branch --show-current`.** It is one command, it is always
> right, and it cannot rot.
>
> What this section records now is **immutable**: what last landed on `main`, as a commit SHA, with
> a symbol to verify it by content. Commits do not move. If you need to know whether that work is
> really on `main`, grep for the symbol — do not trust a PR's MERGED label, which has been wrong
> here before.

**Last landed on `main`:** **#452** as `d7d20666` (2026-09-03) — late chunking measured on three
corpora, and the `MaxTokens` shipped defect it exposed. Verify by content: `MaxTokens { get; set; }
= 256` in `src/Rag.NET.Embeddings.Onnx/OnnxTokenEmbeddingOptions.cs`, and the pinned `0.65510` in
`BeirReproduction.cs`.

Before it, in order: **#451** `b3e7473b` hybrid BM25 on three corpora; **#450** `784d3b5c` the nine
`Delivered` sections normalised so the exercise guard can see them; **#449** `9ed26ff8` the HyDE
pipeline-parity test and `QueryTechniques` discharged; **#448** `fd0f8f48` `AnswerEngines`
discharged; **#446** `93134872` ArguAna reranking and the retraction of "parity predicts the sign";
**#444** `8fb7f00a` the three-corpora scope decision.

**Twelve PRs merged across 2026-09-02 and 2026-09-03**, each verified on `main` by content rather
than by a MERGED label.

**This field was seven PRs stale when the session closed — the tenth occurrence of the pattern its
own note above describes.** It goes stale at the moment work merges, which is the moment nobody is
editing this file. The note says to derive the branch with `git branch --show-current` and check
`main` by content; that advice held every time it was followed and this field still drifted whenever
several PRs landed in one sitting.

Before it, **#442** as `05943fff` — FiQA HyDE at 0.34683, and the correction to what SciFact's cell
had been read to mean. Verify by content: `0.34683` in the same file.

Before it, **#441** as `da22a05e` — the roadmap record for HyDE and the scoped gate. Verify by
content: `HyDE's thread completed 2026-09-02` in `docs/planning/ROADMAP.md`.

Before them, **#433** as `e4923341` — HyDE and reranking measured over Rag.NET's own chunking, plus
the fix for the branch's day-old red CI — and **#439** as `f66b1677`, `RAGNET_BEIR_LONG_RUNS` scoped
to named datasets. Verify by content: `UnderCachedHyde_` (underscore load-bearing) and `IsOptedInFor`
in `BeirRunBudget.cs`.

**Five PRs merged 2026-09-02, each verified on `main` by content rather than by a MERGED label.**
Merged main re-run after the first two: 225 tests, 0 failed, 94 skipped.

Before them, **#431** as `8b2e0124` (2026-08-31) — `mapreduce` pinned at 0.6483, a null result, and
the DoD's answer-engine clause closed. Before it **#430** (`ced43abb`) and **#425** (`3640637b`).

**This field was five PRs stale when this session opened** — it named #429 while `main` carried
#425, #430 and #431 — which is the ninth occurrence of the pattern the note above describes. It went
stale the same way as always: at the moment work merged, which is the moment nobody is editing this
file.

Before it, **#419** as `50221812` (2026-08-29) — the FLARE contract-and-cache fix.
Verify by content: `FragmentProtocol` in `src/Rag.NET.AnswerEngines/FlareAnswerEngine.cs`,
`ThrowIfUnkeyable` in
`benchmarks/Rag.NET.Benchmarks.Quality.GraphExtractions/CachedGraphRagClient.cs`.

Before it, **#418** as `e7563873` (2026-08-29) — gives every engine arm the judge's
extraction contract as `RagOptions.SystemPrompt`. Verify by content:
`SystemPrompt = MultiHopRagAnswerJudge.AnswerInstruction` appears in
`tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs`. This field was stale
by two PRs (#417 `b5a48a94`, #418 `e7563873`) when this session opened; corrected then.

Before it, **#417** as `b5a48a94` — fixed this field the previous time and recorded what the
provisioned machine measured. Before that, **#416** as `d2d96b0d` (2026-08-28) — the five
answer-engine arms and their pilot gates (`AnswerEngineArms.cs`, `AnswerEngineArmsTests.cs` under
`tests/Rag.NET.Benchmarks.Quality.IntegrationTests/`, `chatengine` in `AnswerArm.cs`).

**#419 landed on `main` 2026-08-29 as `50221812`, verified by content** — `FragmentProtocol` and
`ThrowIfUnkeyable` are both present there — **rather than by the PR's MERGED label.** #418 broke
FLARE — a terminal `SystemPrompt` fighting FLARE's own fragment protocol produced an 86,091-byte
runaway, 23× the historical maximum, because `CachedGraphRagClient` also discarded FLARE's
`MaxOutputTokens` guard. Twelve commits fix the fragment protocol, the cache
key (a new optional field, omitted rather than emptied, so all 86,510 existing entries keep their
keys), the client's option-forwarding, and the harness's contract application. A re-run pilot,
2026-08-29, passed 15/15 with 0 skipped, and every engine arm now meets the judge's extraction
contract on 8 or 9 of 9 queries (up from 0 of 9). Full account in
`docs/planning/ROADMAP.md`'s 6.2.1 block and `docs/plans/2026-08-29-flare-contract-pilot-notes.md`.

**This paragraph said "Not on `main`… in flight, not merged" until #419 merged, which is the eighth
time a claim in this file has been falsified at the exact moment its branch landed.** It was true
when written and inverted an hour later. The Working State field above was redesigned to be immutable
for precisely this reason; prose elsewhere in the file is not, so a merge-status sentence anywhere
but that field is a liability with a short shelf life.

**Nothing here needs updating when a branch merges** — only when new work lands, which is the moment
someone is already editing this file.

## Measured 2026-08-28 — the machine was provisioned all along

**Both things this session called unmeasurable were measurable.** `~/.cache/ragnet-beir` holds the
corpus, `model.onnx`, `vocab.txt` and 256 embedding shards, and ships an `env.sh` that points the
harness at them. The tests skip because **no environment variable is set**, not because anything is
missing — and three sessions read that skip as "this machine cannot measure" and wrote it into the
record. Source `env.sh` before writing *unprovisioned* anywhere.

- **Pipeline parity, both legs: PASS**, zero skipped, 90.5 s. The SciFact leg ran for the first time —
  20 queries, real ONNX embedder, `AblationRow.Dense` against a real `AddRagNet` pipeline over one
  shared store, chunk ids and exact scores identical at every rank. **The sixteen default retrieval
  behaviours are no-ops on real data**, which until now was asserted only by reading.
- **Answer-engine pilot, 10 queries, 6 arms: PASS**, 15 tests, 0 failed, 0 skipped, 41 m (most of it
  cache-replayed graph construction). All three gates held first time, including the lookahead gate
  whose guarantee had been wrong in four successive versions.
- **FLARE measured at ~11 calls per query against a ceiling of 33**, so the full sweep is on the
  order of $5–10 rather than the derived $4–$21. Full reading in `ROADMAP.md`'s 6.2.1 entry.
- **The predicted format-versus-reasoning confound is real and visible in the answers**: `dense`
  answers "Trump" and scores correct where every engine answers discursively and scores wrong. No
  accuracy headline is published from nine queries.

**The seven occurrences are recorded below and are left as written.** They are the evidence that
the field was structurally broken rather than repeatedly forgotten, and the reason it was replaced
above rather than re-filled an eighth time.

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
