# Milestone 6: Hardening & v1.0 — Battle-Tested

**Status:** active
**Started:** 2026-08-15, the day Milestone 5 was audited and archived

Completed milestones are archived under `docs/planning/milestones/`. Milestone 5 was archived
there on 2026-08-15 after its close audit — `docs/plans/2026-08-15-milestone-5-audit.md`, verdict
PASS on all five criteria.

> **The ROADMAP is authoritative for the DoD below; the two must agree, and when they do not, this
> file is the one that is wrong.** This file drifted twice in Milestone 4/5 and was caught by an
> audit both times; it is rewritten at every milestone boundary for that reason.

> **This milestone was re-planned before it opened**, at the operator's request on 2026-08-15 —
> *"make Milestone 6 about testing every available feature so we are battle-tested"* — from
> `docs/plans/2026-08-15-milestone-6-battle-tested-replan.md`. The ROADMAP's Milestone 6 section
> is rewritten from that note. **Both of the note's operator decisions are now closed, and they
> closed differently.**
>
> **The recordings question was decided on 2026-08-20, against the note's recommendation.** The
> note proposed that `<VerifiedByReason>` carry the gap where no credentials exist, on the grounds
> that a criterion satisfiable only by credentials that may never arrive is not falsifiable. The
> operator decided instead that **6.1's recordings do gate v1.0**, with 6.1's *work* postponed
> behind 6.2.3. Recorded with the trade-off attached because it was raised at the time and
> accepted: v1.0 now waits on 18 cassettes whose blocker is accounts rather than effort, so if
> those accounts do not arrive, the tag does not either. `<VerifiedByReason>` remains implemented
> and enforced; it is simply no longer the answer for the connectors.
>
> **The #247 / #239 question was settled by events rather than by a decision**: both fixes are on
> `main` as of 2026-08-18 (#311, #312; #296, #291), so they ship in v1.0 unless someone reverts
> them. Noted as such because a decision nobody made is worth distinguishing from one that was
> taken.

## Goal

Every shipped feature exercised for real — a real model, a real corpus, a real store, a real
file, a real service or a recorded one — with the evidence pinned and checked by a test, before
v1.0. Milestone 5 showed why: `Rag.NET.GraphRag` was `VerifiedBy=unit`, `✅ Done`, green, published,
and running it once found eight defects. The dense path, calibrated against four published figures,
is the counter-example: every defect ever found in it was found by that calibration.

## Definition of Done

Authoritative copy in the ROADMAP's Milestone 6 section, in Phase 4.0's falsifiable style.

- [x] **Milestone 5 complete** — closed 2026-08-15 by audit, verdict PASS.
- [ ] **All planned phases complete** — what remains is **three**: 6.1 Recorded Responses
      (blocked on accounts, still gating the tag), 6.2.1 Retrieval & Answer Sweep (active), and
      6.3 Release v1.0. Complete: ~~6.0 Inventory~~ (2026-08-15), ~~6.2 Raise the Floor~~
      (substantially, 2026-08-17), ~~6.2.2 Requested Features~~ (2026-08-16), ~~6.2.3 Corpus-Level
      RAPTOR~~ (2026-08-21), ~~6.2.4 RAPTOR Retrieval Over-Fetch~~ (2026-08-21), ~~6.2.5 Contract
      Defects~~, ~~6.2.6 Package Boundaries~~, ~~6.2.7 Named Pipelines~~, ~~6.2.8 Requested DX and
      Chunking Quality~~, ~~6.2.9 `Umap.Fit` at Corpus Scale~~, ~~6.2.10 Vector-Store
      Initialisation~~, ~~6.2.11 HTML Structure and a Guid Seam~~ (all 2026-08-25), ~~6.2.12
      Dogfooding Defects~~ (2026-08-26).
      *(This line listed 6.2.5–6.2.10 as outstanding until 2026-08-27, five days after their
      code was on `main`, and never listed 6.2.11 or 6.2.12 at all — the third time this file
      has drifted from the ROADMAP. A status is written when a phase is planned, and nobody is
      editing this file at the moment its PR merges.)*
      *(6.2.5–6.2.9 added 2026-08-25 from the GitHub backlog, pre-tag on the operator's
      decision. Consistent with 6.2.2's charter: this is the terminal milestone, so a
      request filed against a published package has nowhere later to land.)*
- [ ] **Every `✅ Done` row in `features.md` names what exercises it** — an *Exercised by* column,
      pointing at a test or benchmark that runs the real thing, and a conventions test that fails a
      ✅ row with an empty column. Today: 56 rows, 0 pointers.
- [ ] **No package remains at bare `VerifiedBy=unit`** — each is `integration`, `container`,
      `recorded`, `benchmark`, or carries `<VerifiedByReason>` naming the service and the gap; the
      ledger test fails a bare `unit`. Today: **22 of 73**, down from 57 when 6.0 wrote the list.
      Of the 73: 29 `integration`, 24 `unit` (22 bare, 2 with a reason), 11 `container`, 5
      `benchmark`, 1 `recorded`, 1 `live`. `recorded` and `live` were both used for the first time
      on 2026-08-17 — `Rag.NET.DataProviders.GitHub` and `Rag.NET.Parsers.Vision`.
      *(`integration` was added 2026-08-16 in Phase 6.2. The level set enumerated here could not
      express "exercised against something real that is not an external service" — a real file, a
      real process, a real host over its real transport, a real store reopened — which is exactly
      what §2 of the 6.2 design defines as the bar for twenty-five of these packages. They met it
      and the ledger had no way to say so. Amending a DoD mid-milestone is not free, and the
      alternative was worse: leaving twenty-five packages at a `unit` that had become false, or
      spending the `<VerifiedByReason>` escape hatch on packages that are exercised.)*
- [ ] **Every retrieval technique and answer engine has a pinned figure with a control** — the
      GraphRAG method (5.2 / 5.2.1 / 5.2.2) applied to RAPTOR, HyDE, hybrid, reranking, late
      chunking, SPLADE, and the three answer engines; every vector store reproduces the SciFact
      parity figure through itself; a pipeline-parity test holds a real `AddRagNet` pipeline to the
      harness's top-k on every push.
- [ ] **The release commit is green on both `ci.yml` matrices**, the Docker tier and the latest
      nightly green on Linux, stated as such.
- [ ] **Release tagged v1.0.**

## Phases

| Phase | Name | Issues | Status |
|---|---|---|---|
| 6.0 | The Inventory | — | **complete** 2026-08-15 — both guards on every push, failing behind a work list: 5 packages at `benchmark`, 57 at bare `unit` owned by 6.1/6.2/6.2.1; 51 Done sections, 2 exercised, 49 owned |
| 6.1 | Recorded Responses | #283, #290, #354 | **postponed but moving** — **the first outside cassette landed 2026-08-25** (#354, contributed by @StefH against a real Azure Document Intelligence resource). It is a strict superset of the hand-written fixtures — `paragraphs`, `styles`, `contentFormat`, per-word `polygon` — and hides no defect today, because `BuildPageText` reads only `Pages`→`Lines`/`Words`→`Content`. Still **gating v1.0** — sequenced behind 6.2.3; the gate was kept rather than handed to `<VerifiedByReason>`, so the tag waits on accounts. Previously in progress: the harness did not work and now does. Record mode had two defects, both silent because recording proxies to the real service and therefore passes: recordings were written to a directory replay never read, and every mapping matched on `Host: localhost:{ephemeral port}`, which cannot match twice. Fixed in #290, which also recorded the first working cassette (GitHub, unauthenticated, 17 KB). #283 carries the corrected instructions for the remaining 18 services; the blocker is accounts, not work |
| 6.2 | Raise the Floor on Unit-Only Packages | #286–#292 | **substantially complete** — 57 bare `unit` down to 22. Defined per kind: parsers/chunkers via a real file, stores via the parity leg, utilities via one real run. Every package picked up produced a defect in something adjacent: `Parsers.Audio` was filed as needing a hosted model and needs none (and is broken on Linux without `libgomp1`), `Parsers.Vision` had no CI tier that could run it, `DataProviders.Web`'s crawler yielded the seed page twice (#288). Remaining 22 are 6.1's credential-blocked connectors plus `Chunking.Templates` and 6.2.1's three |
| 6.2.1 | Retrieval & Answer Sweep | — (all four named debts closed) | **active** 2026-08-20 — the GraphRAG method applied to the rest. **#247 closed 2026-08-18**, fixed twice over (#311 hides graph chunks from retrieval by default, #312 gives them their own store) and pinned at 0.3494 (#280); #239 and #200 closed 2026-08-17. The local-search thread completed 2026-08-20 (#323, #326). **#345 merged 2026-08-22 in #351 (`bb4c11c7`), unblocking Tasks 4-6 of the RAPTOR measurement** — `TargetClusterSize` floors the cluster count so a level's *average* cluster stays within the model's context; without it the corpus tree could not be built at the shipped default. The sweep itself has not started: RAPTOR first, then HyDE, reranking, hybrid, late chunking, SPLADE, the three answer engines, the stores through the parity leg, the pipeline-parity test, #176, local search's unexplained yes/no abstention, and the now-unblocked deletion of the deprecated blend members. **#176 answered 2026-08-26 in #405** — by naming the dropped relationship endpoints rather than counting them: 565 distinct names, overwhelmingly common nouns and paraphrases, so promoting them into entities would trade a better singleton number for a worse graph. Nothing changed on it; the diagnostic stays. **That closes the last of the phase's four named debts, and the sweep itself is what remains** |
| 6.2.2 | Requested Features | #252 | **complete** 2026-08-16 — #252 built, both open design questions settled, exercised in the fast tier and over a real HTTP server |
| 6.2.3 | Corpus-Level RAPTOR | #331, #332, #333 | **complete** 2026-08-21 — merged in #340. `RaptorIngestionBehavior` clusters `ctx.EmbeddedChunks` and `IngestionContext` holds one document, so RAPTOR builds a per-document tree — #300's shape, and not the paper's mechanism. The fix needs RAPTOR to own persistent leaf-embedding state the way GraphRAG owns `IGraphStore`, because `IVectorStore` cannot enumerate and `IChunkLookup` returns `TextChunk` without vectors (#318). Roughly #302 plus #312. Both scopes stay selectable, per #323's precedent, so the 6.2.1 measurement prices old and new in one run. **Three defects were fixed and three more filed:** #332 (summary chunks collided on `ChunkIndex` across levels) and #333 (`SelectK` returned k=n, so a level never reduced and the tree loop never terminated — an unbounded LLM spend at shipped defaults) were both found by *reading*, not by any test. The reason no test caught them is the phase's most useful finding: a mock embedder re-seeded per call returned a byte-identical vector for every summary, so **no test had ever built a tree deeper than one level**, and both defects need depth ≥ 2 to manifest. Two more fixtures of the same shape turned up while fixing it. Left open and documented: #336, #337, #338 |
| 6.2.4 | RAPTOR Retrieval Over-Fetch | — | **complete** 2026-08-21 — merged in #344, added and closed the same day. `RaptorRetrievalBehavior` passes `ctx` to `next` unmodified while `VectorStoreBehavior` fetches exactly `TopK`, so it only sees the truncated top-k: **`Boost` promotes within the set but never into it** (a summary ranked 7th that would beat 6th at 1.2× cannot appear — #239's shape), and **`Filter` under-fills** (ask for 6, get 3). `MmrBehavior`'s `?? TopK * 3` over-fetch is the pattern and the precedent for the default. `Blend` must stay byte-identical — figures are pinned against it. Lands *before* the measurement because the defect is structural and provable from two lines, unlike #247's, which was empirical; the control survives at 1× rather than by leaving the defect shipped |
| 6.2.5 | Contract Defects | #350, #328, #360 | **complete** 2026-08-25 — added and shipped the same day across three PRs: #350 in #372 (breaking: `IBm25Index.Search` takes the metadata filter, so the BM25 arm of client-side hybrid stops ignoring it), #360 in #373 (a benchmark run whose query selection is empty now fails instead of passing on zero queries and printing `NaN`), #328 in #374 (`KNearestNeighborsCount` no longer set below Azure's own default). **#328 stayed open**: the commit title names it, but the merged fix was `KNearestNeighborsCount` and the semantic ranker it actually asks for was split off pending the score-scale decision — a commit naming an issue is not evidence the issue is done |
| 6.2.6 | Package Boundaries | #339 | **complete** 2026-08-25 — added and shipped the same day in #376. `SqliteAuditLog` moved to a new `Rag.NET.Security.Audit.Sqlite` package and `UseAuditLog()` was removed rather than kept as a runtime check, so "auditing configured, nothing recorded" cannot be expressed — forgetting the migration is a compile error. Package count 71 → 72 |
| 6.2.7 | Named Pipelines | #342 | **complete** 2026-08-25 — added and shipped the same day in #381. `AddRagNet(name, configure)` gives each name its own child container; the unnamed form is unchanged, which was the hard constraint. Three Criticals found in review, and four tests in the plan itself could not have failed for what they claimed |
| 6.2.8 | Requested DX and Chunking Quality | #366, #365, #353, #355 | **complete** 2026-08-25 — added and shipped the same day in #378, three of its four items: #366 (heading-only chunks skipped after the breadcrumb is recorded, so the hierarchy survives), #365 (`ChatAnswerEngine` sources move to a `Tool` message), #355 (`Failed` distinguished from `Skipped` — but fail-fast was never implemented or answered, so the issue stays open carrying a question). #353 was split out into 6.2.10 during design |
| 6.2.9 | `Umap.Fit` at Corpus Scale | #348 | **complete** 2026-08-25 — added and built the same day, in #382. Measured before changing anything: the kNN graph is **92% of `Umap.Fit`'s time and 98% of its allocation**, so the issue named the right target. Bounded k-selection replaced the full sort and the row loop parallelises above 512 rows — **5.2× faster, ~729× less allocated, Gen0/1/2 all to zero**, two runs per state on an idle machine. It also corrected two of its own issue's claims: the level-1 reduction is ~6% of the corpus tree build, not "real time", and the sort-to-selection change everyone would call the headline bought 21% on its own |
| 6.2.10 | Vector-Store Initialisation | #353 | **complete** 2026-08-25 — added and built the same day, branch `feat/353-vector-store-init`. Split out of 6.2.8 during design because it looked structural: `InitializeAsync` is not on any interface, and `ICollectionManageable`'s `CreateCollectionAsync` makes a *named* collection rather than the bound index. Neither of the two costs the design section priced had to be paid — its premise was wrong and the pattern already existed in the codebase |
| 6.2.11 | HTML Structure and a Guid Seam | #375, #371, #380 | **complete** 2026-08-25 — added and shipped the same day across two PRs: #375 and #371 in #385 (the sibling walk replaced by a document-order traversal over text nodes, plus `HtmlHrefHandling`), #380 in #386 (`IGuidProvider`, an interface rather than the abstract class the issue proposed, returning `Guid` rather than `string`). Verified on `main` by content — `HtmlHrefHandling.cs` and `IGuidProvider.cs` are both present — rather than by either PR's MERGED label |
| 6.2.12 | Dogfooding Defects — what the first external user found | — | **complete** 2026-08-26 — seven PRs from one reporter (@StefH) integrating the library into a real project, which is the dogfooding this project had not done. **One was silent data loss live at shipped defaults**: under `CleanupMode.Full` a provider listing failure was collected into `Errors` and dropped, so one failed sitemap page deleted every document behind it and the run reported success (#402 — the single `stoppedEarly` bool became a `CleanupBlocked` reason). **Two of the defects were caused by fixes earlier in the same phase** — #390's fix deadlocked Blazor (#396), whose fix still hung on host singletons (#400), fixed by forwarding lazily (#403). And the reported "lock" was an unconditional `Task.Delay(1s)` per document in `AzureAISearchVectorStore.StoreAsync`, so a 500-page ingest spent eight minutes asleep for nothing (#401). **None of the four had a test and the suite was green throughout** |
| 6.3 | Release v1.0 | — | pending — **but its first work is already done**: 71 packages live on nuget.org at 0.1.0 since 2026-08-11 (verified 2026-08-16), so the account, key and every package ID are settled. Only the v1.0 tag remains. ~~Now gated on 6.2.3~~ — cleared 2026-08-21. Still gated on 6.1's recordings (the operator kept that gate) and 6.2.1's sweep |

## Known debt carried into this milestone

- **#247 closed 2026-08-18** — one shared store for article and graph-derived chunks, measured
  −0.043 nDCG and −0.21 answer accuracy: the largest lever any Milestone 5 measurement found, and
  the one debt this milestone opened pointing at. It resolved in the order the method prescribes.
  **Measured before it was fixed** (#274): at n=50, top-6, a `filtered` arm that dropped the
  graph-derived units after over-fetch reproduced the article-only `dense` arm to four decimals on
  both scoring rules — 0.2727 against a polluted control's 0.1364 — so the entire loss was
  *displacement*, not any cost of the graph store existing. The mechanism confirmed itself by
  accident: 46 of 50 queries hit the answer cache, which is keyed on a prompt embedding the
  context, so for those 46 the filtered top-6 was byte-identical to the article-only context.
  **Then pinned** at 0.3494 (#280). **Then shipped**, and only then: #311 hid the graph's chunks
  from retrieval results by default, #312 gave them their own store — option (b), Microsoft's
  shape, though (c) had already recovered 100%. The three options separated on evidence rather
  than on cost. *(RAPTOR indexes synthetic summaries the same way and is expected to take the same
  fix; that is why it is 6.2.1's next thread.)*
- **#239 closed 2026-08-17.** Its three findings resolved differently, which is worth recording
  because only two were code. **Point 1, the blend:** `PageRankWeight` defaulted to 0.3 while
  PageRank normalises to a mean of 1.6e-5 against cosine's 0.3–0.6, so the behaviour demoted
  precisely the chunks it had traversed to — at `w = 0` local search reproduced the candidate-set
  control on **2,255 of 2,255 queries**, so the entire −0.02761 was that default. Now 0 (#296).
  **Point 3, the discarded calls:** two graph queries awaited and thrown away, ~45,000 table scans
  per query pass, removed (#291) — and a test had asserted `Received(1)` on them, pinning the waste
  as coverage. **Point 2, that local search can only reorder and never expands the candidate set:**
  *not* changed. Measured at +0.00148 Recall@100 if the walk's findings were used; closed as a
  documented limitation, and `docs/guide/graphrag.md` now states plainly that the behaviour adds no
  candidates.
- **#176 singleton communities — answered 2026-08-26 in #405; not a defect worth fixing.** The
  numbers first: it carries 65%, and the full 609-article corpus measured 2026-08-20 gives **2,816
  singletons of 3,573 — 78.8%**. **Not a regression** — the 65% is 396 of 607 over the pinned
  **60-article** slice (#168), so the two were never measuring the same graph; 78.8% is the first
  full-corpus reading and it moves the wrong way with scale. The issue's own diagnosis stood: it is
  extraction, not clustering.
  **What settled it was reading the dropped endpoints' names rather than counting them.** 853 of
  16,403 relationships (5.20%) are dropped because an endpoint resolves to no extracted entity,
  stranding 123 entities that do have edges — and 273 + 123 is exactly the slice's 396. Those drops
  name **565 distinct** endpoints which are *not* entities the extractor missed: `content policies`,
  `tasks`, `smart plug`, `handy tool`, `film`, `ceremony`, `death` — common nouns — beside
  paraphrases of things that are extracted (`Falun Gong practitioners` next to the entity `Falun
  Gong`). **Promoting them into entities would drive the singleton share down while adding 565 junk
  nodes**, which is a better-looking number over a worse graph; the singleton count is the metric
  easiest to move without helping. Nothing was changed. Any real fix belongs in the extraction
  prompt and must be measured against retrieval. The diagnostic stays, printing the counts and a
  ten-name sample. *(**#200** usage recording, **#246** the
  Service Bus emulator lock race, and **#104** routing all closed. #104 was listed here in error;
  it closed 2026-08-10, before this milestone opened.)*
- **#297 and #300, both found by questions rather than planned, both closed 2026-08-17.** The graph
  store had **no indexes at all**, and the obvious fix did not work: an index on
  `relationships(source_entity)` was unusable while the predicate said `COLLATE NOCASE`, because an
  index's collation must match the predicate's. Fixing that meant fixing #299's collation bug first
  (#304). And community detection — a whole-graph operation — ran **once per ingested document**,
  17,648 times on the MultiHop-RAG corpus, every run but the last discarded; now debounced on graph
  growth with `GraphProjectionRebuilder` for on-demand rebuilds (#302).
- **#298 open — should the graph store support backends beyond SQLite?** Recorded answer: *not
  yet*, and weaker now than when asked. The two costs attributed to storage were a missing index
  and a per-document recompute, both since fixed without changing engines. Concurrency (one shared
  `SqliteConnection` on a singleton) is the only remaining argument, and nobody has stated that
  requirement.
- **#299 open — multilingual.** Two of its three defects fixed 2026-08-17: SQLite's `COLLATE
  NOCASE` folds ASCII only while the callers fold Unicode, so non-English entity names were one
  entity in memory and two rows in the graph (#304); and BM25 tokenised a whole CJK sentence as one
  term, so hybrid search silently degraded to dense-only for Chinese, Japanese and Korean (#305,
  which also made Vision's OCR language configurable). **What remains is not code**: there is no
  multilingual measurement, so none of this is a multilingual *claim*. MIRACL or mMARCO would fit
  `BeirDatasetDescriptor`'s existing shape.
- **#252** `SitemapDataProvider` cannot skip URLs — reported 2026-08-15 against a shipped package.
  6.2.2. **Closed 2026-08-16**: `SitemapOptions` with prefix and regex exclusions, applied to
  nested index links as well as page URLs.
- **The #300 follow-up measurement is outstanding**: the split of `BeirRunBudget`'s 22 m 18 s graph
  construction between LLM extraction, the `O(occurrences²)` description rewriting and the
  recompute. #302 should have moved that number and nobody has re-measured. It needs the
  provisioned corpus **and an idle machine** — three timing runs attempted on 2026-08-17 disagreed
  by 6× on identical inputs, so no coefficient was claimed anywhere.
- **TREC-COVID's weakest-of-the-datasets agreement** (−0.018 in ±0.02) — nothing yet looks into
  it; 6.2's parity-through-stores runs the same leg many more times and is where it would show.
- **Generation lives in the answer test class rather than the tool** (5.2.2's stated deviation).
- From Milestone 4: the Azure Document Intelligence live half, the AzureAISearch OData filter path,
  the Pinecone live sparse-write verification — all 6.1's.

## Explicitly not in scope

- **Making every feature good.** Battle-tested means measured; a feature measured and found wanting
  is a completion, as 5.2 was. *(6.2.2 is the one deliberate exception, and is scoped to named,
  reported requests against shipped packages — not to improvement in general. It exists because
  this is the terminal milestone, so a request filed against a published package has nowhere
  later to land.)*
- **Every option on every feature.** One real run per feature at its shipped defaults, differenced
  against a control, is the bar; anything above it is a phase with its own name.
- **Anything on the "Beyond v1.0: Recorded, Not Scheduled" list.**
