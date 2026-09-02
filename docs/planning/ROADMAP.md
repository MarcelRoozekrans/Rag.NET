# Project Roadmap

Backlog source: the unchecked items in `docs/reference/features.md` (31 items as of 2026-07-24).
Every backlog item is assigned to exactly one phase below. When a phase completes, tick the
corresponding rows in features.md.

## Recorded follow-up debts (cross-phase, from review cycles)

Anything added here follows one rule: record it with its origin, then schedule it into a
phase or re-justify it. Closed items move to the list below rather than vanishing, so a
future reader can tell the difference between "never existed" and "dealt with".

- **Seven guide pages are unreachable from the sidebar** (found in the Phase 3.4 Part D review):
  `sidebars.ts` omits `guide/security`, `guide/memory`, `guide/resilience`, `guide/data-providers`,
  `guide/mediator`, `guide/graphrag` and `guide/raptor`. They exist and are linked from other
  pages, but nobody browsing the sidebar will find them — including the security guide, which is
  the one a reader is most likely to go looking for deliberately. A sweep, not a fix per page.
  → **Phase 4.5** (with the sample applications, which is when the docs get read end to end)
- **A streamed prompt does not correlate without an ambient activity** (found in Phase 3.4 Part C):
  `ChatAnswerEngine` assembles the prompt *after* its first `yield return`, so the diagnostics
  callback runs on the consumer's execution context, where the span the pipeline started inside
  its own iterator is not ambient. Probe-verified. Chunks, stages, the commit and the
  non-streamed prompt are all unaffected — only the streamed prompt field, and only when the host
  supplies no ambient activity of its own (so: fine under ASP.NET, absent in a console app).
  Pre-existing rather than caused by 3.4: `ragnet.ask` was already started inside an async
  iterator. A one-line `Activity.Current = activity` before `BuildMessagesAsync` closes it, and
  was deliberately not taken in 3.4 — it mutates ambient state in the answer engine for a
  diagnostics benefit, and that phase spent its one production edit on the `ragnet.query` span.
  → **Phase 4.3** (re-pointed 2026-08-02 by the Milestone 4 replan, design §5, from 4.4: it
  travels with the `MessageChild` union in 4.3's slot list, and the structured-logging pass works
  the same streamed answer path. The span-context reasoning that argued for 4.4 still holds — if
  4.3 leaves it, 4.4 inherits it rather than the milestone at large)
- **`MessageChild<TMessage>` is a union by convention** (**created by Phase 3.9**, not pre-existing):
  `EmbeddedMessage != null` means "descend", and otherwise `OpenAsync` and `MimeType` must *both* be
  non-null. Nothing enforces that — the only check is a bare `yield break` in
  `EmbeddedTraversal.DispatchAsync`. Both shipped adapters construct it correctly, so this is latent
  rather than live, but a future adapter that sets `MimeType` and forgets `OpenAsync` drops every
  attachment with no log line at all. The recursion this replaced made that state unrepresentable,
  so 3.9 traded a compile-time guarantee for a runtime convention and did not say so.
  **Rescheduled out of Phase 3.10 on 2026-07-30, because the reason it was scheduled there was
  wrong.** 3.10 was expected to "add a third container shape to the same type"; it added none.
  `MessageChild<TMessage>`, `IMessageAdapter<TMessage>` and `EmbeddedTraversal` model an *email
  message tree* — live library message objects, descend-or-open — and 3.10's promotion deliberately
  left all three `internal` to `Rag.NET.Parsers.Email` while moving only the four container types.
  `ZipDocumentParser` enumerates `ZipArchive.Entries` itself and calls `ContainerEntryDispatcher`
  directly, so it constructs no `MessageChild` and the type still has exactly the two adapters 3.9
  left it with. The debt is therefore unchanged rather than closed: still latent, still two correct
  adapters, still nothing enforcing the rule.
  **The real trigger is a third `IMessageAdapter<TMessage>`** — another message library, not another
  container format — and no phase on this roadmap adds one. Scheduling it against a trigger that may
  never fire is how a debt becomes an open note, so it gets a backstop instead: whichever comes
  first.
  → **the next phase that adds an `IMessageAdapter<TMessage>` implementation, and failing that
  **Phase 4.3** as the owning slot** (assigned 2026-08-02 by the Milestone 4 replan's §5,
  replacing the bare milestone-as-deadline; the fix is small and local — a sealed hierarchy or a
  private constructor with two factory methods — so it needs a slot, not a phase of its own)
- **A twice-seen, twice-unnamed test failure in `Rag.NET.Benchmarks.Quality.Tests`** (seen once
  during Phase 3.16, **not reproduced in 86 subsequent runs** — 26 solo, 45 under three-way
  concurrency, 15 under a concurrent full-solution build — then **seen a second time during the
  whole-phase review**: `Failed: 1, Passed: 109` on the reviewer's first run, then 110/110 on nine
  subsequent runs including four against a byte-identical binary). Investigated and explicitly
  **not diagnosed**; recorded so the next occurrence starts from evidence rather than from zero.
  Ruled out with evidence: the
  project's dependency closure is byte-identical to `main` (`git diff main...HEAD` over both its
  src and test directories is empty, and the src project has zero ProjectReferences), so it cannot
  involve the 3.16 branch; no shared mutable state — every filesystem test class uses a GUID-unique
  temp root; `RAGNET_BEIR_CACHE` is read by no unit test; `EmbeddingCache` writes are atomic via a
  GUID-suffixed `.partial` plus `File.Move(overwrite: true)`; and there is no `DateTime`, `Random`,
  `Task.Run` or `Thread` anywhere in the project. One structurally fragile thing was found and is
  flagged as a **candidate, not a diagnosis**: three `Dispose()` methods call
  `Directory.Delete(_root, recursive: true)` with no retry or catch, which on Windows throws
  intermittently when a transient handle — antivirus, search indexer — is open on a just-written
  file. The right shape, and still undemonstrated — the second sighting neither confirms nor
  clears it, so it stays a **candidate**.
  **All three sightings lost the test's name**, and the third one changes what the instruction
  should be. It happened on 2026-08-02 during Phase 3.8's documentation task, on a docs-only tree
  (1 failed / 128 passed; the immediate re-run passed 129/129) — and the name was lost because the
  run was piped through `tail -3`. The second was lost to summary-only logging.
  **Twice now the standing instruction has been defeated not by forgetting it but by tooling**,
  which is the signal that "capture the next occurrence" is the wrong shape of instruction: by the
  time you know it is the occurrence, the evidence is already gone.
  **So it stops being a reaction and becomes the default: every run of
  `Rag.NET.Benchmarks.Quality.Tests` uses `--logger trx`, and its output is never piped through
  `head`/`tail`/`grep` in a way that can discard a failing test's name.** The failing test's name
  remains the one piece of evidence needed, and three sightings have now produced none of it.
  **The candidate fix has since shipped — into the one file this entry does not name** (found by
  the 2026-08-02 audit). Phase 3.15's `HypotheticalCacheTests.cs:34-61`, the project's fourth
  filesystem test class, wraps its `Directory.Delete` in a two-retry mitigation and cites this
  very debt in its comment — while the three `Dispose` methods this entry names
  (`BeirLoaderTests.cs:47`, `EmbeddingCacheTests.cs:33`, `BeirDatasetCacheTests.cs:39`) still
  delete with no retry. So the candidate cause is mitigated in the one class where the flake has
  never been seen and unmitigated in the three where it may have occurred — backwards whichever
  way the diagnosis lands, and nobody had recorded the asymmetry. Deliberately not spread by the
  audit: this entry's rule is trx-before-anything, and while the asymmetry stands, a failure
  landing in the three bare classes and never in the retrying one is itself weak evidence for the
  candidate. The suite figure above is stale too: the project is **129** tests now, not 110 —
  3.15 added the fourth class and more.
  → **the next occurrence, and failing that Milestone 4 as a deadline** [backstop re-examined and
  deliberately kept 2026-08-02, when the replan converted the other milestone-as-deadline arrows
  to phases: this one hangs off "all test projects passing", which survived the DoD rewrite
  verbatim, and a deadline hanging off a falsifiable criterion is not the shape the replan
  removed; note the `MessageChild` backstop referenced below has since become Phase 4.3]
  [re-checked 2026-08-03, at the v1.0 postponement: unchanged — "all test projects passing"
  stays in Milestone 4's DoD, so this deadline does not move with the tag; Milestone 6's
  both-operating-systems criterion only adds a second, later net behind it] — the
  same backstop shape
  as the `MessageChild` debt above, because "all tests passing" is in that milestone's Definition
  of Done and a suite that has failed once cannot carry that claim uninvestigated.
- **Two live suites have never actually run against the real thing** (surfaced 2026-07-31, while
  reading the first genuine nightly). Both are correctly built and correctly gated; neither has ever
  executed, which is a different claim from "they pass".
  - **`AzureDocumentIntelligenceLiveTests`** needs `RAGNET_DOCINTEL_ENDPOINT` and
    `RAGNET_DOCINTEL_KEY` — a real Azure resource, billed one page per run (free tier covers it).
    Offline coverage is WireMock cassettes, which catch regressions in *our* code; this test exists
    to catch the day those cassettes stop describing the real service, and until it runs once,
    nothing has confirmed they ever did.
  - **`PdfOcrFallbackTests`' OCR case** needs `RAGNET_TESSDATA` **and** `/p:EnableOcr=true`.
    `RAGNET_TESSDATA` is free — a path to `eng.traineddata` from `tesseract-ocr/tessdata`, no account
    — but the MSBuild gate means the test is not skipped, it is **not compiled**, so no run of any
    kind reports on it. This is the third inert guard Phase 3.7 found and the only one still open.
  **The container route was considered and declined.** Document Intelligence does ship as an Azure AI
  container, but those require `Billing` and `ApiKey` pointing at a live Azure resource and meter
  against it — so it keeps the subscription and the per-page cost while adding a multi-gigabyte pull,
  and its version lags the cloud service, so it does not reliably catch the cloud drift the live test
  exists for. Most of the price of the real thing for a weaker guarantee. (Verify Microsoft's current
  container billing and access terms before revisiting; they change.)
  The cheap half is the OCR one: fixing the `EnableOcr` gate costs nothing but a decision about how
  to compile it in CI. The Azure half needs a resource and a deliberate choice to spend a page.
  → **split 2026-08-02 by the Milestone 4 replan (§5), after Phase 4.0's `TestGateTests` put all
  four of these gates on the record as the four (of 28 sites) satisfiable nowhere** — and
  sharpened the OCR half: `ENABLE_OCR` does not merely skip a test, it **compiles the production
  Tesseract engine out**, so the shipped PDF parser has no real OCR in any default build. The OCR
  half (`ENABLE_OCR` + `RAGNET_TESSDATA`, whose only reader sits inside the uncompiled block) →
  **Phase 4.1**, which owns the `ci.yml` rework and where "a decision about how to compile it in
  CI" — this entry's own words — belongs. The Azure half (`RAGNET_DOCINTEL_ENDPOINT`/`_KEY`) →
  **the recorded-responses phase** (design §3), and Milestone 4's new DoD holds both halves either
  way: "no test gated behind a condition nothing satisfies" fails until these gates are
  satisfiable or gone. [The Azure half's destination re-pointed 2026-08-03, at the v1.0
  postponement: the recorded-responses phase is scheduled as **Phase 6.1**, in Milestone 6, and
  the recording criterion moved into that milestone's DoD with it — widened to
  recording-or-recorded-reason. What did **not** move: "no test gated behind a condition nothing
  satisfies" stays Milestone 4's criterion, so the two `RAGNET_DOCINTEL_*` gates must become
  satisfiable somewhere — a fenced, runnable local procedure is what `TestGateTests` accepts —
  or be removed before Milestone 4 closes, even though the recording itself now waits for 6.1.
  That tension lands with 4.1's gate work, said here rather than hidden by the re-point.]
  [**4.1 delivered both halves' Milestone-4 obligations, 2026-08-03 — and the OCR half is
  closed outright** (`107e3e9`): the decision this entry was waiting on is taken. The published
  `Rag.NET.Parsers.Pdf` package **deliberately compiles the Tesseract engine out** — its native
  payload should not ride into every consumer, and Azure Document Intelligence is the packaged
  OCR engine — stated plainly in features.md, `docs/guide/ingestion.md` and the gate-off stub's
  own exception message, all three of which had read as if a consumer project property could
  flip it. It cannot: `EnableOcr` is evaluated when the package itself is built, so the
  advertised per-image Tesseract OCR existed in no shipped binary — the docs' instruction was
  impossible, and the correction says source-build (`-p:EnableOcr=true`), fenced in
  `docs/reference/ci.md`. Not aspiration: all three gated projects build 0 warnings under the
  flag, and `OcrFallback_RealTesseract_ReadsScannedFixture` ran **green on 2026-08-03**
  (tessdata_fast `eng`) — the first execution of that test anywhere, closing the last of the
  three inert guards Phase 3.7 found. The Azure half became **satisfiable**: ci.md carries a
  fenced `az cognitiveservices account create --kind FormRecognizer --sku F0` procedure
  (`ab2d2b4`) — the F0 free tier voids this entry's original billed-per-page premise — and both
  `TestGateTests` ledgers are now empty. Satisfiable and exercised are different claims and only
  the first is made: the procedure has not been executed, the live suite has still never run,
  and its hand-written cassettes remain unconfirmed against the real service — that is 6.1's,
  unchanged. Only the Azure half keeps this entry open.]
- **The ablation table's reranker row permutes only the set it is evaluated on** (found in Phase
  3.15 while writing up the table — **a design flaw in that phase's own plan, not a defect in the
  code**, and the entry says so because the two get fixed differently). The plan set the reranker
  row's `TopK` equal to the evaluation cutoff of 10, so the cross-encoder reorders exactly the ten
  documents it will be scored on and can never surface an eleventh. **Recall@10 is frozen by
  construction**, and the numbers show it: SciFact's reranker Recall@10 is 0.78667, identical to
  dense. A real reranking pipeline retrieves ~100 candidates and reranks down to 10, so the
  published row **understates what a cross-encoder can do** — its +0.0385 on SciFact and +0.0137
  on FiQA are floors of a sort, and part of its −0.0252 on ArguAna may be the protocol rather
  than the model. The plan fixed one `TopK` across all four rows for comparability, and nobody
  asked what a uniform cutoff does to the one technique whose value is reaching below it.
  **The "or labelled" exit condition is already satisfied** (noted by the 2026-08-02 audit, so
  this entry does not read as wholly open): `docs/reference/retrieval-quality.md:406-413` states
  the freeze, the 0.78667 Recall@10 identity with dense, and instructs the reader to take the row
  as "what reordering the dense top-10 does", *never* as the best a cross-encoder can do. The
  page already labels the row for what it is, which was one of the two ways this entry allowed
  itself to close. What remains is only the depth re-measure (~100 in, 10 out), as an optional
  improvement rather than a release blocker.
  → **the next phase that re-measures the ablation table, and failing that Milestone 4 as a
  deadline** — the table ships in `docs/reference/retrieval-quality.md` with the v1.0 docs, and a
  row that understates a shipped component gets re-measured at depth (~100 in, 10 out) or
  labelled for what it is before that page goes out as release documentation. [Labelled — see
  above; only the optional re-measure remains against this deadline.] [Backstop re-based
  2026-08-03, at the v1.0 postponement: this deadline's own justification — the page ships as
  v1.0 release documentation — now points at **Milestone 6**, where the tag moved, so the
  backstop rides there with it. The primary trigger — the next phase that re-measures the
  table — is unchanged, the floor stays met by the label, and only the optional depth
  re-measure remains.]
- **TREC-COVID and EnronQA remain deferred, unchanged from 3.12** (scoped into 3.12, moved to 3.15
  in that phase's scope split, and not run there either — 3.15 spent its budget on the table, two
  library defects and FiQA's real leg). TREC-COVID is still the first graded-relevance dataset,
  and `IrMetrics`' `2^rel − 1` path still has a graded *fixture* but has never seen a graded
  *dataset*; EnronQA is still the private-corpus and multi-tenant story. Recorded here rather
  than carried silently inside a completed phase's scope list, because a deferral that lives only
  in a closed entry is how work disappears.
  **The code contradicts this entry, and at most one of the two is right** (found by the
  2026-08-02 audit): `src/Rag.NET.Benchmarks.Quality/IrMetrics.cs:31-32` states "FiQA and
  TREC-COVID are graded", while this entry says the `2^rel − 1` path has never seen a graded
  *dataset* — which is only true if FiQA's qrels are binary. BEIR's published FiQA-2018 qrels are
  binary, which favours this entry, but that has **not been verified against the actual qrels
  file** and is recorded as unverified. What settles it: read `qrels/test.tsv` in the cached FiQA
  archive and check whether any relevance value exceeds 1. If FiQA is graded, the graded path has
  been exercised by three phases of FiQA runs and this entry's premise falls; if binary,
  `IrMetrics`' own doc comment is wrong and gets corrected when this is picked up. Either way one
  sentence must change, and this paragraph exists so whoever runs TREC-COVID first knows which
  check comes before the run.
  **Settled 2026-08-09 by reading the archive, and this entry was the one that was right.**
  `fiqa/qrels/{train,test,dev}.tsv` from the pinned BEIR URL score **exactly 1 on every row** —
  14,166 / 1,706 / 1,238 rows, no other value, nothing unparsable. **FiQA is binary.** So the
  `2^rel − 1` path still has a graded fixture and has **never scored a graded dataset**, exactly as
  this entry claimed, and `IrMetrics.cs`'s doc comment — which asserted FiQA was graded — was the
  wrong sentence and has been corrected with the counts inline. TREC-COVID remains the first
  graded dataset and remains unrun, so the Milestone 5 DoD criterion it feeds is still open. The
  audit's own guess ("BEIR's published FiQA-2018 qrels are binary, which favours this entry") was
  right, but it was recorded as unverified and stayed that way through four phases that could have
  read one file — which is the more useful lesson than the answer.
  → **stays in Milestone 3's scope** (re-pointed 2026-08-02 by the Milestone 4 replan, design §5,
  which refuses to smuggle it into 4: run or explicitly declined before Milestone 3 closes, and
  declined gets written here, not implied). **And a correction to the design's own §5:** it routed
  the FiQA-qrels check to Phase 4.0 — "one read of the qrels settles it" — but 4.0's plan scoped
  that phase to its three guards and the read was not performed; the check stays exactly where
  this entry put it, first thing for whoever runs TREC-COVID.
  **Declined for Milestone 3, at its close (2026-08-03)** — the explicit decline the replan's §5
  required written here rather than implied. The grounds, so the decision is arguable rather than
  a shrug: neither dataset verifies anything this milestone shipped — three corpora already
  answer the milestone's questions in both directions (real-vs-parity **+0.03148 / −0.02873 /
  −0.01517**, and an ablation table where every technique helps somewhere and hurts somewhere) —
  where TREC-COVID opens a *new* question (the first graded dataset through `2^rel − 1`) and
  EnronQA a new story (the private corpus). And neither is a close-out task: no descriptor, no
  `BeirRunBudget` timing, no revision-pinned published reference and no licence determination
  exists for either — the full checklist every dataset landed so far has needed — against a cost
  that past FiQA is embedding-time in hours. The graded-path risk stays real and stays stated:
  `2^rel − 1` is exercised by a hand-computed fixture and no dataset, and
  `docs/reference/retrieval-quality.md` § "Not measured, and why" labels the gap on the published
  page. The FiQA-qrels check above was **not** performed at the close and stays first: no warm
  BEIR cache was reachable from the closing session (`RAGNET_BEIR_CACHE` unset at every scope, no
  cache directory on disk), and downloading a corpus to settle one doc comment is the wrong trade
  inside a close that runs no measurements.
  → ~~**the next phase that re-measures the ablation table** (where the reranker-depth re-measure
  already sits, and a graded dataset belongs in a re-measured table rather than alone), **and
  failing that Milestone 4 as a deadline** — hanging, like that debt, off `retrieval-quality.md`
  shipping as v1.0 documentation: every "Not measured" entry either measured or still honestly
  labelled. The label exists today, so the deadline's floor is already met; the run is the
  improvement, not the blocker.~~
  → **Milestone 5, Phase 5.3** (re-pointed 2026-08-03, hours after the backstop above was
  written, when Milestone 5 was scheduled — replacing the milestone-as-deadline with a real
  destination, which is the reason that milestone exists rather than a longer version of this
  list). The deadline's floor was already met by the label, so riding it to v1.0 would have
  shipped the graded path fixture-only forever; 5.3 is the run, alongside NFCorpus, and
  Milestone 5's own DoD fails while `2^rel − 1` has scored no real dataset. **The FiQA-qrels
  check recorded above is unchanged and stays first** — it is written into 5.3's entry, not just
  here.
- **Nothing pins the Security→Diagnostics decoration** (found by the 2026-08-02 audit). Phase
  3.4's headline capability — recording what `RbacRetrievalGuard` and `PiiChunkSanitiser`
  removed — works only if the Security package's registrations are in place before
  `AddRagDiagnostics` decorates; no test exercises the combination, and
  `Rag.NET.Diagnostics.Tests` does not reference `Rag.NET.Security` at all. Phase 3.4's
  completion claim is therefore an inference across a package boundary — the shape this
  milestone has repeatedly found blind, where a behaviour on the far side of a boundary is
  presumed covered until a test has been watched to fail. Not a known defect: an unpinned claim.
  → **Phase 4.3** (re-pointed 2026-08-02 by the Milestone 4 replan, design §5, from 4.4: the
  decoration records what guards and sanitisers removed, which is logging's subject matter, and
  4.3's structured-logging pass reasons about the same registration order; the 4.4 argument —
  OTel wiring reasons about cross-package instrumentation regardless — keeps it honest if 4.3
  leaves it behind).
- **A permanent `[Fact(Skip)]` in `AzureAISearchVectorStoreTests` appears in no planning record**
  (found by the 2026-08-02 audit): `AzureAISearchVectorStoreTests.cs:140` skips because
  `azure-ai-search-simulator` does not implement OData filter expressions, so that store's filter
  path has no integration coverage — the same coverage-gap-by-simulator-limit shape as
  Pinecone's sparse-write skip at `PineconeVectorStoreTests.cs:359`, which *is* recorded
  (Milestone 2's "Not in scope", by decision 2026-07-26). This entry is the recording; the gap
  is a simulator limitation, not a defect.
  → **the recorded-responses phase** (design §3; re-pointed 2026-08-02 from the bare
  "Milestone 4, with the never-run live suites") — "has the filter path ever run against the real
  service" is the question that phase exists to answer, and the recording criterion holds it
  [both re-pointed 2026-08-03, at the v1.0 postponement: the phase is scheduled as **Phase 6.1**
  and the criterion — now recording-or-recorded-reason — sits in Milestone 6's DoD]. **On the record twice since Phase 4.0 (2026-08-02):** `TestGateTests` lists this skip
  as one of the two permanent `[Fact(Skip)]`s (with its Pinecone sibling), and the ledger judged
  the whole package **`VerifiedBy=unit` despite its Docker-tier tests** — a community simulator
  without OData filters and of unconfirmed fidelity does not earn `container`.
- **Four debts recorded somewhere and scheduled nowhere** (collected by the 2026-08-02 audit —
  each lives in a completed phase's entry, a design doc or features.md, all outside this list,
  all violating its one rule: record with origin, then schedule or re-justify): five connectors'
  narrowed API field selections (Phase 2.2's completion note, itemised with line numbers in
  `docs/plans/2026-07-26-connector-metadata-design.md`); provider-specific webhook payload
  parsers for GitHub/Notion/Slack (`features.md:452`, "remain deferred", with
  `IWebhookPayloadParser` named as the seam); cron/NCrontab polling schedules
  (`features.md:457`, "deferred", interval-only today); and Pinecone live sparse-write
  verification (Milestone 2's "Not in scope" — a documented coverage gap by decision,
  2026-07-26). None is urgent, which is exactly how each stayed where it was.
  → **owners assigned 2026-08-02 by the Milestone 4 replan (§5): the connector field selections,
  the webhook payload parsers and the cron/NCrontab schedules → Phase 4.2**, with the rest of the
  connector-path work — schedule-or-decline inside that phase, and declined gets written here, not
  implied; **the Pinecone live sparse-write verification → the recorded-responses phase** (design
  §3), where `TestGateTests` already lists its permanent skip and the new DoD's recording
  criterion holds it [now **Phase 6.1**, Milestone 6 — re-pointed 2026-08-03 at the v1.0
  postponement, with the recording criterion, which moved there as
  recording-or-recorded-reason].
- **The official Pinecone .NET SDK is abandoned** (found 2026-08-08 while triaging Renovate #38).
  `pinecone-io/pinecone-dotnet-client` was **archived by its owner on 2026-07-03** — read-only, no
  further releases, and no migration guidance published. The 4.x incompatibility this repository
  already recorded on 2026-07-26 ("the 4.x control-plane models cannot deserialize Pinecone Local's
  responses") is upstream's own **open** issue #54, filed against 4.0.2 with `pinecone-local`, and
  it will not be fixed. Re-verified rather than assumed: v4 needs only a three-line
  `CreateIndexRequestMetric` → `MetricType` rename to compile, but its `Index` model marks
  `vector_type` required and **no published emulator image sends it** — `:latest` and the newest
  tag `v1.0.0.rc0` both fail all 12 container-backed tests identically (`v0.6.0` and `v0.7.0` are
  the only others, all 17 months old). `Pinecone.Client` 3.1.0 works and is fully tested, so it
  stays, and `renovate.json` now pins `<4.0.0` so the PR stops reopening. **The real question is
  not the version.** A connector resting on an archived SDK has three futures — stay on 3.1.0
  indefinitely, hand-roll against Pinecone's REST API (the connector needs few endpoints), or drop
  Pinecone support — and that is a decision, not a dependency bump. The one third-party
  alternative found, `searchpioneer/pinecone-dotnet-client` (`Pinecone.Grpc 1.0.0-alpha1`), is a
  four-commit alpha roughly three years old and is not a candidate.
  → **unscheduled**, and deliberately so: nothing is broken today, and choosing between those
  three futures needs more evidence about how much Pinecone matters to this library than exists
  now.
- **61 of 71 packages have only ever been exercised against fakes** (measured by Phase 4.0's
  ledger, 2026-08-02: `unit` 61, `container` 8, `recorded` 0, `live` 0, `none` 2). Not a defect
  list — the *shape* of the risk: `VerifiedBy=unit` is the state late chunking was in for five
  phases while inert, and the reranker while sending a quarter of every document to the model as
  `[UNK]`. Roughly 20 of the 61 talk to services no test can reach — the twelve SaaS connectors,
  the cloud vector stores, the hosted LLM and reranker providers — and among them sits
  `Parsers.Pdf.AzureDocumentIntelligence`, judged `unit` rather than `recorded` because its
  WireMock cassettes were hand-written, never recorded from the live service: a cassette encoding
  our belief about an API is the shape that let the reranker's smoke test agree with its defect.
  → **the recorded-responses phase (design §3) for every live-service package**, enforced by the
  new DoD's recording criterion; every other upgrade — or honest stay-at-`unit` — is recorded per
  package in its `<VerifiedBy>`, which is what makes this entry shrinkable rather than
  aspirational. This is the milestone's dominant work, and the replan says so rather than
  footnoting it. [Re-pointed 2026-08-03, at the v1.0 postponement: the recorded-responses phase
  is **Phase 6.1**, in Milestone 6, and the recording criterion sits in that milestone's DoD —
  widened to recording-or-recorded-reason, because credentials for some of the ~20 services may
  never exist, and a criterion that can never become true is not falsifiable, it is dead. And
  the other ~41 of the 61 — the unit-only packages with no live service to record against —
  stop being this entry's unowned remainder: **Phase 6.2** exists to decide what raising the
  floor means for them and to do it, and Milestone 6's DoD fails any package still at bare
  `VerifiedBy=unit` with no stated reason. "The milestone's dominant work" was written of
  Milestone 4; the work is now Milestone 6's, and it is still the dominant work.]
- **The packages ship without XML documentation, and the CS1574 backlog is still unmeasured**
  (the known blocker recorded on Phase 4.1's entry since 2026-07-28, surfaced into this list at
  that phase's close, 2026-08-03, because the phase did **not** take it up and this list's rule
  is record-then-schedule, not silently carry). `GenerateDocumentationFile` is set nowhere in
  the repository, so the 70 published packages carry no IntelliSense XML, and the blocker's
  measurement stands: **9 distinct CS1574 sites in `Rag.NET.Abstractions` alone**, only two
  projects ever measured, the rest never having compiled their XML at all — under
  `TreatWarningsAsErrors`, every one becomes a build failure the day generation is enabled, so
  9 is a floor. No task in
  4.1's plan scoped it and no commit decided against it; the phase's close says so rather than
  absorbing it. The work is repo-wide (enable generation, clear the cref backlog, and a
  PackageValidation check that each package carries its XML so it cannot silently regress).
  → **Phase 4.2**, as the owning slot — the next phase that scrutinises the public API surface
  project by project, which is the same pass the crefs need — and it must land before **Phase
  6.3** either way: publishing 70 IntelliSense-less packages is a defect the packaging phase has
  now measured, not a style choice.
- **`ZeroAlloc.ValueObjects` roots five `Microsoft.Extensions` packages in every Rag.NET
  package's closure** (measured in Phase 4.7 with `dotnet nuget why`, 2026-08-04, `e46fe26`):
  `Rag.NET.Abstractions` → `ZeroAlloc.ValueObjects` → `Microsoft.Extensions.Hosting.Abstractions`
  pulls `Options`, `Diagnostics.Abstractions`, `Configuration.Abstractions`,
  `Hosting.Abstractions` and `FileProviders.Abstractions` into the closure of **everything**,
  because every package references Abstractions. This is why the caching swap saved 2 packages
  instead of the expected 6 — four of the six were rooted here independently — and it is **the
  largest remaining thread**: no extraction inside this repository can remove them while the
  reference stands. The fix is upstream — ZeroAlloc.ValueObjects splitting or dropping its
  Hosting.Abstractions dependency — or a decision that five framework abstraction packages in
  every closure is an acceptable floor.
  → **the next phase that touches `Rag.NET.Abstractions`' own dependencies or takes a
  ZeroAlloc-ecosystem version bump, and failing that Phase 6.3 as a pre-publish decision**:
  publish with the five in every closure knowingly, or take the upstream fix first — the same
  decide-before-it-ships shape as the Mcp.Tool residual, held by the phase that owns the
  release checklist.
- **Five members `docs/guide/data-providers.md` documents do not exist in the code, and four
  other pages still name package ids the decomposition retired** (found in Phase 4.7 writing
  the per-package READMEs against the reflection guard, 2026-08-04, `17e698d`): Slack
  `ChannelIds` → `ChannelId`, Gmail `EmailAddress`/`ImapHost`/`ImapPort` → `UserName`,
  Confluence `SpaceKeys` → `SpaceKey`, and GitLab and Bitbucket `Branch` → `Ref`. The READMEs
  now matched the code — this repository's dominant defect caught before shipping for once — but
  **the guide was not yet fixed**, and the same close found `docs/index.md` (16 mentions),
  `docs/guide/ingestion.md`, `docs/guide/data-providers.md` and
  `docs/reference/oss-libraries.md` still naming `Rag.NET.Parsers.Word`/`.Excel`/`.PowerPoint`,
  `Rag.NET.Chunking.Semantic`/`.TokenAware` and the four standalone Graph connector packages.
  (`docs/getting-started.md`'s two stale install commands were fixed with the chooser; the rest
  was deliberately not swept in a phase-close task.)
  **The five-members half closed 2026-08-04 in Phase 4.9** (which was already editing this file
  for the time-weighting fallback keys): all five re-verified against the actual option classes
  before writing, not copied from this entry's own table — it was accurate, but the phase checked
  rather than trusted it. **Only the package-id-naming half remains open** →
  **Phase 4.5** (the docs-read-end-to-end pass that already owns the sidebar sweep and
  `docs.yml` — this is the same "nobody has read these pages against reality" work).
- **`Rag.NET.Mcp.Tool`'s package shape needs one deliberate look before it publishes**
  (opened by the Phase 4.7 design as "19 MB, unexplained"; the phase's close explained it by
  measurement and shrank the question): a `PackAsTool` package ships its entire dependency
  closure inside the `.nupkg`, so the 19 MB was the pre-decomposition core closure — SQLite
  natives for every RID, the resilience tree — riding inside the tool, and the close's pack is
  **1.87 MB**, 34 entries, all managed dependency assemblies under `tools/`. What remains is
  confirming that shape is intended (the Cl100kBase vocabulary and MCP stack dominate it) —
  small, but it must be a decision rather than a default before the tool is published.
  → **Phase 4.6** (which owns `Rag.NET.Mcp.Tool`'s first tests), owed before **6.3** publishes.
  **Decided and re-measured 2026-08-08 (Phase 4.6).** The 1.87 MB figure is superseded: the tool
  now carries the providers it needs to actually work, and packs at **4.97 MB, 55 entries** — 2.7×
  the previous shape. Every increment is traceable to the design's bounded provider set
  (`docs/plans/2026-08-08-executable-configuration-design.md` §1.1–§1.2), uncompressed:
  **`OpenAI.dll` 4.98 MB**, the Qdrant stack (`Qdrant.Client` + `Google.Protobuf` +
  `Grpc.Net.Client`) ~2.0 MB, **`Npgsql` 1.41 MB** for PgVector, on top of the pre-existing
  `ModelContextProtocol.Core` and Cl100kBase vocabulary (~1.75 MB together). **This shape is
  intended.** `OpenAI.dll` alone is the largest single item and is unavoidable given §1.1's
  decision to standardise on one OpenAI-compatible client — that one dependency is what buys
  OpenAI, Azure OpenAI, OpenRouter, Ollama and LM Studio rather than four separate providers, and
  the alternative measured here is the shape the phase started from: 1.87 MB of tool that could
  not register a pipeline, could not register a transport, and logged over its own protocol
  stream. A working 5 MB tool is the better package. Anything beyond the bounded set is served by
  hosting `Rag.NET.Mcp` directly (§1.3) rather than by growing this closure further — which is the
  line that must hold, since the original 19 MB was reached one reasonable-looking reference at a
  time.
- **GoogleDrive's fourth field mask has no test** (found by Phase 4.10, `b3f026c`, 2026-08-05):
  widening `CreatedAt`/`UpdatedAt` support touched four identical field-selection sites — whole-drive
  `Files.List`, folder-traversal `Files.List`, and both pages of `Changes.List` — and three are
  each pinned by a dedicated test. **Delta pagination's second page is not**, because no existing
  test drives GoogleDrive delta pagination at all; this is pre-existing coverage, not a regression
  the phase introduced, but stating it as debt beats leaving three-of-four tests to imply
  four-of-four coverage.
  → **Phase 6.2** (Raise the Floor on Unit-Only Packages), which already owns auditing every
  `unit`-verified package's coverage before the ledger can call it more than "exercised at all".
- **Slack's and Microsoft Teams' `date` tags stay day-granularity (`yyyy-MM-dd`) despite both
  connectors now holding full-precision timestamps internally** (found by Phase 4.9's design,
  reaffirmed out of scope by Phase 4.10's design §8, 2026-08-05): both connectors group messages
  into one document per UTC calendar day and tag it with the day label; Phase 4.10 gave both a
  full-precision typed `CreatedAt`/`UpdatedAt` pair (the day's earliest/latest message) that
  `TimeWeightedRetriever` now ranks on, but the `date` *tag* itself — what a caller filters or reads
  directly — was deliberately left exactly as it was. Normalising it changes what these two
  connectors report to callers and belongs with whoever next touches their own fetch/grouping
  logic, not with the timestamp-channel work that happened to sit next to it twice now.
  → no phase currently owns the Slack or Microsoft Teams connectors' own fetch layer; stays
  unscheduled until one does, re-justified rather than silently dropped.
- **`Rag.NET`'s pipeline options are not yet aligned on `IOptions` or validated with
  `ZeroAlloc.Validation`** (`features.md:1117`, unchecked since the backlog was written; recorded
  because Phase 4.2 no longer owns it): the Milestone 4 replan's design §5 routed "IOptions
  Alignment + ZeroAlloc Validation for pipeline options" to Phase 4.2 under the name "Options
  Alignment & Validation." Phase 4.2's own measurement (design §0) found five workstreams stacked
  into that slot by four earlier phases and split two of them back out — documentation and
  connectors — leaving "who owns a content type, and how that is declared" as the one subject the
  phase actually built. The general `IOptions`/`ZeroAlloc.Validation` alignment was never one of
  the five; it carried no design of its own, and none of the phase's seven tasks touches it. Phase
  4.2 did close one options-home question inside its own scope — `CostBudgetOptions.DatabasePath`,
  below — but that is a single property this phase happened to own, not the general alignment pass
  the backlog row still describes.
  → no phase currently owns the general `IOptions`/`ZeroAlloc.Validation` alignment; stays
  unscheduled until one does, re-justified rather than silently carried under a phase number that
  no longer means it.
- **`AddParser<T>(replaces:, replacesTypeNames:)` cannot replace a factory-registered parser**
  (found while implementing Phase 4.2's `RemoveReplacedParser`, 2026-08-08): both parameters match
  on `ServiceDescriptor.ImplementationType`, which is `null` for every parser registered through
  `AddSingleton<IDocumentParser>(sp => …)` rather than `AddSingleton<IDocumentParser, TParser>()`.
  `Rag.NET.Parsers.Vision`, `.Email`, `.Archive` and this repository's own
  `Rag.NET.Chunking.Templates` all register that way. Naming one of them in `replaces:` or
  `replacesTypeNames:` removes nothing and throws nothing — it is a silent no-op indistinguishable
  from naming a package that was never installed, which is the exact failure shape this whole
  mechanism exists to remove. Documented on `RagBuilder.RemoveReplacedParser`'s own remarks rather
  than fixed; the two replacements this repository ships today (`CsvDocumentParser`,
  `ExcelDocumentParser`) both arrive through `AddParser<T>()` and are unaffected.
  → no phase currently owns extending the replacement match beyond `ImplementationType` — the
  options are resolving a provider to read the runtime type (the same "needs live instances"
  problem `ParserClaim`'s own remarks describe) or a second, explicit removal key a factory
  registration could supply; stays unscheduled until a phase touches parser registration internals
  again, re-justified rather than silently dropped.
- **The first `MakeGenericMethod` call in `src/`** (Phase 4.2's Task 3, 2026-08-08):
  `RagBuilder.DeclareContentTypeClaims<TParser>` needs to invoke
  `IDeclaresContentTypes.ContentTypes`, a static abstract interface member, on a `TParser` known
  only at the `AddParser<TParser>()` call site with no constraint to `IDeclaresContentTypes` —
  reachable only through a generic method built with `MethodInfo.MakeGenericMethod` once a runtime
  check confirms the interface applies. No AOT or trim analyzer is enabled anywhere in this
  repository and no package claims AOT compatibility, so nothing warns today. But
  `MakeGenericMethod` is the textbook Native AOT failure mode, and the ROADMAP already lists
  *Native AOT startup time* as a metric Phase 5.1 intends to measure — the first place this call
  path's cost, if any, becomes visible. A reflection-free alternative exists — a second
  `AddParser<TParser>()` overload constrained to `TParser : IDeclaresContentTypes`, which the
  compiler binds automatically for every concrete caller — but it would silently fall back to the
  reflective path for any generic-forwarding call (a helper that calls `AddParser<T>()` for a `T`
  it only knows through its own type parameter), trading one silence for another rather than
  removing it.
  → **Phase 5.1** (Library Performance Comparison), the first phase that measures Native AOT
  startup time and the first that would notice this path costing anything; re-justified rather
  than fixed speculatively until it does.
- **`ParserClaimCoverageTests` reads `ContentTypeMap`'s private `s_map` field by reflection**
  (Phase 4.2's Task 4, 2026-08-08): `ContentTypeMap` exposes no public enumeration of the MIME
  types it covers, only `FromFileName(extension)`, so the coverage test's "behaviour implies
  declaration" direction reads the backing dictionary directly to build its check set. Renaming or
  restructuring `s_map` breaks the test loudly — `ContentTypesInMap()` throws a named
  `InvalidOperationException` naming the missing field rather than passing silently — so the
  coupling fails safe, but it is real: a future refactor of `ContentTypeMap`'s storage shape has to
  know this test reads inside it.
  → no phase currently owns adding a public enumeration surface to `ContentTypeMap`; stays
  unscheduled until one does, re-justified rather than silently dropped — the fail-loud property is
  why this ranks lowest-urgency of the three debts recorded here.
- **`ragnet evaluate` is not implemented** (Phase 4.6, `2c2e6d61`, 2026-08-08 — deferred
  deliberately, not half-built): `Rag.NET.Evaluation`'s evaluators (`EmbeddingDistanceEvaluator`,
  `LlmJudgeEvaluator`) score `EvaluationSample` instances that already carry a *predicted* answer,
  and `AddRagNetPipelineFromConfiguration` registers no `IRagEvaluator`. A working command needs a
  dataset file format this repository does not have anywhere — nothing parses a set of
  question/reference pairs into the shape an evaluator scores — plus an evaluator-selection
  decision, neither of which is a thin call onto an existing seam the way `ingest`/`query` are.
  Running `ragnet evaluate` prints this reason to stderr and exits non-zero rather than
  half-working.
  → no phase currently owns designing a dataset file format for `Rag.NET.Evaluation`; stays
  unscheduled until one does, re-justified rather than silently dropped — Milestone 5's evaluation
  work is the closest existing candidate, but none of its four phases scopes this.
- **`Rag.NET.Cli`'s and `Rag.NET.Mcp.Tool`'s `VerifiedBy=unit` excludes host selection and
  process-level behaviour** (Phase 4.6, 2026-08-08, documented in each package's own csproj
  remark rather than only here): choosing between the CLI's ingest/query host and
  `Rag.NET.Mcp.Tool`'s stdio-vs-HTTP host, and actually running either one, is launching a process
  or a Kestrel listener — not a computation a unit test asserts on. Both packages' command
  handlers and argument parsing are genuinely covered; the host wiring around them is not.
  → **Phase 6.2** (Raise the Floor on Unit-Only Packages), which already owns auditing every
  `unit`-verified package's coverage before the ledger can call it more than "exercised at all".
- **Nothing compiles documentation code snippets** (found in Phase 4.5, 2026-08-08, while
  following `docs/getting-started.md` to build `samples/Rag.NET.QuickStart`): two of that page's
  six numbered steps did not compile against the pinned packages — a wrong
  `Microsoft.Extensions.AI.OpenAI` API and a package id used as a namespace — and every existing
  guard passed regardless, `docs.yml` included, because `docs.yml` checks links, not code. A
  Markdown code fence is prose to every tool in this repository; nothing ever tries to build it.
  → no phase currently owns a doc-snippet compilation checker; stays unscheduled until one does,
  re-justified rather than silently dropped.
- **`npm audit` reports 25 vulnerabilities (6 moderate, 19 high)** (measured 2026-08-08 in Phase
  4.5, after the Docusaurus 3.7.0 → 3.10.2 upgrade — the number the design's own text predicted,
  "24 (12/12)", is stale; this is the actual post-upgrade count). All in the site's build-time
  npm dependency tree — webpack-dev-server's transitive chain among them — not in anything shipped
  to a Rag.NET consumer; recorded as deliberate debt for that reason, not fixed reflexively.
  → no phase currently owns an npm audit remediation pass; stays unscheduled until one does.
- **`docs/index.md`'s Quick Links point at `docs/plans/` as inline code, not a Markdown link**
  (found in Phase 4.5, 2026-08-08): `docs/plans/` is not part of the published site (Task 2 of this
  phase's own plan established that a relative link into it can never resolve), and because the
  reference is rendered as `` `docs/plans/` `` rather than `[text](docs/plans/)`, no link checker
  — including this phase's own `docs.yml` — has anything to flag. A reader following it from the
  published site finds nothing.
  → no phase currently owns rewording this reference (to a GitHub URL, or dropping it, per Task
  2's precedent for the same class of link elsewhere); stays unscheduled until one does.
- **`onBrokenMarkdownLinks` is a deprecated Docusaurus config key** (found in Phase 4.5,
  2026-08-08, present since the Docusaurus 3.10.2 upgrade): `docusaurus.config.ts` sets
  `onBrokenMarkdownLinks` directly; Docusaurus 3.10 wants it under
  `markdown.hooks.onBrokenMarkdownLinks` instead and prints a deprecation warning on every build.
  Cosmetic — the build still succeeds and still hard-fails on a genuinely broken Markdown link —
  but a warning nobody silences tends to become the warning nobody notices.
  → no phase currently owns migrating the config key; stays unscheduled until one does.

### Closed

- ~~**Two packages have never been exercised by any test at all**~~ (declared honestly by Phase
  4.0's `<VerifiedBy>` ledger, 2026-08-02): `Rag.NET.Mcp.Tool` (a host scaffold no test
  references) and `Rag.NET.Security.AspNetCore` (two types, zero test references). Both declared
  `VerifiedBy=none`, which the ledger's release gate — "no package declares `none`" — turns into a
  release blocker without failing the build, because punishing an honest `none` is how a ledger
  becomes fiction. Owners assigned at Phase 4.0's close: `Rag.NET.Mcp.Tool` → Phase 4.6,
  `Rag.NET.Security.AspNetCore` → Phase 4.5. **The `Rag.NET.Mcp.Tool` half closed 2026-08-08 in
  Phase 4.6** (`8762c181`): its argument parsing (`ProgramArguments`) and `X-Api-Key`
  authorization decision (`ApiKeyAuthorization`) moved to named `internal` types,
  `Rag.NET.Mcp.Tool.Tests` (16) covers both, `VerifiedBy=unit`. **Three real defects were found
  by running the tool** — no pipeline registered, no transport registered (stdio silently started
  a bare Kestrel server instead of speaking MCP), and logging over stdout, the exact channel MCP
  JSON-RPC travels on — see Phase 4.6's own entry for the full account. **The
  `Rag.NET.Security.AspNetCore` half closed 2026-08-08 in Phase 4.5** (`398c595f`):
  `tests/Rag.NET.Security.AspNetCore.Tests` drives both of the package's types — `GetRoles()` and
  `AddRagNetAspNetCoreSecurity()`/`UseRbac()` — through real ASP.NET Core primitives
  (`HttpContextAccessor`, `DefaultHttpContext`, a real `TestServer` pipeline), including 16
  concurrent requests with distinct roles to prove per-request resolution rather than a
  cached/shared value, and both negative-path tests were verified to fail on the defects they
  target before the fix. **Unlike Phase 4.6's package, no production defect was found here** — the
  two types do what their names say. `VerifiedBy=unit`, ledger entry removed in the same commit.
  **This was the last package at `none`**: `NoPackageIsVerifiedByNothing`'s skip condition is now
  never true — `Rag.NET.RepoConventions.Tests` went from 48 passing + 1 skip to **49 passing, 0
  skipped**, confirmed by an independent re-run of `PackageVerificationTests` at Phase 4.5's close.
- ~~**Three pieces of house furniture this repository lacks**~~ (recorded in the Phase 3.5 design as
  out of scope; all three exist in `MarcelRoozekrans/AdoNet.Async` and none existed here: `docs.yml`,
  `.commitlintrc.yml`, `renovate.json`) → **closed 2026-08-08 in Phase 4.5**, the last of the three
  to ship. Owners assigned 2026-08-02 by the Milestone 4 replan: `docs.yml` → Phase 4.5 (design
  §5 — with the sidebar sweep, when the docs get read end to end); `.commitlintrc.yml` and
  `renovate.json` → Phase 4.1. **The two 4.1 halves shipped 2026-08-03** (`1217791`):
  `.commitlintrc.yml` was run against all 1,506 existing commits before being allowed to fail
  anything — stock config-conventional rejects 184, the tuned rules still reject 70, none newer
  than 2026-07-29 — so the gating job lints only a pull request's base-to-head range;
  `renovate.json` is `config:recommended` plus forced semantic commits, validated with
  `renovate-config-validator`, and gained a `packageRules` entry at Phase 4.8 (2026-08-04,
  re-validated the same day) batching patch/minor bumps weekly and leaving majors ungrouped.
  **Recorded inert through Phase 4.8, and that note went stale without anyone correcting it**: the
  Renovate GitHub App is enabled and has been opening PRs against this repository since
  2026-08-05 — five `renovate/*` branches live at this closure (`box.v2-10.x`,
  `major-ml-dotnet-monorepo`, `pinecone.client-4.x`, `wiremock.net-2.x`,
  `zeroalloc.mediator.generator-5.x`), several already merged, including a major bump
  (`zeroalloc.valueobjects` to v2, PR #54). Corrected here and in `docs/reference/ci.md`'s
  Renovate section, both of which had carried the stale claim forward. **`docs.yml` — the half
  that kept this entry open — closed 2026-08-08 in Phase 4.5**: builds the Docusaurus site on
  every pull request (`npm ci && npm run build`), modelled on `ci.yml`'s job shape and confirmed
  to fire on a normal PR trigger. Deployment is deliberately not part of it — the site is
  configured for `rag-net.github.io/Rag.NET/` (`organizationName: 'rag-net'`), the repository is
  `MarcelRoozekrans/Rag.NET`, the `RAG-Net` GitHub org exists but does not hold this repository,
  and GitHub Pages is enabled nowhere for it (the Pages API answers 404) — a real decision this
  phase does not have grounds to make, recorded rather than guessed at by "fixing"
  `organizationName` to a plausible-but-unverified value.
- ~~**No supported way to replace a built-in parser**~~ (found while fixing the Phase 3.11
  review's first finding) → closed 2026-08-08 in Phase 4.2 (Parser Registration Ownership),
  **implemented**: `RagBuilder.AddParser<TParser>(replaces:, replacesTypeNames:)` removes the
  named parser's `IDocumentParser` `ServiceDescriptor` and its `ParserClaim` together, rather than
  only silencing the conflict check. Silencing alone would not have been enough — selection takes
  the first registered parser whose `CanParse` matches, and built-ins register before `configure`
  runs, so a "replacement" that left the old descriptor in place would still lose to it.
  `replacesTypeNames` (a `string[]`) exists for a caller with no compile-time reference to the
  parser it overrides — `Rag.NET.Chunking.Templates` naming `Rag.NET.Parsers.Office`'s
  `ExcelDocumentParser`, which may not even be installed; a name matching nothing registered
  removes nothing and is not an error, the shape "the package isn't installed" needs. Both match by
  `Type.FullName`, not `Type.Name` — the same short-name reasoning `ParserClaim.ParserTypeName`
  pins. **One debt this closure created rather than removed, recorded above**: the match is on
  `ServiceDescriptor.ImplementationType`, which is `null` for a factory-registered parser, so
  Vision, Email, Archive and Chunking.Templates' own factory registrations cannot be named here —
  a silent no-op indistinguishable from an absent package.
- ~~**`Rag.NET.Chunking.Templates` still ships `MimeKit`, `CsvHelper` and `ClosedXML`**~~ (Phase
  4.7's Task 10, stopped rather than completed, 2026-08-04) → closed 2026-08-08 in Phase 4.2,
  **partly implemented, and correctly not finished**. `message/rfc822` — `EmailTemplateDocumentParser`
  duplicated `Rag.NET.Parsers.Email`'s strictly more capable `EmailDocumentParser`, and
  `UseEmailChunking`'s own remarks already recorded that the chunking strategy "does not care which
  parser produced" its sections — is retired outright: the type is deleted, `UseEmailChunking`'s
  `registerParser` escape hatch goes with it, and `MimeKit` drops from the package, verified in the
  packed nuspec's `<dependencies>` rather than trusted from the csproj (Phase 4.7's own lesson: a
  floating reference freezes into the nuspec). **`text/csv` — kept, on purpose, not left over.**
  `QAPairsDocumentParser` is not a duplicate of core's `CsvDocumentParser`: `QAPairsChunkingStrategy`
  reads the answer out of `DocumentSection.Heading` as a documented internal contract with that
  parser, so retiring it would break the feature. `CsvHelper` and `ClosedXML` stay for exactly that
  reason. An earlier design proposed retiring both parsers on symmetry and claimed all three
  dependencies would drop — that was wrong, and only `MimeKit` ever does. Phase 4.7's Task 10 is
  therefore **partly complete**, not finished, and that is the honest final state rather than a
  waypoint.
- ~~**`CostBudgetOptions.DatabasePath` survives as a property nothing reads**~~ (created by Phase
  4.7's cost-ledger decision, `f2518d5`) → closed 2026-08-08 in Phase 4.2, **implemented**: the
  property, its `DefaultDatabasePath` constant, and the `UseCostBudgeting()` guard that turned a
  non-default value into a runtime error are all removed together. A consumer who writes
  `o.DatabasePath = "spend.db"` now gets a compiler error naming a property that does not exist,
  which is strictly better than the runtime error it replaces — the mistake is caught before the
  build, not after `UseCostBudgeting()` runs. The dangling `<see cref="CostBudgetOptions.DatabasePath"/>`
  references the removal would otherwise have left in `RagBuilderExtensions.cs` were removed in the
  same change, ahead of the documentation-generation phase that would have turned them into a
  CS1574 build failure.

- ~~**`BuildMetadata` drops `baseMetadata.CreatedAt`, so provider-ingested documents score as
  brand new**~~ (found in Phase 2.2; recorded until 2026-08-02 only in
  `docs/plans/2026-07-26-connector-metadata-design.md:237-240`, surfaced into this list by that
  day's audit; routed to **the next phase that touches the data-provider ingestion path, and
  failing that Phase 4.2, because "the fix is one copied property plus a test: a slot, not a
  phase"**) → **closed 2026-08-04 in Phase 4.9 — and the routing corrected, not merely
  serviced.** The "slot, not a phase" estimate was wrong, and the evidence was already in the
  repository: the very design doc the routing cited already said, in the sentence right before
  the one this list quoted, that "a connector's real creation timestamp cannot reach
  `DocumentMetadata.CreatedAt` — only a tag." Four independent reasons confirm the one-line fix
  could never have worked, each measured rather than argued: `baseMetadata` is a **per-call**,
  not per-document, parameter on `IngestFromProviderAsync` (`RagPipelineExtensions.cs:67-76`), so
  copying it would stamp every document in a run with one identical value, not each document's
  own creation time; **no production caller sets it** — `BackgroundPollingTrigger`, the only
  production caller found, never passes `baseMetadata`; `FileEntry`/`FileHandle` carry **no
  timestamp field**, so a connector has no typed channel to supply one even if it wanted to; and
  `created_at` is a **reserved** tag key — a connector that emits it gets
  `ReservedMetadataKeyException`, so the tag channel is closed too. `DocumentMetadata.CreatedAt`
  is now `DateTime?` with no default (`2166eab`) — a breaking change to an unpublished type, no
  `[Obsolete]` shim needed — and provider-ingested documents that carry no timestamp now rank
  **neutrally** under `TimeWeightedRetriever` (decay `1.0`, pinned by test in `b35a835`) rather
  than fabricating freshness. Full entry in Phase 4.9, ROADMAP and MILESTONE. **What this did
  not fix, priced rather than left a slot a second time:** 17 of 25 providers hold a real
  timestamp and discard it, 4 more do not even fetch it, 4 genuinely have none —
  → **Phase 4.10**.
- ~~**`HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize`**~~ (found by Phase 3.16's
  audit of the other chunking strategies — the inverse of the defect that phase fixed: chunks are
  one heading subtree each, unbounded above, and the Book, Legal and AcademicPaper templates all
  delegate to it, so a user setting `MaxChunkSize` on any of the three got no effect; the routed
  decision was *document the limit or honour the option*, with behaviour changes explicitly off
  the table) → **closed 2026-08-03 in 4.1, resolved by documenting — the option is deliberately
  ignored, and the docs that said otherwise were false** (`085be6b`). The contract as now
  stated: a chunk is one heading subtree, a semantic unit whose size the document decides;
  truncating it at a character count would defeat the strategy's purpose, `Overlap` has no
  meaning between disjoint subtrees, and the supported way to bound chunk size is
  `UseSemanticRefinement()` on top. Said in every place a user meets the option: XML remarks on
  the strategy and on all three templates (the packaged IntelliSense surface — this was the
  phase inventorying that surface), and `docs/guide/chunking.md` — **whose comparison table had
  claimed the strategy respects an approximate max-chars limit, which was false**, the docs
  drawing the behaviour the code did not have, the same shape 3.16 found in the recursive
  chunker's flowchart. No behaviour changed; the honour-the-option alternative stays available
  to any future phase that brings measurements.
- ~~**`docs/reference/ci.md` does not list the nine ablation cells**~~ (found in Phase 3.15,
  while writing up the table: the page counted "eleven cases" in what the nightly measures,
  from before 3.14/3.15 added their rows to `BeirRunBudget`) → **closed 2026-08-03 in 4.1,
  corrected against measurement rather than memory** (`afe388b`). The budget table holds **30
  dataset × protocol pairs across ten protocols**; counting parity per separator leg the way
  the page's table always has, that is **35 cases, of which the nightly still runs seven** —
  and the rewrite surfaced two more stale figures the entry never named: the page still quoted
  pre-3.16 real-leg costs (~19 min derived / 28 min cold) where 2026-07-31 measured SciFact
  real at 10m43s and ArguAna real at 11m07s. The page now lists every group with its measured
  (or honestly derived) cost and names `BeirRunBudget` as the authority that throws on an
  untimed pair. **The same commit corrected a second false claim found while rereading the
  page:** it said this repository has no branch protection rules — measured 2026-08-03 via the
  GitHub API, the `Main` ruleset is **active** and requires exactly `build-test
  (ubuntu-latest)` and `build-test (windows-latest)`, so both matrix legs do mechanically
  block a merge (admins can bypass; `pack-validate` and `commitlint` are outside the required
  set, stated on the page). Three `ci.yml` comments carried the same false prose and now state
  the measured reality.
- ~~**The current `nightly.yml` has never executed, and its reranker download feeds nothing**~~
  (found by the 2026-08-02 audit; the title's first half went stale in-entry — runs 30735435427
  and 30789374909 were the first two genuine executions, both confirming the second half: the
  ~87 MB `ms-marco-MiniLM-L6-v2` download ran end to end and fed no test, because every reader
  sits behind `RAGNET_BEIR_LONG_RUNS`, which the job never sets; the decision — run something
  that uses it, or stop provisioning it — was left genuinely open for 4.1) → **closed 2026-08-03
  in 4.1: stop provisioning** (`9fc368a`), decided on the budget table's own numbers rather than
  taste. No reranked ablation cell fits the nightly — every one is `FitsTheNightly: false` in
  `BeirRunBudget` (SciFact ~4 m warm plus the parity embedding price cold, ArguAna ~28 m) — and
  running one beside `BeirParityTests` would race it for the cold cache and pay the corpus
  embedding twice, the exact reason the cheap comparison cells are opt-in. The pinned
  `RERANKER_REVISION` and both SHA-256 checks moved verbatim into a fenced local procedure in
  `docs/reference/ci.md` beside the `RAGNET_BEIR_LONG_RUNS` command — which is also what keeps
  `RAGNET_ONNX_RERANK_MODEL`/`_VOCAB` satisfiable for `TestGateTests` now that no workflow
  writes them. The presence report still lists both variables deliberately: printing them unset
  says the reranked cells were off tonight, the same reason it lists `RAGNET_BEIR_LONG_RUNS`.
- ~~**Two near-duplicate RAGAS test suites**~~ (found by the 2026-08-02 audit:
  `tests/Rag.NET.Tests/Evaluation/` and `tests/Rag.NET.Evaluation.Tests/Ragas/` carried
  near-duplicate test names over the same metrics — the two-copies shape Phase 3.1 removed from
  `src/`, surviving in the tests that certify the fix) → **closed 2026-08-03 in 4.1:
  `tests/Rag.NET.Evaluation.Tests/Ragas/` is authoritative and the smaller suite is deleted**
  (`8aa9d7c`). The measured shape: 24 tests / ~650 lines (pre-3.1 style, NSubstitute canned
  replies) against 101 tests / ~1,570 lines (post-3.1, the suite that certifies the
  malformed-reply fixes) — the dedicated test project of the packages under test, strictly
  deeper on every shared behaviour. Before deletion, the **six behaviours only the smaller
  suite covered were migrated** in the authoritative suite's style — the faithfulness ratio's
  zero floor, two suite-level throw cases, precondition fail-fast before any spend, a genuine
  two-value overall mean, and the dataset-builder-to-report end-to-end that no per-component
  suite shows — and the other 18 were verified duplicate name by name, not assumed. Baselines
  moved with the merge and were verified green: `Rag.NET.Tests` 1342 → **1318**,
  `Rag.NET.Evaluation.Tests` 382 → **388**.
- ~~**Two ghost directories from the PgVector rename are on disk and in no solution**~~ (forced
  onto the record by Phase 4.0's ledger, which had to decide whether `src/Rag.NET.PgVector` was
  a 72nd package: it is not — an empty leftover of the rename to `VectorStores.PgVector`, with a
  matching ghost at `tests/Rag.NET.PgVector.Tests`, one of which broke a `dotnet run` in Phase
  3.16 by making a project name ambiguous) → **closed 2026-08-03 in 4.1: both deleted from
  disk — and there is deliberately no commit to cite, because there was nothing to commit.**
  `git ls-files` showed **nothing tracked** under either path — untracked `bin`/`obj` only — so
  the deletion touched no tree git records; recorded here precisely because the usual
  closed-by-commit evidence cannot exist for it. The package inventory this phase settled
  (71 src csproj, 70 packages) never counted them, and `PackageValidation.Tests`' exact-count
  assertion now fails if a ghost ever grows back into a csproj.
- ~~**`BeirDatasetCache` is not safe against two test classes wanting the same un-downloaded
  dataset**~~ (found 2026-08-02 at Phase 3.14's close, reading the first genuine run of the
  post-3.15 nightly — run 30735435427, whose one failure was
  `BeirRealChunkingTests.Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments("scifact")` throwing
  `IOException` on `scifact.zip.partial` out of `BeirDatasetCache.DownloadAndVerifyAsync` when it
  and `BeirParityTests` both cold-started SciFact; routed at the time "→ Milestone 4, with 4.1")
  → **closed 2026-08-03, in Milestone 3 — the routing was stale: a red nightly failed this
  milestone's "all tests passing" criterion, so the fix could not wait for 4.1's nightly rework**
  (`50a80cd`, `335710c`). **Three same-shaped races, not the one recorded.** The download race
  was as diagnosed — xUnit runs test classes in parallel, both callers found the dataset missing
  and raced `File.Create` on one shared `.partial` path — and the fix is the GUID-suffixed
  partial this entry itself named as a candidate, the shape `EmbeddingCache` and
  `HypotheticalCache` already use; no lock, which would only serialise unrelated downloads.
  Fixing it exposed two more one step later, which run 30735435427 died too early to reach:
  **archive publication** — `File.Move(overwrite: true)` cannot replace the shared archive while
  the rival holds it open for extraction (Windows refuses to replace a file another process has
  open with `FileShare.Read`), so the archive name is now published last, after extraction, when
  nobody ever holds it open — and **extraction itself**, where two in-place extractions race the
  same entry files through exclusive handles: extraction now lands in a GUID-named `.extracting`
  staging directory renamed into place only when complete, so the dataset directory becomes
  visible atomically or not at all, the rename's loser accepting the winner's directory (built
  from an archive verified against the same published MD5, so losing is success) and
  delete-on-failure preserved on every path out. All three mutation-verified with
  gate-synchronised tests that make the writers genuinely overlap without a sleep: the download
  race reproduces the nightly's exact `IOException` with the shared name restored, and the
  publication collision reproduced **on the test's opening attempt, in 28 ms** — a defect that
  waited five phases for a cold cache falls in milliseconds once a test controls the
  interleaving. **Verified where no local run could verify it:** nightly run **30789374909**
  (2026-08-03), triggered on the fix branch against a fresh `RUNNER_TEMP/beir` — the cold-cache
  condition a developer's warm `RAGNET_BEIR_CACHE` has hidden since Phase 3.7 — `env-gated`
  green in 19m01s, `llm` green, every suite passing.
- ~~**`docs/reference/features.md` documents an observability package that does not exist — a
  live Definition-of-Done failure**~~ (found by the 2026-08-02 milestone audit — the first
  failing DoD criterion this milestone recorded: `features.md:666-676` marked OpenTelemetry
  Tracing & Metrics `✅ Done` in `Rag.NET.Telemetry`, a package never built — no
  `.UseTelemetry()`, no `gen_ai.*` attribute, metric names matching nothing in
  `src/Rag.NET/Telemetry/RagTelemetry.cs` — while its own matrix row at `:1135` sat unchecked;
  held in Phase 4.0's `KnownFalseClaims` with a staleness guard; routed "→ Phase 4.4, or any
  documentation pass before it") → **closed 2026-08-03 by exactly that earlier documentation
  pass** (`81163af`, at Milestone 3's close): the section is **withdrawn from Done** and now
  describes what exists — the core package's internal `RagTelemetry` `ActivitySource`/`Meter`
  named `Rag.NET`, with its real spans and instruments — and names first-class OTel wiring as
  Phase 4.4's, so the detail section finally agrees with its own unchecked matrix row, which was
  the honest one throughout. The `KnownFalseClaims` entry is deleted —
  `EveryRecordedFalseClaimIsStillFalse` forces exactly that once the claim leaves the docs — and
  the parse drops to 53 Done sections and 72 package claims, every one verified directly with no
  exemptions. Phase 4.4 still owns building the wiring; what it no longer inherits is a detail
  section describing its deliverable as already done.
- ~~**`features.md` claims `Rag.NET.Parsers.CSharp`, a package that does not exist under that
  name**~~ (found by Phase 4.0's `FeatureClaimTests`, 2026-08-02 — the second of exactly two
  false claims in the then-54 `✅ Done` sections, and the benign twin of the OTel ghost above:
  the feature is real and lives at `src/Rag.NET.Chunking.CSharp`, only the claimed identity was
  wrong; routed → Phase 4.1) → **closed 2026-08-03 ahead of 4.1** (`81163af`, with the OTel
  correction): the section — and `docs/guide/chunking.md`, which repeated the wrong name — now
  says `Rag.NET.Chunking.CSharp`, where `CSharpChunkingStrategy` actually lives; the
  `KnownFalseClaims` entry is deleted and the claim is guarded directly again. 4.1's packaging
  pass still reads every package identity; it now starts from a truthful one here.
- ~~**Three debts routed "→ Milestone 4, with the release-readiness work" have no owning phase
  among 4.1–4.6**~~ (found by the 2026-08-02 audit, reading this list against Milestone 4's phase
  list: milestone-as-deadline satisfies the letter of this list's rule while the phase list gives
  the work nowhere to land) → **closed 2026-08-02 by the Milestone 4 replan**
  (`docs/plans/2026-08-02-milestone-4-replan-design.md` §5), which is the scoping session this
  entry demanded: `docs.yml` → 4.5, `.commitlintrc.yml`/`renovate.json` → 4.1, the never-run live
  suites split between 4.1 (the OCR compile gate) and the recorded-responses phase (design §3)
  with the new DoD failing while any gate stays unsatisfiable, and TREC-COVID/EnronQA re-pointed
  **back into Milestone 3's scope** rather than smuggled into 4. Each destination is written on
  its own entry above. Every remaining "Milestone 4" arrow in this list now names a phase, a
  trigger with an owned backstop, or a falsifiable DoD criterion — the two "as a deadline"
  backstops that survive (the unnamed flake, the reranker-depth re-measure) both hang off DoD
  criteria rather than off the milestone's goodwill, and the second is already satisfied by
  labelling with only its optional re-measure outstanding. [2026-08-03, at the v1.0
  postponement: the recorded-responses phase these arrows name is scheduled — **Phase 6.1**,
  Milestone 6. Of the two surviving backstops, the flake's stays at Milestone 4 (its
  criterion, "all test projects passing", did not move) and the reranker-depth one rides to
  Milestone 6 (its criterion — the v1.0 docs — moved with the tag). Both re-checks are written
  on their own entries above.]

- ~~**Our BM25 is not comparable to published BM25**~~ (recorded in the Phase 3.7 design as out of
  scope; re-pointed 2026-07-31 from 3.12 to 3.15 when the ablation table moved with the phases)
  → **closed in 3.15, resolved by labelling — the first of the two options the entry demanded a
  knowing choice between, chosen knowingly and before publication.** The `+BM25 hybrid` row is
  published as a **Rag.NET-internal comparison with no published BM25 reference**:
  `InMemoryBm25Index` as it ships — lowercase-and-split, Lucene's `k1=1.5, b=0.75` — fused with
  the dense results via RRF, against Anserini's Porter-stemmed, stopworded `k1=0.9, b=0.4` that
  produced BEIR's published figures. Still not two settings of the same retriever, and the row's
  label says so where the number is printed, which was the whole danger: a row that would read as
  validation of our BM25 while sitting in a table whose first row *is* validated against a
  published figure. The alternative — a BEIR-comparable analyzer for the harness — stays
  rejected for the reason §2 of the 3.7 design gave on the dense path: a benchmark-only analyzer
  measures the benchmark, not the library. What the row measures under that label is real and
  goes both ways: **+0.0532 on SciFact, +0.0074 on ArguAna, −0.0142 on FiQA** against the dense
  anchor — deltas internal to this table, comparable to nothing published, and labelled so.
- ~~**FiQA's two protocols do not index the same corpus**~~ (found in Phase 3.12: 38 of FiQA's
  57,638 corpus entries have an empty `title` *and* an empty `text`, one of them — `117276` —
  judged relevant, and `RecursiveChunkingStrategy` correctly yields nothing for empty input)
  → **closed in 3.15 by stating it alongside the number, which is all the entry ever required.**
  FiQA's real leg indexed **57,600 of 57,638** documents; the 38 empty entries contribute
  nothing, and the one judged relevant can never be retrieved under the real protocol — stated
  next to the measurement it qualifies, real nDCG@10 **0.35569** against parity 0.37086, delta
  −0.01517. Nothing was fixed because nothing was broken: `BeirRunResult.UnindexedDocumentCount`
  already surfaced the 38, and the rejected alternative — a placeholder chunk per empty document
  — would have made the two legs agree by indexing text the corpus does not contain.
- ~~**Nothing established that the source's text all ends up in some chunk**~~ (found by Phase
  3.16's whole-phase review) → **closed by `9682967`, which adds the missing coverage property. A
  test gap, not a product defect — the shipped code never dropped anything.** The phase's tests
  established that every chunk is a substring of the source; nothing established the converse.
  The review proved the gap exploitable: mutating `SplitParts` to delete the mid-stream flush —
  the `Pack(pending, …)` yield loop before recursing into an oversize part, keeping only
  `pending.Clear()` — silently discards every run of short parts preceding an oversize sibling,
  and **all 1,340 core tests and all 110 quality tests stayed green**. Measured under the
  mutation: FiQA 121,236 → 119,279 units, SciFact 20,155 → 19,958, ArguAna 24,003 → 23,626.
  **The fix is a coverage property:** mark every index covered by some chunk's
  `[StartPosition..EndPosition)` span at `Overlap = 0`, and require every uncovered character to
  be whitespace or a `'.'` on a pack boundary — the only two things the chunker may drop. A
  500-iteration fixed-seed generated test plus a deterministic short-run-then-oversize-sibling
  case, both verified to fail under the mutation. Suite 1,340 → **1,342**.
  **Said twice so nobody records it as a bug that shipped:** across the 500 generated shapes plus
  20,000 randomized inputs in the review's own harness, every uncovered character was whitespace
  or `.`. What was missing was the test that would notice if that stopped being true.
- ~~**`RecursiveChunkingStrategy` never merges short split parts back up**~~ (measured in Phase
  3.12 while costing the real-chunking runs, recorded as a *probable* defect with confirmation
  required first) → **closed in 3.16, implemented — and the hedge resolved: confirmed, and it was
  three faults rather than one.** The size limit was never consulted before splitting —
  `SplitRecursively` checked fit only on the branch where the current separator was absent, so a
  35-character section became 2 chunks against a 512-character limit. Split parts were never packed
  back — every part that fit was emitted as its own chunk, and with no sentence separator present
  the recursion reached the `" "` separator and emitted **one chunk per word**, 150 words becoming
  150 chunks of 4 characters. That is what settled the "is it deliberate?" question this entry
  required answering before any fix: nobody deliberately makes word boundaries chunk boundaries.
  And `Split(". ")` destroyed sentence punctuation, with nothing putting the separator back.
  Counts after packing, same stock options: FiQA 429,850 → **121,236** units (7.5× → **2.1×** —
  the 522-character-median-suggests-~2× arithmetic that opened the investigation now closes),
  ArguAna 82,618 → **24,003** (9.5× → 2.8×), SciFact 56,707 → **20,155** (10.9× → 3.9×); the
  single worst document fell **1,723 → 41**. Parity runs unmoved to five decimal places — the
  phase's regression gate — and both real runs improved: SciFact 0.65589 → 0.67742, ArguAna
  0.42594 → 0.47559. **The existing tests asserted the defect and the docs drew it** —
  `ChunkAsync_SplitsByParagraphsFirst` asserted 2 chunks for a 35-character input and passed, and
  the chunking guide's flowchart drew "fits in MaxChunkSize? → yes → emit chunk" with no merge
  step — the sixth instance in this milestone of code, tests and docs agreeing with each other and
  being wrong together. Full numbers in the Phase 3.16 entry.
- ~~**`docs/reference/benchmarks.md` publishes chunking performance measured against the old
  splitter**~~ (found by Phase 3.16's Task 5 documentation agent) → **closed by `cfea8e9`, the
  re-measure this entry said would finish it — run immediately after 3.16's close, on the same
  branch.** The old Recursive rows — 512 ns / 2.94 KB at 500 characters, 5.0 μs / 31.91 KB at
  5 KB, 47.3 μs / 315.54 KB at 50 KB — predated packing, and the entry's refusal to guess a
  direction in print was right: the numbers moved both ways at once.
  **What was measured.** Packing made `Recursive` faster at every size — 512 → **188 ns** at 500
  characters, 5.0 → **4.0 μs** at 5 KB, 47.3 → **38.5 μs** at 50 KB — on far fewer `TextChunk`
  allocations. Allocation moved in both directions: down at 500 characters (2.94 → **1.41 KB**,
  fewer chunk objects) and up at 50 KB (315.54 → **354.21 KB**), where the `StringBuilder` joins
  rebuilding each packed chunk cost more than the chunk objects they save. The whole table was
  re-measured in one run, so the four strategies stay comparable.
  **Two things found while doing it.** First, the benchmark suite could not run at all:
  BenchmarkDotNet searches subfolders for the project it is asked to build and refuses on two
  matches, so a leftover agent worktree holding a second `Rag.NET.Benchmarks.csproj` killed the
  run in about three seconds with output that reads like a build failure — nobody could have
  reproduced this page while an agent worktree existed under the repository. Now documented in
  `benchmarks.md`, with `git worktree list` as the first check. Second, the chunking guide's
  overhead row disagreed with `benchmarks.md` by roughly 2× — ~29/~94/~1,750 μs against
  17.9/47.3/972 μs — and had done so **before this phase**. Both now carry the same measurement,
  and the two cells that were never measured say "not measured" instead of carrying a number.
  **Also worth recording:** the three strategies this phase did not touch moved 10–25% between
  runs on identical hardware, standard deviations reach ±14% of the mean, and five of eleven
  benchmarks are bimodal — these figures are bands, not numbers to compare at one significant
  figure.
- ~~**The nightly `run-secrets` job now selects hours of work it has 120 minutes for**~~ (found while
  documenting Phase 3.12, from the numbers that phase measured — never observed on a run) → **closed
  in 3.12, by the phase that opened it, before the first nightly it would have affected.**
  **The problem was as recorded.** `nightly.yml` runs `dotnet test` over every `RequiresSecrets`
  project with **no filter**, and 3.12 added five parity cases and three real-chunking cases to that
  project. FiQA's parity leg alone measures **1 h 11 m** and its real leg — a case of the same theory
  — is estimated at **eight to nine hours** [revised by 3.16 to a derived **~1.5–2 h**, since
  packing cut FiQA's real leg to 121,236 chunks — still more than the budget this entry argues
  about], against a `timeout-minutes: 120` that also covers a
  restore, a whole-solution build and four other secret-gated projects. `RUNNER_TEMP/beir` is fresh
  every night, so the embedding cache saves that job nothing.
  **What shipped is a budget table, not the `--filter` this entry proposed.** `BeirRunBudget` records
  what every dataset costs under every protocol and which of them the nightly can afford; the four it
  cannot are gated behind `RAGNET_BEIR_LONG_RUNS`, which `nightly.yml` deliberately never sets. A
  gated case skips with its own name, its **measured** cost and the exact command that runs it —
  never a bare "skipped", which is indistinguishable from a pass. A filter was rejected because it
  lives in a workflow file where nothing type-checks it and nothing explains it; the table throws
  when a dataset is added without being timed, so the next dataset cannot silently default either
  into the job or out of it.
  **What it gives up, stated rather than buried.** No chunk-to-document max-pooling runs against a
  corpus in the nightly any more. The cheap chunk-shape checks still do — no model, ~1.5 s for all
  three datasets — and still catch a chunker that stopped chunking; the pooling half is
  `DocumentRankingTests`' fixture plus an opt-in run. What the nightly keeps is the SciFact and
  ArguAna **parity** legs, which are the only numbers comparable to a published figure at all.
  **The `ci.md` half of this entry was already stale when the entry was written.** It said that page
  "still describes this job as running the SciFact retrieval-quality parity run"; the same commit
  that gated the runs rewrote that section into a per-case cost table. Left here because a debt
  register that quietly deletes its own wrong sentences teaches nobody anything.
  Two things did **not** ship and are not pretended to have. FiQA's real leg still has no number —
  it moves to **Phase 3.15** with the cached-embeddings artifact that makes it affordable, and it is
  listed under "Not measured, and why" rather than counted. [**Measured in 3.15, 2026-08-02:**
  nDCG@10 0.35569, in 1 h 4 m.] And a gated case is a case nothing
  re-checks: FiQA's parity target and its 0.37086 are now guarded only by
  `BeirDatasetDescriptorTests` and `BeirReproduction`, on a pull request, not by any run.
- ~~**Late chunking silently produces no embeddings for any text containing a newline or a tab**~~
  (Phase 3.7 whole-phase review, while provisioning the ONNX model `nightly.yml` had only been
  claiming to supply) → closed in 3.13. **Read the corrections before quoting the original entry:
  it was wrong about the scope in one direction and wrong about the severity in the other.**
  **The mechanism was as recorded.** `OnnxTokenEmbeddingGenerator` rejects input whose tokenizer
  normalization changes the text length — deliberately, because token offsets index the *normalized*
  text — and BertTokenizer's normalizer **removes** `\n` and `\t` rather than folding them to a
  space, so `LateChunkingStrategy` caught the failure and fell back to chunks with
  `Embedding = null`. The fixture's `"\n\n"` was written in `b5bea3d` and the guard that rejects it
  arrived in `d53b672`, a review commit two commits later **in the same phase**, with the only test
  that would have caught the collision already unrunnable when it landed.
  **Five times broader than recorded.** Not just paragraph breaks: `\n`, `\t`, `\r`, a *trailing*
  newline, any other control character, **NFD-decomposed text** (`"cafe" + U+0301 + " test"`,
  10 → 9, the form macOS filesystems produce) and **all CJK** (`"日本語 text"`, 8 → 14 — that one
  *grows*). Late chunking worked only on single-line, NFC, non-CJK text.
  **It corrupted tokens, not only offsets.** `"alpha\n\nbeta gamma"` normalized to
  `"alphabeta gamma"` and tokenized as `alphabet | ##a | gamma`, so a fix that restored only the
  offsets would still have embedded a word the document never contained. That is why the fix lives
  in the tokenizer plumbing rather than the late-chunking path.
  **Two more encoders had it, and neither ever tripped the guard.** `OnnxSpladeEncoder` and
  `OnnxEmbeddingGenerator` discard offsets, so the guard — the only thing that made this
  diagnosable — protected the one encoder that read them and said nothing about the two that
  embedded the merged word silently. `OnnxEmbeddingGenerator` embedded the whole SciFact corpus
  that way, which is where the 0.00314 separator "shift" came from.
  **Severity was overstated.** `EmbeddingBehavior` backfills any chunk whose embedding is null or
  empty, so the fallback degraded to *ordinary* embeddings rather than losing chunks. Nothing was
  ever unretrievable; a configured feature silently did not apply. Still a real defect, and the
  reason it was invisible for two phases is that a silent fallback on a *contract* violation is
  indistinguishable from working.
  **Fixed by substituting a space** for `\n`, `\t` and `\r` in `BertOnnxPlumbing` before every
  `EncodeToTokens` call — length-preserving, so offsets stay valid, and it matches BERT's reference
  whitespace handling, which is what corrects the tokens. **CJK and NFD are still refused**, now
  with a message naming the direction and the cause, and documented as limits in
  `docs/guide/chunking.md` rather than left to be discovered. The guard stays: probing showed CJK
  offsets going genuinely out of bounds.
- ~~**Two `EmailDocumentParser`s, and one of them breaks the other's contract**~~ (Phase 3.9
  whole-phase review) → closed in 3.11, **partly implemented and partly converted into a startup
  error**. Read what did *not* ship before treating the name as settled.
  **Shipped.** The hard parse failure is gone twice over: `application/octet-stream` is removed
  from both Templates parsers' `CanParse` — a fallback type meaning "unknown binary" is a guess no
  format-specific parser should answer — and `EmailAttachmentDispatcher` now contains a throwing
  attachment parser to its own attachment, so the next parser to accept a type and then fail costs
  one attachment rather than the document. The name collision is settled: the Templates type is
  `EmailTemplateDocumentParser`.
  **Not resolved — converted.** The `message/rfc822` overlap between `UseEmailChunking()` and
  `AddEmailParser()` is **not fixed**. Both parsers still claim it and this phase deliberately did
  not pick a winner: they serve different purposes, and which one a user wants is a question only
  that user can answer. What changed is that registering both is now an `InvalidOperationException`
  at `AddRagNet` time naming both parsers, both registration calls and the way out, instead of
  silent content loss — a 3-level nested `.eml` yielding 2 sections instead of 6. Detection works
  off a `ParserClaim` singleton each registration declares, because `CanParse` needs live instances
  and `ServiceDescriptor.ImplementationType` is `null` for every colliding registration.
  **The limit was stated too narrowly, and the whole-phase review found it in-box.** "Only a
  third-party parser goes undetected" was wrong: the boundary is *declares a claim*, not
  *first-party*. `AddRagNETServices()` auto-registers `TextDocumentParser` and
  `MarkdownDocumentParser` before `configure` runs, and neither declared one — so registering a
  parser claiming `text/plain` left a single declared claimant, the guard stayed silent, and
  selection resolved `text/plain` to the built-in while the user's parser never ran. That is the
  failure the guard exists to prevent, reachable without any third-party package. Both built-ins
  now declare their claims from `AddRagNet` itself, `MarkdownDocumentParser` including the
  `text/x-markdown` alias its `CanParse` also answers, because a source generator writes their
  registrations and cannot host a claim.
  **Still open, and not scheduled.** A parser registered through `AddParser<T>()` declares no claim
  and is undetected. `CanParse` is a predicate, not an enumeration, so nothing can discover what an
  arbitrary parser accepts without probing it against a guessed list of content types — which is a
  worse mechanism than an undetected collision, so this is a stated limit rather than a deferral.
  The guard also compares *declared* claims, not the parsers themselves: a claim that drifts from
  its own `CanParse` is caught by nothing but the two being written next to each other.
  **What the design got wrong.** §4 made registering both packages a startup error while §6 made
  that same configuration the phase-defining test, and the error it produced told the user to
  "register only one of them" when `UseEmailChunking()` bundled a parser with a chunking strategy
  and offered no way to take the strategy alone. `UseEmailChunking(registerParser: false)` and its
  twin on `UseQAPairsChunking` close that; the design doc carries the correction. The flag shipped
  as a property on the two options types and the whole-phase review moved it to a parameter on the
  call: neither chunking strategy takes options at all, so `UseEmailChunking(o => {
  o.IncludeHeaders = false; o.RegisterParser = false; })` compiled, ran, threw nothing and silently
  discarded `IncludeHeaders` — dropping the parser dropped its only reader.
  **The "still open, and not scheduled" paragraph above is closed as of 2026-08-08, by Phase 4.2
  (Parser Registration Ownership) — and its own first measurement was wrong in three ways, recorded
  here because the wrong version was persuasive and nearly reached implementation** (design §1.1).
  It is **not** "11 parsers covering ~22 content types declare nothing, two live silent
  collisions." Measured: seven of those eleven — Audio, Epub, Html, Office (×3), Pdf — register
  through `AddParser<T>()`, exactly the path this paragraph already named as the accepted,
  documented limit; counting them again as a fresh gap double-charged the same fact. Only **one**
  collision was live, `…spreadsheetml.sheet` between `ExcelDocumentParser` and
  `QAPairsDocumentParser` — `CsvDocumentParser` carries no `[Singleton]` attribute and nothing
  registers it by default, so its `text/csv` overlap is conditional on a caller adding it
  explicitly, not universal. And a claimed third collision, `image/jpeg` between the two Vision
  parsers, did not exist at all — the string in `VideoDocumentParser` is the MIME type of an
  extracted video *frame* handed to `DataContent`, not a `CanParse` claim, and it was found by
  grepping whole files rather than reading `CanParse` bodies. **The one genuine oversight was
  Vision**: it registered two parsers through `AddSingleton<IDocumentParser>`, the same mechanism
  Archive, Email and this repository's own Chunking.Templates use *with* claims, and declared none
  — an inconsistency with its own peers, not the documented `AddParser<T>()` limit.
  **What actually shipped, in the order the design required** (reversing it would have turned
  `UseQAPairsChunking()` into a startup error for every user of `Rag.NET.Parsers.Office`):
  `AddParser<T>(replaces:, replacesTypeNames:)` landed first as the override vocabulary;
  `UseQAPairsChunking()` adopted it, declaring `text/csv` against `CsvDocumentParser` and
  `…spreadsheetml.sheet` against `ExcelDocumentParser` by type *name*, so replacing an optional
  package that may not be installed is a no-op rather than a compile-time dependency; and an
  opt-in `IDeclaresContentTypes` interface lets a parser enumerate its own accepted types so
  `AddParser<T>()` can declare claims for it automatically — adopted by all nine parsers that can
  state their set honestly, closing Vision's oversight along with the rest, and held to `CanParse`
  by a new convention test (`ParserClaimCoverageTests`) rather than left to drift. `CanParse`
  itself is unchanged: a parser that cannot enumerate its types honestly simply does not opt in,
  and remains exactly as undetected as this paragraph already said it would be.
- ~~**Stack-recursive email traversal**~~ (Phase 2.1, Part C) → closed in 3.9, **implemented**.
  **Read the history before trusting the word "closed": this entry was closed once already, in
  3.6, as "re-justified, not implemented", on a premise that phase's own whole-phase review
  falsified — and it was reopened.** The false premise was that the recursion could not be
  flattened because it re-enters through the public `IDocumentParser` boundary by content-type
  dispatch, so its frames belong to arbitrary third-party parsers. That is false for the dominant
  path: a nested `message/rfc822` arrived as a live `MimeKit.MessagePart` and
  `ParseEmbeddedAsync` called `ParseMessageAsync` **directly**, with `EmailAttachmentDispatcher`
  never involved — probe-verified with an empty parsers list against a 64-level chain. Two
  inherited words did the rest of the damage and neither survived being questioned: the debt was
  recorded as a **work queue** (FIFO reorders sections, which is what everyone then argued
  against — a stack drained LIFO is depth-first and order-preserving), and the reopened entry
  named the fix `Stack<IAsyncEnumerator<DocumentSection>>`, a type that cannot express the
  traversal at all, since a section enumerator has no way to say "descend into a child here, then
  resume me". The workable unit is a traversal **frame**.
  What actually shipped: `EmbeddedTraversal` drains a `Stack<Frame<TMessage>>` depth-first,
  shared by both parsers behind one `IMessageAdapter<TMessage>` per library and an injected
  `IDescentPolicy`; `ParseMessageAsync`, `ParseAttachmentsAsync` and `ParseEmbeddedAsync` are
  deleted from both, and neither parser holds a method that calls itself. Section ordering is
  byte-identical, pinned by `EmbeddedMessageOrderingTests` written and green against the
  recursive parsers before anything changed. `MaxSupportedEmbeddedDepth = 64` stays, now bounding
  a third-party parser registered for a message content type plus fan-out sanity rather than an
  overflow that the in-place path can no longer reach.

- ~~**Fourth filename sanitizer**~~ (Phase 2.1, Part C) → closed in 3.6, **implemented**:
  `EmbeddedMessageMetadata`'s private copy is deleted and `Compose` calls
  `FileNameSanitizer.Sanitize(name, Fallback)` on the shared implementation in
  `Rag.NET.Abstractions`. One of the three recorded divergences was never one — the shared
  sanitizer takes the fallback as a parameter, so `"embedded-message"` is preserved exactly.
  **Four** changes to emitted names, all pinned by tests: the stem cap moves 64 → 128; an
  all-invalid stem now collapses to `embedded-message` rather than `___`; a genuine defect went
  with the copy, since `TrimEnd('.', ' ')` matched two characters in one pass and so stripping a
  trailing dot re-exposed a non-breaking space it could not see; and — found in the whole-phase
  review, not recorded with the other three — the two sanitizers order replacement and trimming
  oppositely, so a TAB/LF/VT/FF/CR at either edge is now substituted to `_` before trimming can
  reach it (`"report\t"` → `report_`, was `report`). `FileNameSanitizer`'s ordering is
  deliberately left alone: four other call sites depend on it, and replacing before trimming is
  arguably the more correct rule.

- ~~**Unsanitized webhook filename**~~ (found in the Phase 2.1 Part A review) → closed in 2.5:
  `GenericWebhookPayloadParser` now routes the untrusted `documentId` through
  `FileNameSanitizer` with a `"document"` fallback stem, pinned by 25 adversarial cases
  covering traversal, absolute paths, UNC, drive letters, control characters, and names that
  collapse to nothing.

- ~~**Connector metadata consistency**~~ (Phase 1.6) → closed in 2.2: all 21 connectors emit
  metadata to an enforced convention, with reserved keys guarded and `provider_id` written
  centrally.

- ~~**Graph transport-exception mapping**~~ (Phase 1.6) → closed in 2.1: `RagError.TransportFailed`
  plus a shared `src/Shared/GraphErrorMapping.cs` linked into all four Graph connectors.
- ~~**Shared `SanitizeFileName` helper**~~ (Phase 1.6) → closed in 2.1: `FileNameSanitizer`
  adopted by nine connectors, six of which previously sanitized nothing.
- ~~**Embedded-message recursion**~~ (Phase 1.5) → closed in 2.1, bounded by depth and node caps.
- ~~**PDF table dominance-guard refinement**~~ (Phase 1.5) → closed in 2.1 at a ≤ 2 words/cell
  exemption.
- ~~**Persistent-memory score normalization**~~ (Phase 1.2) → closed in 2.1 via `IScoreScaleAware`.
- ~~**`ConfigureResilience` dangling pipeline**~~ (pre-existing) → closed in 2.1: decorates
  `IEmbeddingGenerator` and `IVectorStore`.

## Milestone 1: Feature Backlog [status: complete]
**Goal:** Work the remaining feature backlog to completion — chunking, retrieval techniques, ingestion ops, resilience, parsers, connectors, and vector stores.
**Started:** 2026-07-24
**Completed:** 2026-07-26
**Definition of Done:**
- [x] All planned phases complete
- [x] Every feature row it covers ticked in features.md with tests and docs
- [x] All tests passing

### Phase 1.1: Chunking Strategies [status: complete]
**Backlog items:** Sliding Window Chunking with Overlap; Proposition Extraction Chunking; Late Chunking
**Plan:** `docs/plans/2026-07-24-chunking-strategies-design.md` + `-implementation.md`
**Completed:** 2026-07-24

### Phase 1.2: Retrieval Techniques [status: complete]
**Backlog items:** Hypothetical Document Embeddings v2; FLARE; Sparse Embedding Retrieval (SPLADE); Multi-Index Federation
**Plan:** `docs/plans/2026-07-24-retrieval-techniques-design.md` + `-implementation.md`
**Completed:** 2026-07-24 (SPLADE delivered for Qdrant + in-memory; PgVector sparse storage deferred)

### Phase 1.3: Ingestion Operations [status: complete]
**Backlog items:** Batch Ingestion Optimiser; Webhook / Event-Driven Ingestion; Embedding Versioning & Re-indexing
**Plan:** `docs/plans/2026-07-24-ingestion-operations-design.md` + `-implementation.md`
**Completed:** 2026-07-24 (Service Bus trigger and the CLI reindex command deferred as planned)

### Phase 1.4: Resilience & Cost Controls [status: complete]
**Backlog items:** LLM Fallback Chain; Rate Limiting & Cost Budgeting
**Plan:** `docs/plans/2026-07-25-resilience-cost-controls-design.md` + `-implementation.md`
**Completed:** 2026-07-25

### Phase 1.5: Document Parsers [status: complete]
**Backlog items:** EPUB Parser; Email File Parser (EML/MSG); PDF Table Extraction; OCR for Scanned PDFs
**Plan:** `docs/plans/2026-07-25-document-parsers-design.md` + `-implementation.md`
**Completed:** 2026-07-25 (OCR = Tesseract behind the `EnableOcr` compile gate; Azure Document Intelligence and PDF rasterization deferred)

### Phase 1.6: Connectors [status: complete]
**Backlog items:** Email Connector (Outlook/Exchange); Linear Issue Tracker
**Plan:** `docs/plans/2026-07-25-connectors-design.md` + `-implementation.md`
**Completed:** 2026-07-25

### Phase 1.7: Vector Stores [status: complete]
**Backlog items:** Weaviate Vector Store; Chroma Vector Store; Pinecone Vector Store
**Plan:** `docs/plans/2026-07-25-vector-stores-design.md` + `-implementation.md`
**Completed:** 2026-07-26 (Pinecone pinned to the official SDK 3.1.0 — the 4.x control-plane models cannot deserialize Pinecone Local's responses; its sparse write path is unverified against live Pinecone)

## Milestone 2: Deferred Items & Technical Debt [status: complete]
**Goal:** Follow through on what Milestone 1 delivered around rather than through — the features scoped out during brainstorming, and the debt review cycles surfaced. No delivered feature row should keep an unstated caveat.
**Started:** 2026-07-26
**Completed:** 2026-07-27
**Definition of Done:**
- [x] All planned phases complete
- [x] Every Milestone 1 deferral delivered or re-recorded with a current reason
- [x] The follow-up debt list above empty or explicitly re-justified
- [x] All tests passing

### Phase 2.1: Engineering Debt Sweep [status: complete]
**Items:** shared filename sanitizer; Graph transport-exception mapping; embedded-message recursion (EML/MSG); PDF table dominance-guard refinement; persistent-memory score normalization; `ConfigureResilience` wiring
**Plan:** `docs/plans/2026-07-26-engineering-debt-sweep-design.md` + `-implementation.md`
**Completed:** 2026-07-26 (three new debts recorded above: a fourth filename sanitizer, the stack-recursive email traversal behind the depth ceiling, and an unsanitized webhook filename)

### Phase 2.2: Connector Metadata Consistency [status: complete]
**Items:** populate `FileHandle.Metadata` across the remaining 19 of 21 connectors
**Plan:** `docs/plans/2026-07-26-connector-metadata-design.md` + `-implementation.md`
**Completed:** 2026-07-27 (also codified the tag convention, enforced reserved keys, and added `provider_id`; five connectors' narrowed API field selections remain recorded as debt). **Corrected 2026-08-05 (Phase 4.10):** this debt's own "Recorded, not fixed" section (`docs/plans/2026-07-26-connector-metadata-design.md`) priced widening Confluence, Jira, Asana, GoogleDrive and Box's field selections as needing **re-recorded WireMock cassettes** — a cost then repeated unverified by Phase 4.9's design and Phase 4.10's own design, three planning documents agreeing with each other and all wrong. **There are no WireMock cassettes anywhere near these connectors' fast unit-test suites.** Confluence's fixtures are inline JSON literals fed to a fake `HttpMessageHandler`; GoogleDrive's are a fake HTTP handler of the same shape; Box has no offline HTTP layer to fake at all — its tests call the internal `ToHandle` mapping directly, DI-registered against a real `BoxClient`. (WireMock cassettes do exist in this repository, under `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/` — but that is a separate, Docker/live-gated suite over a different connector list, and was never what this debt's cost was actually about.) Phase 4.10 widened Confluence, GoogleDrive and Box's DTO mappings without touching a cassette anywhere; see its own entry below.

### Phase 2.3: PgVector Sparse Storage [status: complete]
**Items:** SPLADE for PgVector (deferred in Phase 1.2 for lack of a native sparse type)
**Plan:** `docs/plans/2026-07-27-pgvector-sparse-design.md` + `-implementation.md`
**Completed:** 2026-07-27 (pgvector 0.8.2's `sparsevec` made it native, so the planned client-side RRF fallback was not needed; also fixed a pre-existing duplicate-row defect and built the dense ANN index the docs had long claimed)

### Phase 2.4: Azure Document Intelligence OCR [status: complete]
**Items:** whole-document OCR engine alongside Tesseract (deferred in Phase 1.5)
**Plan:** `docs/plans/2026-07-27-azure-document-intelligence-design.md` + `-implementation.md`
**Completed:** 2026-07-27 (not a second `IPdfOcrEngine` as the item assumed — that seam is per-image, so a new document-level seam was added instead, which dissolves three limitations Phase 1.5 recorded as permanent; also extended `ICostLedger` to represent per-page spend)

### Phase 2.5: Service Bus Ingestion Trigger [status: complete]
**Items:** Service Bus trigger alongside the existing webhook/polling paths (deferred in Phase 1.3)
**Plan:** `docs/plans/2026-07-27-service-bus-ingestion-design.md` + `-implementation.md`
**Completed:** 2026-07-27 (not the published "thin producer over `IIngestionJobQueue`" design — that would have settled a durable broker message into an in-memory channel and converted at-least-once into at-most-once on crash, so the trigger owns ingestion end to end instead; also fixed the latent defect that made re-ingest append rather than replace BM25 postings, which this transport would have manifested, and relocated `FileNameSanitizer` to `Rag.NET.Abstractions`)

**Not in scope:** the CLI reindex command (belongs with the CLI tool in Milestone 4); Pinecone live sparse-write verification (needs a live account — documented as a coverage gap by decision on 2026-07-26).

## Milestone 3: Quality Hardening & Evaluation [status: complete]
**Goal:** Close the evaluation-tooling gap and harden quality: RAGAS metrics, dataset tooling, A/B testing, pipeline debugging, and CI coverage for the Docker-dependent suites.
**Started:** 2026-07-27
**Completed:** 2026-08-03
**Definition of Done** (checked criterion by criterion at Phase 3.14's close, 2026-08-02 — the milestone did not close then, on two failing criteria — and re-checked at the actual close, 2026-08-03; see both assessments in `MILESTONE.md`):
- [x] All planned phases complete (all 16 as of 2026-08-02; Phase 3.14 closed last. The run-or-decline the Milestone 4 replan §5 kept in this milestone's scope — TREC-COVID/EnronQA — is decided: **explicitly declined at the close, 2026-08-03**, written on its follow-up-debts entry above, not implied)
- [x] No feature marked done in features.md lacks tests and docs — detail sections, summary matrix, and code agree — **holding as of 2026-08-03** (`81163af`): both false claims Phase 4.0's sweep found are corrected in features.md — the OTel section withdrawn from Done, the C# chunking section renamed to the package that exists — `KnownFalseClaims` is **empty**, and `FeatureClaimTests` verifies all 72 package claims across all 53 Done sections directly, with no exemptions (7 of 7 passing, re-run at the close). [Historical: the 2026-08-02 sweep found exactly **two** false claims in the then-54 Done sections — the OTel ghost and the `Rag.NET.Parsers.CSharp` wrong name — both now in the Closed debts list]
- [x] Integration/vector-store suites run in CI (Dockerized) — holding as of 2026-08-02: `ci.yml`'s Docker tier partitions the test projects with guards that fail a project landing in no tier, and the latest `main` push run (30760759923, 2026-08-02) is green through it
- [x] All tests passing — **holding as of 2026-08-03**: nightly run **30789374909**, triggered on the fix branch against a cold runner cache — the condition that exposed the dataset-download race and that no warm-cache local run reproduces — is green, `env-gated` (the gating BEIR job) passing in 19m01s after `50a80cd`/`335710c` fixed what turned out to be **three** races, not one (see the Closed debts list). The solution builds 0 warnings / 0 errors (re-verified 2026-08-03) and push CI on `main` is green

> **Correction (2026-07-27).** This milestone was scoped from the unchecked rows in
> features.md, but that file contradicted itself: RAGAS-Style Metrics and Evaluation Dataset
> Builder are marked `✅ Done` in their detail sections while their matrix rows read `[ ]`. Both
> shipped on 2026-04-11 — three months before this ROADMAP was written — with tests **and** a
> guide section that both describe the defective behaviour as correct. The guide gave
> `precision = relevant / total` as the definition of Context Precision, which is not the RAGAS
> metric, and `ScoreAsync_MalformedClaimsJson_ReturnsOneGracefully` asserts that an unreadable
> model reply scores the best possible value. The matrix row was the honest one, and the only
> signal. 3.1 and 3.2 are therefore completion phases, not greenfield ones, and they must rewrite
> existing assertions and documentation rather than only add missing ones.
>
> Corrected twice, 2026-07-27: this note first said "no tests", then "undocumented". Both were
> wrong. The tests live in `tests/Rag.NET.Tests/Evaluation/` (a subfolder of the main test
> project) and the docs in `docs/guide/evaluation.md`; both were missed by searches that were
> scoped too narrowly or truncated, and read as exhaustive.

### Phase 3.1: RAGAS Metrics — verify, test, document [status: complete]
**Backlog items:** RAGAS-Style Metrics
**Plan:** `docs/plans/2026-07-27-ragas-verification-design.md` + `-implementation.md`
**Completed:** 2026-07-28 (Context Precision was not the RAGAS metric — it ignored rank, scoring a retriever that returns the gold chunk first identically to one that returns it last; it is now rank-aware average precision. A malformed model reply scored 1.0, the best possible value, in two duplicated copies — the plumbing is now shared and an unreadable reply makes a sample unscoreable rather than perfect. Answer Relevance gained the noncommittal penalty and genuinely distinct synthetic questions, and its score is clamped. Also: a shared per-run concurrency ceiling replacing unbounded fan-out, per-sample results, chat and embedding cost recording, and a rewritten guide section. Scores changed; the guide says so.)

### Phase 3.2: Evaluation Dataset Builder — verify, test, document [status: complete]
**Backlog items:** Evaluation Dataset Builder
**Plan:** `docs/plans/2026-07-28-dataset-builder-verification-design.md` + `-implementation.md`
**Completed:** 2026-07-28 (sampling was unseeded, so a dataset could not be regenerated and any before/after comparison silently compared two different question sets — now seeded reservoir sampling. A generation the model returned nothing for became a sample with an empty question, certified by a test called `HandlesGracefully`; such generations are now dropped and counted in `EvaluationDataset.Skipped`. Also: the corpus is no longer materialised to sample from it, concurrency is bounded, and chat spend is recorded — via a shared caller moved down from `RagasJudge` rather than copied, since copying that plumbing is what put the same defect in two evaluators in 3.1.)

### Phase 3.3: A/B Testing Framework [status: complete]
**Backlog items:** A/B Testing Framework
**Plan:** `docs/plans/2026-07-28-ab-testing-design.md` + `-implementation.md`
**Completed:** 2026-07-28 (offline harness only; shadow mode deferred to Phase 3.8 because production traffic has no ground truth, so two of the four RAGAS metrics cannot run against it at all. Two decisions carry it. Execution alternates which variant leads, because whichever runs second benefits from provider prompt caching and a warm store — a fixed order hands one side an advantage and reports it as a result. And the comparison is paired with a bootstrap confidence interval, because an A/B run always produces a higher number on one side: +0.07 over 50 samples is a finding at [+0.02, +0.12] and nothing at [-0.04, +0.18]. Mutation testing was what made this phase work — a bootstrap trimmed to a 70% interval passed 23 tests, a percentile function replaced by "always return the minimum" passed 238, and a shared `Random` passed 262. All three now have tests that bite.)

### Phase 3.4: Pipeline Debugger / Trace Viewer [status: complete]
**Backlog items:** Pipeline Debugger / Trace Viewer
**Plan:** `docs/plans/2026-07-28-pipeline-debugger-design.md` + `-implementation.md`
**Completed:** 2026-07-28 (mostly a join over things that already existed — `RagTelemetry` emitted stage spans and the audit log already recorded chunks with scores, but nothing connected them. The genuinely new capability is recording what guards and sanitisers removed: `RbacRetrievalGuard` and `PiiChunkSanitiser` silently changed what the pipeline saw and nothing anywhere noted it, so "why is that chunk missing" could not be answered. Kept separate from `IAuditLog` because a compliance record and a debug buffer have opposite retention needs. Content capture is off by default behind four explicit flags, verified closed all the way to the serialised HTTP payload. Also added an enclosing `ragnet.query` span to every public pipeline entry point — without it a fan-out retriever produced one trace per sub-question, all but the last unreachable by id.)

### Phase 3.5: CI Integration Coverage [status: complete]
**Goal:** Run the Testcontainers-based vector-store and integration suites in CI. (Not a features.md row — quality-hardening scope.)
**Plan:** `docs/plans/2026-07-28-ci-integration-coverage-design.md` + `-implementation.md`
**Completed:** 2026-07-29 (there was no CI at all — every test in the repository had only ever run on a developer's machine, which is why 3.5 builds the pipeline and 4.1 narrows to packaging. Test projects declare their own needs via `RequiresDocker`, `RequiresLlm` and `RequiresSecrets`, and `Rag.NET.RepoConventions.Tests` fails when a declaration and reality disagree — in both directions, so a stale declaration is as loud as a missing one. The phase's own thesis was falsified during its final review: `Rag.NET.WebSearch.Tavily.Tests` had four real tests, a correct tier, and was in no solution, so `dotnet test --no-build` exited 0 having run none of them. Both it and its source project are now in the solution, every tier loop fails a project whose assembly is absent, and two conventions tests guard `src/` and `tests/` against a repeat. **The workflows have never executed — the first pull-request run is the real verification.**)

### Phase 3.6: Email Parser Debt [status: complete]
**Goal:** Close the two recorded email-parser debts above. Only one of them turned out to be a behaviour change; the other closes without code. (Not a features.md row — debt carried out of Milestone 2.)
- Retire `EmbeddedMessageMetadata.Sanitize` in favour of `Rag.NET.FileNameSanitizer`, accepting and documenting the naming changes. Two of the three recorded divergences are real (the 64 → 128 cap, the `embedded-message` fallback for an all-invalid stem) plus one genuine defect fixed in passing (a non-breaking space re-exposed by trailing-dot trimming) and a fourth found in the whole-phase review (replacement now runs before trimming, so a TAB/LF/VT/FF/CR at either edge becomes `_` instead of being trimmed); the fallback-stem divergence dissolves, since the shared sanitizer takes the fallback as a parameter.
- Convert the embedded-message traversal to an explicit work queue. **Attempted as a re-justification and withdrawn.** 3.6 argued the traversal could not be flattened because it re-enters through the public `IDocumentParser` boundary via content-type dispatch; the whole-phase review falsified that — the dominant path is `MessagePart` recursion entirely inside `EmailDocumentParser`, with no dispatcher hop. `MaxSupportedEmbeddedDepth = 64` stays either way and now carries the corrected reasoning, but the debt is **reopened** and rescheduled to **Phase 3.9**. See the follow-up-debts list at the top of this file.

**Completed:** 2026-07-29 (half the phase was deleting code, and the more valuable half was finding out that its own central argument was wrong. `EmbeddedMessageMetadata`'s private sanitizer is gone — 93 lines to 63 — and `Compose` calls the shared `FileNameSanitizer`. Three naming divergences were recorded in the debt; the review found the count was wrong in both directions. One dissolved, because the shared sanitizer takes the fallback as a *parameter*, so `embedded-message` is preserved rather than changed. A fourth was never recorded at all: replacement now runs before trimming, so a TAB, LF, VT, FF or CR at either edge becomes `_` instead of vanishing — reachable through `.msg`, whose subject is a raw MAPI property with no header normalization. It was found by deriving the full difference between the two implementations over three million random inputs and attributing every one of 2,228,480 differences to a named cause, which is what makes "there is no fifth" a claim rather than a hope. The traversal debt was closed as re-justified and then reopened: the argument that the recursion cannot be flattened because it re-enters through the public `IDocumentParser` boundary is false for the dominant path, where a nested `message/rfc822` arrives as a live `MessagePart` and recurses inside `EmailDocumentParser` with the dispatcher never involved — probe-verified with an empty parsers list. The original debt said "work queue"; nobody, including this phase, questioned the word, and the ordering objection that word invites does not apply to a stack drained LIFO. → **Phase 3.9**.)

### Phase 3.9: Email Traversal Flattening [status: complete]
**Goal:** Replace the stack-recursive embedded-message traversal in `EmailDocumentParser` and `MsgDocumentParser` with an explicit `Stack<IAsyncEnumerator<DocumentSection>>` drained LIFO, so nesting depth costs heap rather than stack. (Not a features.md row — debt reopened out of Phase 3.6.)

> **Runs next, before 3.7 and 3.8.** It keeps the number it was assigned when it was scheduled after 3.8 — commit messages, the 3.6 design and the 3.6 plan all already point at "Phase 3.9", and renaming it would falsify those references to buy nothing. Numbers here record when a phase was created, not the order it runs in.

Reopened because 3.6 closed it on a premise its own whole-phase review falsified; the corrected analysis and the probe that falsified it are recorded in the follow-up-debts list at the top of this file.

**Scope:**
- Flatten the in-place `MessagePart` path first — it is the dominant one, it is entirely internal (`ParseMessageAsync → ParseAttachmentsAsync → ParseEmbeddedAsync → ParseMessageAsync`), and it is the path the ~500-level overflow was measured on.
- **LIFO, not FIFO.** A queue reorders sections; a stack drained depth-first reproduces the recursive order byte for byte. Pin that with a test comparing flattened output against the recorded pre-change section sequence for a multi-branch fixture, not merely against a section count.
- `MaxSupportedEmbeddedDepth = 64` **stays**. It stops being an overflow guard and becomes a bound on a third-party parser registered for a message content type, plus a fan-out sanity limit. Its XML says so already and will need narrowing again, not deleting.
- **Set `MaxEmbeddedMessages` deliberately in any depth test.** At its default of 50 a 64-level chain stops on the fan-out cap, not the depth ceiling — the 3.6 probe hit exactly that and would have measured the wrong bound had it been read at face value.

**Not in scope:** raising `MaxEmbeddedDepth`'s default, or raising the ceiling. Nobody has asked for a deeper chain; this phase changes what the ceiling is protecting against, not where it sits.

**Completed:** 2026-07-29 (one internal depth-first driver, `EmbeddedTraversal`, draining a `Stack<Frame<TMessage>>`, shared by both parsers behind an `IMessageAdapter<TMessage>` per library and an injected `IDescentPolicy`. `EmailDocumentParser` goes 171 lines → 52 and `MsgDocumentParser` 185 → 52; `ParseMessageAsync`, `ParseAttachmentsAsync` and `ParseEmbeddedAsync` are gone from both, and neither parser now holds a method that calls itself. **The type named in the Goal above cannot express this traversal.** `Stack<IAsyncEnumerator<DocumentSection>>` was inherited from the 3.6 review: a section enumerator can say "here is a section" or "I am finished" and has no way to say "descend into a child here, then resume me", so driving off one would need a marker type smuggled through the stream. That is the second inherited word in this entry's history to fail on first inspection, after "work queue" — the transferable finding is that a debt note's vocabulary propagates into every later decision about it. The descent policy is a seam, not decoration: the overflow floor was ~500 levels and the ceiling is 64, so **no test reaching through `EmailParserOptions` can construct a case that would ever have overflowed** — a 64-level test passes identically before and after and certifies nothing, the same shape as the vacuous guards this milestone keeps finding. Wiring an always-yes policy drives the driver 10,000 levels in ~98 ms, and that test was confirmed able to fail: made recursive, it terminated the runner with `0xC00000FD` rather than going red. Ordering was pinned first — `EmbeddedMessageOrderingTests` was written and green against the recursive parsers, and its sequence is byte-identical afterwards. `MaxSupportedEmbeddedDepth` stays at 64 with its XML narrowed a second time in two phases: it now bounds a third-party parser registered for a message content type, reached through the dispatcher path, plus fan-out sanity, and the ~500 figure is kept only as the floor of a traversal that no longer exists. The whole-phase review found the 3.6 pattern recurring inside the phase meant to have learned it: three places still asserted stack-recursion in the present tense, and the worst was not a comment but the `ArgumentOutOfRangeException` thrown by `AddEmailParser` — a runtime message on the public API, telling a caller the parser is stack-recursive from the same assembly whose XML says otherwise, unpinned by any test. All three corrected. Its readability verdict is worth keeping: **+272 lines across 7 files replacing logic that lived in 2**, and the win is deduplication rather than the driver — the old code held two near-identical traversals with a standing obligation to keep them in sync, which this repository has a documented history of failing. The `Peek`-not-`Pop` invariant is subtle and carried entirely by a comment.)

### Phase 3.11: Duplicate Email Parser [status: complete]
**Goal:** Stop `Rag.NET.Chunking.Templates`' email parser from claiming content types it cannot parse, which turned one unknown-extension attachment into a failed document parse. (Not a features.md row — a bug found in the Phase 3.9 whole-phase review.)
**Plan:** `docs/plans/2026-07-29-duplicate-email-parser-design.md` + `-implementation.md`
**Completed:** 2026-07-29 (the defect was four lines and the phase was six tasks, because the four lines were the only part anybody had noticed. `application/octet-stream` is gone from both Templates parsers' `CanParse`; `EmailAttachmentDispatcher` contains a throwing attachment parser to its own attachment, driven manually since C# forbids `yield return` inside a `try` with a `catch`, and rethrowing `OperationCanceledException` so a cancelled ingestion does not become a silently partial one; the `message/rfc822` overlap is now a startup error; and the Templates type is `EmailTemplateDocumentParser`. **The design contradicted itself and the contradiction was load-bearing.** §4 made registering both packages illegal while §6 made that exact configuration the phase-defining test, so Task 1's end-to-end test and Task 4's guard could not both stand. Underneath was the worse problem: the error said "register only one of them" while `UseEmailChunking()` registered a parser *and* a chunking strategy, with no way to take the strategy alone — it instructed the user to do something the API did not permit. A parser opt-out makes the instruction followable and makes the pairing a user would actually want — email-shaped chunking with `Rag.NET.Parsers.Email` parsing — reachable for the first time; the `ParserClaim` carries the opt-out so the message can quote it verbatim. (It shipped as `EmailChunkingOptions.RegisterParser` and its twin on `QAPairsChunkingOptions`, and the whole-phase review moved it to a `registerParser` parameter on the two calls: neither chunking strategy takes options, so `RegisterParser = false` silently discarded every other property on the object it lived on.) **Two verification findings worth more than the fix.** `ParserClaim.For` keys on `FullName`, and mutating it to `typeof(T).Name` turned four conflict tests from passing to "no exception was thrown": both colliding types were literally named `EmailDocumentParser`, so short names collapsed the two claimants into one and the guard stopped firing on the only collision it existed for. And the phase nearly shipped with **no end-to-end regression test at all** — `QAPairsAttachmentClaimTests` was re-run against a reverted Task 2 and passed, because attachment containment makes "a parser wrongly claimed this type and threw" produce sections identical to "nothing claimed it". The two states differ only in the dispatcher's log line, which is what the test now asserts and what makes it fail against the reverted fix. Registration-order roulette was also measured rather than assumed and turned out not to exist for the octet-stream defect: `Rag.NET.Parsers.Email` declines the type outright and `AddRagNETServices()` runs before `configure`, so both orders failed identically — registering the email package first was never a workaround. **The whole-phase review then found both verification findings had decayed and a third had never held.** The `FullName` mutation reddened four tests only while both colliding types were named `EmailDocumentParser`; Task 5's rename, in this same phase, abolished that coverage without replacing it — afterwards the mutation reddened one test, for the unrelated reason that it asserts full names appear in the message. A pair of parsers sharing a short name across namespaces now pins the rule directly. The guard itself was blind to `TextDocumentParser` and `MarkdownDocumentParser`, auto-registered before `configure` and declaring nothing, so a user parser claiming `text/plain` produced silence rather than a conflict — the in-box version of the failure the guard exists for. And `EmailTemplateDocumentParser`'s half of the octet-stream removal was still pinned by a `CanParse` theory alone, the exact shape `QAPairsAttachmentClaimTests` argued against; it is now pinned end-to-end through top-level `ParseBehavior`, the second failure route §1 named and nothing covered.)

**Deliberately not resolved:** which parser should own `message/rfc822`. They serve different purposes and the startup error asks the user. **Still open:** parsers registered through `AddParser<T>()` declare no claim and go undetected — see the Closed debts list for why that is a stated limit rather than a deferral, and for the whole-phase review's finding that "third-party" was the wrong word for that limit.

**Was not in scope:** merging the two parsers, or changing what the Templates parser emits for a `.eml` it legitimately wins.

### Phase 3.10: Archive Parser (ZIP) [status: complete]
**Goal:** Parse `.zip` archives by dispatching each entry to the registered parser for its content type, closing a gap where zipped email attachments are silently dropped. (features.md row: **Archive Parser (ZIP)**.)
**Plan:** `docs/plans/2026-07-29-archive-parser-design.md` + `-implementation.md`

Raised while designing 3.9. Today a `.zip` attachment reaches `EmailAttachmentDispatcher`, matches no parser, logs a warning and yields nothing — the archive's contents never reach the index. Every attachment type with no registered parser behaves this way; the warning is the only signal that content was dropped. That default is deliberate and stays, but zip is common enough in real mail that it should not be one of the misses.

Runs **after 3.9**, which is what makes it cheap: the shared traversal driver and the injected descent policy are the machinery a nested-container parser needs, and building them once for two containers beats building them twice.

**Scope:**
- Dispatch each entry by content type through the existing parser registry, matching how the email parsers already dispatch attachments.
- **Cap decompression ratio and entry count.** A zip bomb expands without bound from a small file, and an archive's own headers cannot be trusted to declare it. This is the first parser to accept an untrusted structure that *expands*, so the limits are part of the feature, not a hardening pass afterwards.
- **Sanitize entry names.** `../` traversal and absolute paths are the classic archive defect; `FileNameSanitizer` in `Rag.NET.Abstractions` already exists and is the fourth-copy lesson from 2.1 — use it rather than writing another.
- **Share one budget across nested containers.** `zip → .eml → zip` is the same unbounded-recursion shape the email parsers bound. `EmbeddedMessageContext` carries depth and budget through `DocumentMetadata.Tags` precisely so the accounting survives a hop through `IDocumentParser`; the archive parser rides that channel rather than inventing a second one.
- **Make `MessageChild<TMessage>` a real union** (the 3.9-created debt above). This phase adds a third container shape to that type, which is the moment its "descend, or open — never neither" rule stops being enforced by two adapters that happen to be written correctly. — **Not done, and the sentence above is false.** See the Completed paragraph; the debt is rescheduled rather than left open.

**Not in scope:** other archive formats (7z, tar, rar), encrypted archives, and any change to the warn-and-skip default for unregistered content types.


**Completed:** 2026-07-30 (a **promotion plus an addition**, not the reuse this entry predicted. Every piece the archive parser needed was `internal` to `Rag.NET.Parsers.Email` — the depth/budget context, the budget, the extension→content-type map and the attachment dispatcher — so the phase opens by moving all four into `Rag.NET.Abstractions/Containers` as `ContainerContext`, `ContainerBudget`, `ContentTypeMap` and `ContainerEntryDispatcher`, under the acceptance criterion that no existing test changes an *assertion*. None did; Email stayed at 76 and Templates at 51 across the move. **Sharing the accounting is a security property, not tidiness.** The tags carry depth and entry budget across the `IDocumentParser` boundary, and an archive parser holding its own pair would leave `zip → .eml → zip` counted by two bounds that each look correct in isolation while neither bounds the chain — an attacker who alternates formats walks through both. `ContainerContentTypes` centralises which content types count as containers for the same reason, and states the trap that follows: a container format not listed there is not bounded at all, its own tests pass, and nothing complains.

**Phase 3.11's containment swallowed this phase's headline behaviour, and every test stayed green.** `ContainerEntryDispatcher` catches everything an entry parser throws, which is right — it cannot tell a decompression bomb from a corrupt PDF, and one bad entry must not cost the archive — but it caught `LimitedReadStream`'s refusal too, degrading a zip bomb into a warning per entry rather than a refused archive. The *bound* still held, since the stream stops producing bytes either way, so nothing measuring the bound could see it: the tests passed while the behaviour they were written for was absent. The fix during the phase re-checked the archive-wide total in `ZipDocumentParser` after each entry, where refusing the archive is this parser's decision rather than the shared machinery's.

> **The phase congratulated itself on a fix that covered half the problem, and the whole-phase review found the other half.** `LimitedReadStream` throws for **two** bounds — the ratio and the total — and only the total was re-checked, so a **ratio** breach was still swallowed. At *default* options a 1 MB-of-zeros entry at a genuine ~1000:1 against `MaxCompressionRatio = 100` produced no exception, indexed the sibling entry and logged one warning: precisely the degradation the paragraph above claims to have prevented, still present in the cap that detects bombs most directly. It survived because every ratio test drove `LimitedReadStreamTests`' own read loop, which never touches the parser or the dispatcher, and the single end-to-end bomb test covered only the total — deleting the ratio refusal cost two unit tests and no end-to-end one. Fixed by recording a ratio breach on `ArchiveReadBudget`, where it outlives the containment, and re-raising both bounds together after each entry, ratio first so the order of refusal holds end to end as well as inside the stream. **The transferable part is not "one more instance of the containment lesson" but a narrower one: a fix written for a symptom was scoped to the throw site that produced the symptom, not to the set of throws the containment could swallow.** The review also found the byte budget itself per-archive rather than per-document, so a nested archive got a fresh allowance — the same phase, the same file, and a bound worth roughly `51 ×` what it was configured as. Both are fixed on `feature/archive-parser`, each with end-to-end coverage and a mutation check.

**This is the second time in this milestone that containment quietly undermined the thing a phase was about while the tests stayed green** — the first was 3.11's own `QAPairsAttachmentClaimTests`, which passed against a reverted fix because containment makes "a parser wrongly claimed this type and threw" produce sections identical to "nothing claimed it". Same mechanism, same signature, two phases apart, and the transferable finding is the one worth keeping: **a containment boundary makes the failure it contains unobservable to every assertion downstream of it, so a test for a behaviour on the far side of one is presumed blind until it has been watched to fail.** 3.11 recorded this as a lesson about adding containment in the same phase as a routing fix; it is wider than that — the containment here was two phases old and inherited.

**Three things the plan did not have, each found by building it.** `ContentTypeMap` had no `.zip` entry, so a zip inside a zip typed as `application/octet-stream`, matched no parser and was warn-and-skipped — which looks exactly like the designed degradation and is not, because an entry that never reaches a parser never counts against the shared budget. `ArchiveParserOptions` as specified had only the three bomb caps and no nesting bounds, so there was nothing to build a `ContainerContext` from; `MaxNestingDepth` and `MaxNestedContainers` were added, defaulted to match `EmailParserOptions` deliberately, since design §5's claim that an alternating chain is bounded by the same numbers as a non-alternating one holds only while the two packages agree. And `ArchiveReadBudget` had to be per-archive rather than per-stream: the plan put the counting in `LimitedReadStream`, one of which exists per entry, which would have enforced `cap × entries` instead of `cap` — the same shape of hole `ContainerBudget` documents for nesting, in a different place.

**The `MessageChild<TMessage>` debt scheduled into this phase was not closed, because the premise that scheduled it here is false.** The entry says this phase "adds a third container shape to that type". It does not add any: `MessageChild<TMessage>`, `IMessageAdapter<TMessage>` and `EmbeddedTraversal` model an *email message tree* — live library message objects, descend-or-open — and stayed `internal` to `Rag.NET.Parsers.Email`. `ZipDocumentParser` drives its own `foreach` over `ZipArchive.Entries` and calls `ContainerEntryDispatcher` directly; it has no adapter, constructs no `MessageChild`, and the type still has exactly the two adapters 3.9 left it with. So the debt is neither closed nor worsened — it is exactly as latent as it was, and touching it here would have been a refactor with no caller. Rescheduled on a corrected trigger rather than left open; see the follow-up-debts list. Counts: Archive **44**, Email **76**, Templates **51**, `Rag.NET.Tests` **1325**, RepoConventions **9**, build 0 Warning(s) 0 Error(s).)

### Phase 3.7: Retrieval Quality Benchmark Harness [status: complete]
**Goal:** Measure retrieval quality against public benchmarks with published reference numbers, so correctness is *demonstrable* rather than asserted. (Not a features.md row — quality-hardening scope.)
**Plan:** `docs/plans/2026-07-30-retrieval-quality-benchmark-design.md` + `-implementation.md`
**Docs:** `docs/reference/retrieval-quality.md`

Distinct from `EvaluationDatasetBuilder` (Phase 3.2), which synthesises QA pairs from *your* corpus: useful for iterating on your own data, but it can only show that a change moved a number, never that the number is right. Also distinct from the existing `Rag.NET.Benchmarks` project and `docs/reference/benchmarks.md`, which measure **speed**; this measures **quality**. Keep the names apart.

**First cut: SciFact only, to prove parity.** ~5k documents, runs in seconds, and its abstracts are short enough that chunk-to-document aggregation is easy to validate. One number matching the published reference is worth more than five unvalidated ones — a harness defect is inherited by every dataset added after it.

**The methodological trap, recorded up front.** BEIR is evaluated at **document** level: qrels map `query_id → doc_id`, and nDCG@10 ranks documents. Rag.NET chunks. Ranking *chunks* computes a different quantity that merely resembles nDCG@10. The harness must map chunk → parent document, max-pool to one score per document, dedupe, and only then take the top k. This bites unevenly, which is what makes it dangerous: SciFact abstracts and ArguAna arguments are mostly single-chunk so those numbers look plausible, while FiQA and TREC-COVID have long documents where the discrepancy is real — a table that is right in the cheap places and wrong in the expensive ones. [**Corrected by Phase 3.12 (2026-07-31): "mostly single-chunk" is false, and it is false of the two datasets it names.** Measured against `ChunkingOptions`' stock 512 characters, **99.2%** of SciFact's abstracts and **87.3%** of ArguAna's arguments exceed the chunk size, against FiQA's **51.0%** — the reverse of the ordering this paragraph assumes. The default chunker produced 56,707 units from SciFact's 5,183 documents and 82,618 from ArguAna's 8,674 (3.16's packing later cut these to 20,155 and 24,003; the percentages above are document lengths and do not move). The aggregation was a no-op on SciFact because the **parity protocol indexes one chunk per document**, which is what the published figures embed, and not because of anything about the length of an abstract. Right conclusion — SciFact was the right first dataset and its number is unaffected — reached from a premise that does not hold.] Also pin BEIR's `title + text` concatenation and cosine over normalised embeddings; both shift the numbers.

**Scope:**
- `Rag.NET.Benchmarks.Quality` — BEIR qrels/corpus/queries loaders, nDCG@k, Recall@k, MRR implemented natively (no `pytrec_eval` dependency), and the chunk-to-document aggregation above.
- Datasets downloaded on demand and cached; **never vendored into the repo**. Record each dataset's licence — they differ across BEIR.
- Env-gated like the `RAGNET_*` precedents. Corpus scale is an *embedding cost* problem rather than a disk one, so anything past SciFact needs a cached-embeddings artifact and stays out of default CI.

**Later, once parity holds:** FiQA (long documents, where HyDE should show lift), ArguAna as a **negative control** (HyDE should *not* help; a harness that shows lift everywhere is broken), then EnronQA for the private-corpus and multi-tenant story. Ablation table — baseline dense → +BM25 hybrid → +HyDE → +reranker — using the behaviors that already exist. → **Phase 3.12**, now that parity does hold. [**The HyDE half of this sentence was falsified when Phase 3.15 measured it:** FiQA is the *flat* cell (−0.0054) and SciFact — which this sentence does not even mention — took the lift (+0.0541), from the same model, prompt and cache. The negative control held: ArguAna −0.0014, and the harness demonstrably does not show lift everywhere.]

**Not in scope here:** comparative tables against other libraries. Legitimate and worth doing, but only credible with genuinely equivalent configuration (same embedding model, chunk size, top-k), which is a separate piece of work and the part such tables are usually attacked on.

**Completed:** 2026-07-30 (**SciFact nDCG@10 = 0.64593** against a published ≈ 0.645 and a band of 0.625–0.665, with Recall@10 = 0.78667 and MRR@10 = 0.60483 over 300 judged queries — 809 of the 1,109 excluded as unjudged — through 5,183 documents and Rag.NET's real embed → store → retrieve path in ~355 s. Every component is the library's own; nothing in the harness is a benchmark-only reimplementation, which is the point, since a harness built out of purpose-made parts measures the harness. **The phase's first premise was already false when it started.** The design and the plan both assume a local dense embedder exists; none did — `OnnxTokenEmbeddingGenerator` is token-level for late chunking and `OnnxSpladeEncoder` is sparse, so there was no way to run Rag.NET with a local, free, offline dense embedder at all. `OnnxEmbeddingGenerator` was added to `Rag.NET.Embeddings.Onnx` rather than to the benchmark, because the gap was the library's. **The number is a conjunction, not a measurement.** Landing in-band needs five independent settings simultaneously right, and the parity run cannot say which one broke: padding excluded from the mean; `[CLS]` and `[SEP]` included in it, as sentence-transformers includes them; truncation at 256 to match `max_seq_length` rather than windowing and stitching; IDCG over `min(|relevant|, k)` and never over `k`, which decides **277 of the 300** judged queries single-handedly, since they have exactly one relevant document and IDCG must therefore equal exactly 1; and only judged queries scored, since scoring the other 809 as zero divides the mean by ~3.7 and reads as retrieval collapse rather than as a harness bug. Each is pinned by its own test for that reason. **Two settings the harness gets right are deliberately NOT on that list**, because on this dataset the number cannot see either: the chunk-to-document aggregation order (below), and Recall's denominator being *every* relevant document rather than `min(|relevant|, k)` — the exact inverse of the IDCG rule above, which is what makes confusing the two so easy. SciFact's most-judged query has 5 relevant documents, so `min(|relevant|, 10)` equals `|relevant|` for all 300 judged queries and the wrong denominator gives the same Recall@10 of 0.78667. `IrMetricsTests` guards that one; nothing about 0.78667 does. **Three design errors, all recorded in the design rather than silently rewritten.** §2 asserts BEIR concatenates `title + "\n" + text`; upstream `sentence_bert.py` declares `sep: str = " "`, and both were measured — space 0.64593, newline 0.64907, a shift of 0.00314 with the space closer to published. Both pass the band, which is why this had to be checked against upstream instead of inferred from a green run. [**Corrected by Phase 3.13:** the 0.00314 was this project's newline-deletion defect, not a property of the separator — the normalizer deleted `\n` and merged each title's last word into its abstract's first across all 5,183 documents. With the substitution 3.13 shipped, both separators produce 0.64593 and the concatenation moves the number by nothing. The space is still the default because upstream uses one; the number never could have chosen.] §6 requires `<RequiresSecrets>true</RequiresSecrets>` on the `src` project because it reads `RAGNET_*`; the property is **inert** there — `RepoConventions` scans `tests/*/` and `nightly.yml` globs `tests/*/*.csproj` — and the reasoning it was standing in for pointed the wrong way, since `RequiresSecrets` is per project and would have carried all 70 arithmetic tests out of the gating tier along with the parity test. What shipped: the env read stays in `src/` on `BeirDatasetCache`, and the parity test lives in its own `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`, so the arithmetic gates every push and the run needing an 86 MB model and a downloaded corpus runs nightly. And §4 and §5 contradict each other outright: §5 justifies ±0.02 with "the chunk-to-document bug shifts SciFact by considerably more than 0.02" while §4 says SciFact abstracts are "mostly single-chunk, so those numbers look plausible either way". §4 is right — it is why SciFact was chosen — and the shipped harness is starker still, indexing one chunk per document because that is what the published figure embeds, so max-pooling is a literal no-op here and the two orderings return identical rankings. **On this dataset the band does not guard the aggregation order at all**; `DocumentRankingTests`' four-chunks-in-one-document fixture is the only thing that does, and cut-then-pool fails **3 of its 13** tests, the disagreement being documents going *missing* rather than being reordered. Checked rather than argued: one chunk per document and `TopK` equal to the cutoff means both orderings pool the same ten hits, so the ranking is the same for all 1,109 queries and nDCG@10 is identically **0.64593** — confirmed by mutating `DocumentRanking` to cut-then-pool and re-running the full measurement, which passes unchanged at both separators. That is an argument for the fixture, not against the band — which still guards pooling, normalisation, the separator [**not any more, per 3.13:** both separators now give 0.64593, so the band cannot see the concatenation either], the IDCG cap, the exclusion rule and whether the whole corpus was indexed — but the overstated justification was not allowed into the documentation, because a band credited with catching a defect it cannot catch is the same shape as the vacuous guards this milestone keeps finding. SciFact's licences are recorded from upstream rather than assumed, and they are two: ODC-By 1.0 for `corpus.jsonl` and CC BY 4.0 for queries and qrels, with the Hugging Face mirror declaring a single `cc-by-sa-4.0` that matches neither and adds a share-alike obligation upstream does not impose — upstream treated as authoritative, the disagreement recorded rather than resolved. Datasets download on demand into `RAGNET_BEIR_CACHE`, are verified against BEIR's published MD5 onto a `.partial` file deleted on any failure, and are never vendored. The BM25 comparability debt is recorded with its numbers and scheduled → **Phase 3.12**.)

### Phase 3.8: A/B Shadow Mode [status: complete]
**Goal:** The production half of the A/B framework — wrap a live pipeline, return the primary answer to the caller, run the secondary out-of-band and score it. (Not a features.md row of its own; it is the deferred half of the `A/B Testing Framework` row delivered in 3.3.)
**Plan:** `docs/plans/2026-08-02-ab-shadow-mode-design.md` + `-implementation.md`
**Docs:** `docs/guide/shadow-mode.md`

Scoped out of Phase 3.3 deliberately, because it is a production-path concern with failure modes the offline harness does not have, and bolting it on would have given it none of the design attention they need:

- **No ground truth.** Production traffic has no reference answer, so Context Precision and Context Recall — which *throw* on an empty `ReferenceAnswer` — cannot run at all. Only the reference-free metrics apply, and the docs must say so rather than implying all four.
- **Doubled spend on every request**, invisible unless each variant gets its own ledger.
- **Fire-and-forget loss.** Secondary work running out-of-band is lost on host shutdown, and a naive implementation drops it silently.
- **The secondary must never break the primary.** `IRagPipeline.AskAsync` throws rather than returning a `Result`, so an unhandled secondary failure would surface on a request the caller had already been served.

**Completed:** 2026-08-02 (`ShadowRagPipeline` decorates `IRagPipeline` via `UseShadow<TSecondary>`; everything lives in `src/Rag.NET.Evaluation/Shadow/` — needing only `IRagBuilder` and the DI abstractions, so the core package gains no Evaluation dependency and Evaluation gains none on core. **Each of the four failure modes above was closed structurally rather than by a flag.** *No ground truth* became the argument for the design's central decision — **capture, don't score**: `ShadowCapture` stores no reference answer deliberately, and `ShadowReplay.From(captures, references)` turns stored captures into `RagAbTester.CompareAsync`'s input, where an unannotated replay scores with the two reference-free metrics and references supplied at replay time unlock all four — proven by a test that feeds the real Context Precision and Context Recall evaluators an annotated replay, which is what inline scoring could never do. *Doubled spend* is off by default and visible when on: `SampleRate` defaults to **0.0** and an out-of-range rate is refused rather than clamped — a clamped rate is a rate nobody chose — while the secondary's spend is recorded per capture as a before/after diff over a dedicated ledger, honest only because the consumer runs secondaries one at a time; the primary's spend is **honestly absent**, see below. *Fire-and-forget loss* became counted loss: deliberately not `IngestionJobProcessor`'s shutdown, which treats stopping-token cancellation as a clean exit and silently abandons the queue — `StopAsync` completes the queue, drains within `DrainTimeout` (default 5 s) on a separate drain-deadline CTS with `WaitAsync` bounding even a store that ignores its token, and the remainder is counted in `AbandonedCount` and logged; `DroppedCount` (the queue is `BoundedChannelFullMode.DropWrite`, exact via the synchronous drop callback) plus `AbandonedCount` is the entire gap between the configured sample rate and what the store holds, with the identity `enqueued − dropped − failed − abandoned = processed` stated on the five `ragnet.shadow.*` counters. *Secondary never breaks the primary* is the isolation contract: the primary's completed result is what gets scheduled — the caller is served **before** anything shadow-related happens — the enqueue completes synchronously inside a catch, and the secondary runs on the background consumer where a throw becomes a persisted `ShadowVariantFailure`. Verified by running the named wrong implementation — `try/catch` around an awaited secondary, which passes every "does it throw" test while coupling the primary's latency to the secondary's — against the suite: **5 of 12 decorator tests fail** in 257 ms with no hangs, so the suite pins the structure and not just the exception path. **Four things the plan and design got wrong, recorded rather than absorbed.** The plan was **missing the replay bridge entirely**: the design's whole argument for capture over inline scoring is "score it offline with the harness 3.3 built", and no task converted captures into `CompareAsync`'s input — without it a user hand-writes ~40 lines of replay pipeline and the promise is not kept. Found by Task 1, added as Task 6b, shipped as `ShadowReplay`. Design §2's **"per-variant spend is already solved" oversells**: `RagAbTester.SpendAsync` measures a whole sequential run, which transfers to the consumer — one secondary at a time on its own ledger — but not to the primary, which serves concurrent traffic on a shared ledger; with `ICostLedger`'s current read surface (aggregate time windows, no per-caller attribution) no honest per-request primary figure exists, so the primary's spend is absent by design, never zero, and this is not a limitation awaiting a fix — it awaits a different ledger read surface. **`BackgroundService.StartAsync` schedules `ExecuteAsync` deferred on the stopping token, so a fast stop can cancel it before it ever ran** — measured at **1,921 of 2,000** immediate start→stop cycles never running `ExecuteAsync` — which means a drain living only in `ExecuteAsync` loses everything silently *even when it correctly avoids the stopping token*; the drain therefore lives in `StopAsync`, reading the queue's non-waiting `TryDequeue` so it depends on the queue's contents and never on the loop having started. This is genuine .NET behaviour, recorded here for whoever writes this repo's next background service. And **`IRagPipeline` has a fifth member the plan's delegation list omitted**: `AskStreamingAsync` — delegated, deliberately not shadowed, because a streamed answer completes on the caller's schedule and pairing it would mean buffering the caller's stream, which puts shadow work on the request path. Captures hold the question and retrieved document text **verbatim**; `IShadowCaptureSanitiser` defaults to none — a documented choice, failing safe: a sanitiser that throws or returns null costs the capture, never persists it unsanitised — and retention, encryption and deletion are named as the `IShadowCaptureStore` implementer's, with the in-memory default explicitly not production storage. Mutation checks beyond the decorator's: flipping the queue back to `FullMode.Wait` fails the non-blocking test, and replacing the shutdown handling with the neighbouring clean-exit catch fails the queued-at-shutdown test. features.md's `A/B Testing Framework` row was updated rather than duplicated — no new row, no new `KnownFalseClaims` entry — with side-by-side review recorded as still not built, since the old paragraph had bundled it into 3.8's schedule though 3.8's goal never included it. Counts: `Rag.NET.Evaluation.Tests` **381**, `Rag.NET.RepoConventions.Tests` **30** (29 + 1 by-design skip), build 0 Warning(s) 0 Error(s).)

### Phase 3.12: BEIR Expansion & Ablation Table [status: complete]
**Goal:** Add the datasets and the ablation table Phase 3.7 deliberately deferred until parity held. (Not a features.md row — the second half of 3.7's quality-hardening scope.)
**Plan:** `docs/plans/2026-07-31-beir-expansion-ablation-design.md` + `2026-07-31-beir-expansion-implementation.md`
**Docs:** `docs/reference/retrieval-quality.md`

Created when 3.7 completed. Parity holds — SciFact nDCG@10 = 0.64593 against a published ≈ 0.645 — which was the precondition 3.7 attached to every item below. The harness is built and verified; this phase spends it.

**Scope:**
- **FiQA** — long documents, where HyDE should show lift, and the first dataset where chunk-to-document max-pooling is not a no-op. 3.7 measured SciFact with one chunk per document, so **nothing in the parity number exercises the aggregation order**; `DocumentRankingTests`' fixture is the only thing that does today, and FiQA is where the band starts guarding it too. [**Corrected by this phase, and it is the contradiction §0 of the design was written to resolve.** The second sentence is right and the first and third are wrong, for one reason: max-pooling is a no-op under the **parity protocol**, not on SciFact's documents. Every dataset is measured under that protocol against its published figure, so **no parity band will ever guard the aggregation order** — not FiQA's, not any. The length premise is also false: 99.2% of SciFact's abstracts exceed the 512-character chunk size against FiQA's 51.0%, so if document length decided this, SciFact would have exercised it first. What exercises the aggregation is the **real run** this phase added, where ArguAna pooled on 1,406 of 1,406 queries against the parity leg's 0 — and that run is compared to our own parity measurement, because there is no published figure for its protocol.] [**And the "HyDE should show lift" half fell in 3.15:** FiQA showed none (−0.0054); the lift the table does show is SciFact's (+0.0541).]
- **ArguAna as a negative control.** HyDE should *not* help there. A harness that shows lift everywhere is broken, and without a case where the expected answer is "no change" nothing can distinguish a working ablation from an optimistic one.
- **TREC-COVID** — the first graded-relevance dataset. `IrMetrics` uses `2^rel - 1` and has a graded fixture, but no graded dataset has ever been through it.
- **EnronQA**, for the private-corpus and multi-tenant story.
- **A cached-embeddings artifact.** Past SciFact the cost is embedding time rather than disk — 5,183 documents already take ~355 s of CPU — so anything larger cannot re-embed per run.
- **Ablation table**: baseline dense → +BM25 hybrid → +HyDE → +reranker, using the behaviours that already exist.

**The `+BM25 hybrid` row is the one to be careful with**, and the reason is recorded in the follow-up-debts list at the top of this file with its numbers: our BM25 and Anserini's are not two settings of the same retriever, so that row is incomparable to any published BM25 reference **while sitting in a table whose first row is validated against one**. Decide what the row is before publishing it, not after. [**Decided in 3.15, before publication:** the row is a Rag.NET-internal comparison, labelled as such — the debt is closed in the list above.]

**Not in scope:** comparative tables against other libraries — the same reasoning 3.7 gave. And no change to `InMemoryBm25Index` for benchmark comparability; §2 of the 3.7 design rejected building a benchmark-only analyzer for the dense path, and the objection is unchanged here.

**Scope split, decided after the design was approved and before the plan was written.** The four items above are four independent pieces, and the last of them needs two model dependencies nothing in this project has. **§1–§3 shipped here** — the two-run protocol, the embeddings cache, FiQA and ArguAna. **§4–§5 moved to Phase 3.15**, the ablation table, along with **TREC-COVID**, **EnronQA** and the `+BM25 hybrid` debt this entry owned. The design keeps both sections rather than moving them, because the reasoning about what each row *is* was the expensive part.

**Completed:** 2026-07-31 (**three parity numbers against three published references, every one of them in band: SciFact 0.64593 against 0.64508, FiQA 0.37086 against 0.36867, ArguAna 0.50432 against 0.50167** — one chunk per document, truncated at 256 tokens, over 5,183, 57,638 and 8,674 documents, through Rag.NET's own embed → store → retrieve path. Published figures were **looked up rather than assumed**, per the plan's refusal to supply them: MTEB's official results repository at `sentence-transformers__all-MiniLM-L6-v2/8b3219a929…`, `mteb_version 1.12.75`, test split, cited by dataset revision on each descriptor. That path segment is the model's own Hugging Face commit, so the figures are pinned to a **revision** rather than to a name. The BEIR paper is not a second opinion on any of them — it does not evaluate this model at all, its only MiniLM being the ms-marco cross-encoder — so the plan's "MTEB and the BEIR paper sometimes differ" had no disagreement to adjudicate. The same lookup found SciFact's **0.64508**, which is the bare `0.645` 3.7 carried unsourced for two phases; the band stays centred on 0.645 and now has a citation. **All three land above published**, by +0.00085, +0.00219 and +0.00265. Each is an order of magnitude inside ±0.02 and none is a failure, but three out of three in the same direction is a sign rather than noise, and it is recorded as an open observation with the obvious candidates named — tie-breaking at equal scores, or the exact truncation boundary — and **neither checked nor claimed**. **The real run is the first thing that has ever exercised chunk-to-document max-pooling against a corpus**, and its two counters are what make that verifiable rather than asserted: **0 queries** retrieved two units of one document under the parity protocol on either dataset, and **all 1,406** of ArguAna's and **all 1,109** of SciFact's did under Rag.NET's chunking — 82,618 units from 8,674 documents at up to 285 from one, and 56,707 from 5,183 at up to 221. **The two real deltas have opposite signs, which is the most useful thing the phase produced.** Default chunking **costs 0.0784 nDCG@10 on ArguAna** — 0.50432 → 0.42594, with Recall@10 0.79161 → 0.70057 and MRR@10 0.41515 → 0.34147, so documents are missed rather than reordered — and **gains 0.0100 on SciFact**, 0.64593 → 0.65589, with Recall@10 flat at 0.78667 → 0.78222 and MRR@10 up 0.60483 → 0.62057, which is the same documents better ordered. [**SciFact's real leg was measured in the whole-phase review, 2026-07-31**; the phase itself recorded it as "not recorded", and one page argued from its absence that the helping case "is FiQA's, and FiQA's real run has not been measured". It is SciFact's, and it is measured.] [**Both real deltas were re-measured by Phase 3.16 under the packing chunker:** SciFact 0.65589 → 0.67742 (+0.03148 against parity) and ArguAna 0.42594 → 0.47559 (−0.02873). Both improved, both signs held, and ArguAna recovering ~63% of its loss is what confirmed this paragraph's fragmentation explanation rather than falsifying it — the test 3.16's design set up in advance.] As reasoning and not as measurement: the sign tracks whether relevance is passage-level — a claim supported by two sentences inside an abstract — or document-level, as a whole counterargument to a whole argument is. One dataset could not have told those apart. **FiQA's real run was deliberately not made**, with a measured basis rather than an estimate: FiQA's parity leg took 1 h 11 m for 64,247 distinct embeddings, its real leg is 429,850 chunks, and the vector store would sort 429,850 entries per query across 6,648 queries — eight to nine hours. [**Overtaken by Phase 3.16:** packing cuts the leg to 121,236 chunks and the cost to a derived ~1.5–2 h, at the ~27 embeddings/s observed across the two packed real legs.] It is still the run worth having, because FiQA's documents are genuinely long and heterogeneous where ArguAna's 9.5× fan-out comes largely from the chunker's short-part behaviour and SciFact's abstracts are uniform — but [**with SciFact's real leg measured it is no longer the only thing that can answer whether max-pooling helps or hurts**: the answer is both, and it depends on the corpus] → **Phase 3.15**, which needs a cached-embeddings artifact anyway. [**Measured there, 2026-08-02:** nDCG@10 0.35569 against parity 0.37086, delta −0.01517, in 1 h 4 m — under the ~1.5–2 h derivation, not over it.] **Three debts recorded with their numbers.** `RecursiveChunkingStrategy` never merges short split parts back towards `MaxChunkSize`, so a document of short lines becomes one chunk per line: FiQA 429,850 units from 57,638 documents, up to 1,723 from one, against the ~2× a 522-character median document over a 512-character chunk size suggests. That is a probable library defect with nothing to do with benchmarking — it inflates embedding cost, storage and query-time sorting for every user of the default chunker → **Phase 3.16** [closed there, 2026-07-31 — confirmed, and it was three faults rather than one]. And FiQA has 38 corpus entries whose title and text are both empty, one of them (`117276`) judged relevant, so the real leg indexes 38 fewer documents than the parity leg — surfaced as `UnindexedDocumentCount` rather than papered over with a placeholder chunk → **Phase 3.15**, to be stated alongside FiQA's real number [done there — 57,600 of 57,638 indexed, stated with the number; closed]. The third was found while writing this entry rather than while running anything: `nightly.yml` selects the whole integration project with no filter and allows it **120 minutes**, and the cases this phase added are hours — so the nightly would have failed on a timeout, which reports on parity exactly as little as skipping did. [**Closed inside the phase rather than carried to 3.15.** `BeirRunBudget` records what each dataset costs under each protocol and gates the four the job cannot afford behind `RAGNET_BEIR_LONG_RUNS`, which `nightly.yml` never sets; each skips naming its measured cost and the command that runs it. The nightly keeps the SciFact and ArguAna parity legs and gives up corpus-scale max-pooling, which is stated rather than buried.] **Self-exclusion is carried per dataset**, because it is part of the published figure rather than a preference: MTEB's `ignore_identical_ids` and BEIR's `if corpus_id != query_id`, set for ArguAna and FiQA and off for SciFact. ArguAna is unrunnable without it — 1,298 of its 1,406 queries are byte-identical to the corpus document sharing their id — and SciFact's ids do not intersect at all, so 0.64593 is untouched. **Licences are not uniform and all three disagree with their mirrors.** ArguAna is CC BY 4.0 from the Zenodo deposit that replaced BEIR's dead homepage link, against `cc-by-sa-4.0` from both mirrors. FiQA names **no** licence and restricts to non-commercial use twice in upstream's own words, while `BeIR/fiqa` declares `cc-by-sa-4.0` — permitting precisely the commercial use upstream refuses — and `mteb/fiqa` declares `unknown`. The meta-finding is that `BeIR/scifact`, `BeIR/fiqa` and `BeIR/arguana` all declare the same `cc-by-sa-4.0`: a blanket mirror-wide declaration rather than a per-dataset determination, which is why it disagrees with all three upstreams at once. Upstream is authoritative throughout; nothing is redistributed. **The roadmap entry that scheduled this phase was wrong about why**, corrected inline above and in `docs/reference/retrieval-quality.md` rather than silently: max-pooling was a no-op on SciFact because of the *parity protocol*, not because abstracts are short — 99.2% of them exceed the chunk size against FiQA's 51.0% — and no parity band will ever guard the aggregation order, on any dataset. **One inaccuracy was knowingly left in place and is now gone**: `BeirDatasetDescriptor.FiQA`'s remarks still said 51% of its documents exceeding `MaxChunkSize` "is what makes this the first dataset where chunk-to-document max-pooling is not a no-op", the same wrong reason surviving into a comment. [**Corrected in the whole-phase review**, along with a fourth copy of the same false premise nobody had listed — `DocumentRanking`'s own summary still said SciFact abstracts and ArguAna arguments "are mostly single-chunk".] **The review also closed the gap the phase's own numbers were pinned by**: nothing asserted 0.64593, 0.37086 or 0.50432 anywhere, and the ±0.02 published band plus the real run's 0.5×–1.5× envelope both pass a cut-then-pool mutation that moves those numbers by 0.016–0.020. `BeirReproduction` pins the measured figures at ±0.005, labelled as this machine's reproduction rather than as agreement with anyone's publication, and `BeirDatasetDescriptorTests` now pins FiQA's and ArguAna's targets, which were pinned by nothing at all. Supporting work: the parity test is a theory over `BeirDatasetDescriptor.All` with each dataset carrying its own target and band, so a dataset is a descriptor rather than a copied test file; `EmbeddingCache` is content-addressed on the model identity **and** the text, treats a truncated entry as a miss, and is what makes measuring each dataset twice affordable; and `Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments` needs no model and finishes in seconds, which is how the chunk counts here were measured rather than guessed.)

### Phase 3.13: Late Chunking Newline Defect [status: complete]
**Goal:** Make late chunking work on text that has paragraphs. (Not a features.md row — a defect found by the Phase 3.7 whole-phase review and recorded in the follow-up-debts list at the top of this file.)

Created when that review provisioned the ONNX model `nightly.yml` had been claiming to supply, which ran `LateChunkingIntegrationTests` for the first time since it was written and turned it red. `OnnxTokenEmbeddingGenerator` refuses any input whose tokenizer normalization changes the text length, BertTokenizer's normalizer deletes `\n` and `\t`, and `LateChunkingStrategy` swallows the resulting failure into text-only chunks with `Embedding = null`. The feature is inert for any document containing a line break, which is all of them.

**Scope:**
- **Decide where the fix belongs.** Position-preserving pre-normalization in the generator — mapping `\n` and `\t` to a space keeps the length, and consecutive spaces already survive — is the cheap option; a real offset map through the normalizer is the thorough one. The guard itself is correct and stays: it is the only reason this was diagnosable at all rather than a silent quality regression.
- **A fixture that would have caught it.** The current one contains `"\n\n"` and was written before the guard existed. Whatever replaces it must fail against the unfixed generator.
- **Decide whether the strategy's silent fallback is right.** Falling back to unembedded chunks on a *contract* violation is indistinguishable from working, and that is what hid this for two phases. A generator rejecting its input is not a transient failure.
- **Ask the same question of `OnnxEmbeddingGenerator`.** It pools internally and exposes no offsets, so it has no equivalent guard and embedded the whole SciFact corpus without complaint. Worth confirming rather than assuming that it is unaffected.

**Not in scope:** the tokenizer. Microsoft.ML.Tokenizers' BERT normalization is upstream behaviour and matching it is the point.

**Completed:** 2026-07-30 (late chunking works on multi-line, tab-separated, NFC text of any script but CJK, and `LateChunkingIntegrationTests` — written in Phase 1.1 and **never once executed anywhere** — now passes against a real `all-MiniLM-L6-v2`, with a tab case added to it. The fix is a length-preserving substitution of a space for `\n`, `\t` and `\r` in `BertOnnxPlumbing` before every `EncodeToTokens` call. **The defect was five times broader than the debt entry said**, which the design established by probing rather than reasoning: not only paragraph breaks but `\t`, `\r`, a trailing newline, any other control character, NFD-decomposed text (`"cafe" + U+0301 + " test"`, 10 → 9, the form macOS filesystems produce) and **all CJK** (`"日本語 text"`) — which *grows*, 8 → 14, and so cannot be fixed by any substitution at all. **It corrupted tokens, not only offsets.** `"alpha\n\nbeta gamma"` normalized to `"alphabeta gamma"` and tokenized as `alphabet | ##a | gamma`: BERT's reference implementation treats `\n` as whitespace and substitutes a space, this tokenizer deleted it as a control character, and the words either side merged into one the document never contained. A fix restoring only the offsets would still have embedded `alphabet`, which is why the substitution went into the shared plumbing rather than the late-chunking path. **`OnnxSpladeEncoder` and `OnnxEmbeddingGenerator` shared the defect and never tripped the guard**, because they discard offsets — the guard only ever protected the one encoder that read them, while the other two embedded the merged word in silence. That is not hypothetical: it is where Phase 3.7's `title + "\n" + text` measurement got its 0.00314 from, and correcting it is recorded above. **Severity was overstated in the debt entry.** `EmbeddingBehavior` backfills every chunk whose embedding is null or empty, so the fallback degraded to *ordinary* embeddings rather than losing chunks — nothing was ever unretrievable, and what actually happened is that a configured feature silently did not apply. The fallback is therefore kept, per the design: one awkward section should not fail a document. **The guard stays and gets tests.** Probing showed CJK token offsets going genuinely out of bounds, so refusing is correct rather than cautious; what changed is the message, which now names the direction the length moved and the cause that direction implies — grew means CJK and there is no remedy, shrank means NFD (fixable with `string.Normalize()`) or a rarer control character. **The plan's claim that the guard had "no test coverage at all" was wrong**: `GenerateAsync_NormalizationChangesTextLength_ThrowsClearError` reached it through `GenerateAsync` with a `U+0001` and pinned the old wording, so it failed on the message change. What was genuinely missing was a direct test of the guard and any pin on the *cause*, both added in `NormalizationGuardTests`. **And the plan's premise that control characters are "now substituted" is only true of `\n`, `\t` and `\r`** — the rarer ones are still deleted and still a live cause, so the message qualifies that advice instead of dropping it. Verified with the model provisioned rather than skipped: `Rag.NET.Embeddings.Onnx.Tests` 147 passed / 0 skipped, `Rag.NET.Chunking.IntegrationTests` 4 passed / 0 skipped, and both the guard's cause-naming and the new tab case were mutation-checked — removing "CJK" from the message fails two assertions, and neutralising the substitution fails both late-chunking tests with every chunk's `Embedding` null, which is the fallback the design predicts rather than a thrown error. SciFact parity is unmoved: **0.64593** measured under both separators when the substitution landed, and `Rag.NET.Benchmarks.Quality.IntegrationTests` re-run green afterwards — 2 passed / 0 skipped in ~7 minutes, against a band the run reports the number for only on failure.)

### Phase 3.14: Library Comparison at Defaults [status: complete]
**Goal:** Compare Rag.NET's retrieval quality against other RAG libraries on the same corpus and the same embedding model, **each at its own defaults**. (Not a features.md row — scoped out of 3.7 and framed in the 3.12 design.)
**Plan:** `docs/plans/2026-08-02-library-comparison-design.md` + `-implementation.md`
**Docs:** `docs/reference/library-comparison.md` (results) + `docs/reference/library-comparison-defaults.md` (every entrant's defaults, read from source at pinned versions **before** any entrant was written)

Created by the 3.12 design, which decided the framing that 3.7 left open. 3.7 declined comparative tables because they are "only credible with genuinely equivalent configuration"; the 3.12 design went further and rejected *matched* configuration as the wrong target:

- **A matched-configuration table measures how carefully each library was configured**, not the libraries. Match the model, the chunk size and the top-k across four libraries and they converge on near-identical numbers, because at that point they are all calling the same embedding model through different syntax. The differences that survive are rounding.
- **The credible comparison is each library's defaults** — same corpus, same model, every configuration published in full. That measures the decisions a library makes on your behalf when you do not make them yourself, which is a real difference and the one a reader is choosing between.
- It is also the harder table to write honestly, because "our defaults win" is exactly what every such table concludes. Whatever ships must publish the configuration of every entrant, and a default that loses is a finding rather than a bug to be tuned away.

**Depends on** the 3.12 harness: the parity protocol, the descriptors and `EmbeddingCache` are what make running one corpus through several libraries affordable.

**Not in scope:** changing any Rag.NET default in response to the table within the same phase. Measure first; a defaults change is its own decision with its own phase.

**Completed:** 2026-08-02 (**five entrants, two corpora, one matched embedder, everything else at each library's own defaults — and the headline is that the defaults barely matter on these corpora.** The table, nDCG@10, SciFact / ArguAna: Rag.NET control **0.64593** / **0.50432**, Semantic Kernel 1.78.0 0.64593 / 0.50306, LangChain core 1.5.3 **0.64613** / **0.50450**, LlamaIndex core 0.14.23 0.64508 / **0.50450**, Haystack 3.0.0 0.62757 / 0.49715 — **LangChain highest on SciFact, LangChain and LlamaIndex tied highest on ArguAna, published plainly.** Every row crosses the same **TREC run-file boundary** (`87aa5d1`) and is scored by the one `IrMetrics` behind the published figures — no entrant's code computes a metric — and **the control row reproduced the published parity figures EXACTLY through that boundary**, which is what makes the other rows readable; it is stated before the table, not after. Everything except Haystack sits within thousandths (spreads 0.00105 / 0.00144), an order of magnitude inside the between-protocol deltas 3.12–3.16 measured on the same corpora (+0.031 / −0.029 / −0.015), so the four non-Haystack rows are published as **not separable** rather than ranked. The mechanism is that most default chunk sizes exceed these documents: LangChain (4000 characters) and LlamaIndex (1024 cl100k tokens) produced at most **3 and 2 units per document**; **Haystack is the only entrant that chunks hard at its defaults (200 words, 0 overlap → 8,042 / 11,342 units) and the only one measurably lower** (−0.018 / −0.007 against the control). **Semantic Kernel has no default chunker at all** — no ingestion pipeline; `TextChunker` is `[Experimental]` and size-less; no default top-k at the vector-store API; the InMemory connector preview-only — so **its row *is* the parity protocol by construction**, which is why it equals the control exactly on SciFact and differs on ArguAna only in tie- and near-tie ordering (Recall@10 identical at 0.79161, nDCG −0.00126). **Kernel Memory was dropped, and the drop is the finding**: its packages are marked legacy on NuGet, its README calls it "an archived research project", `0.98.250508.3` is final — recorded **with no number attached**, because a number against a project its own authors archived invites the fair objection that the table picked something that could not answer back; the Task 3 reading that its own validation refuses its 1000-token default against a 256-token embedder stays recorded on the defaults page. **LlamaIndex's default embedder (`OpenAIEmbedding()` → `text-embedding-ada-002`) validates an API key at resolution time, so it will not run offline at its true defaults** — and all three Python libraries default to ada-002, so the pinned local embedder is the same forced substitution every entrant got, each library's would-have-been embedder published beside its row. **The identity check found a real tokenizer divergence before any entrant ran, and it is a finding in its own right:** HF `tokenizers`' `BertNormalizer` strips accents by default when lowercasing; `Microsoft.ML.Tokenizers` at default `BertOptions` — the pipeline behind every published figure here — does not (`müllerian` → `[UNK]`), and the two pipelines sat **0.166 apart (max-abs)** on accented text from the same model file until the Python side was pinned `strip_accents=False`, after which all six battery strings were **bitwise identical, max |diff| = 0.0**. Given a section of its own on the results page rather than a footnote, because anyone comparing this repository's BEIR figures against Python-stack numbers needs it. **FiQA is unrun for every entrant** — recorded NEVER RUN in `BeirReproduction` and `BeirRunBudget` at a derived ~1 h per entrant (the corpus embedding dominates), an empty entry being a different state from an absent one. Reproducibility: every version pinned; the Python harness committed with its `uv.lock` (CPython 3.14.5, `fe11cfb`); run files carry per-line tags naming the library and version, with self-exclusion and chunk-to-document max-pooling applied writer-side so **an outsider's `trec_eval` scores what `IrMetrics` scores**; every figure pinned in `BeirReproduction` at ±0.005. `retrieval-quality.md`'s two "→ Phase 3.14" arrows now point at the published page — and its prediction that matched-configuration tables "converge on near-identical numbers" turned out to hold one framing further out: on corpora whose documents fit inside the default chunk sizes, the *defaults* converge too, which is the table's most honest finding and its stated limit.)

### Phase 3.15: Retrieval Ablation Table [status: complete]
**Goal:** Publish the ablation table — baseline dense → +BM25 hybrid → +HyDE → +reranker — over the datasets 3.12 landed. (Not a features.md row — §4–§5 of the 3.12 design, split out before that plan was written.)

Created by the 3.12 scope split. §4 and §5 of `docs/plans/2026-07-31-beir-expansion-ablation-design.md` are kept in that document rather than moved, because the reasoning about what each row *is* was the expensive part to work out and this phase should start from it rather than rediscover it.

**The rows are not uniform, and each is labelled for what it is:**
- **dense** — free, deterministic, validated against a published figure. The anchor.
- **+BM25 hybrid** — free, deterministic, and **incomparable to any published BM25**. `IHybridSearchable` is implemented only by the Azure AI Search and Weaviate stores, so in-memory this row is `InMemoryBm25Index` combined with dense results via RRF. The comparability debt is in the follow-up-debts list with its numbers; the decision it demands is due **before** the row is published, not after. [**Decided and closed in this phase:** the row is published as a Rag.NET-internal comparison with no published reference; the debt has moved to the Closed list.]
- **+HyDE** — needs an `IChatClient`, and is the only nondeterministic row. The generated hypotheticals must be **cached alongside the embeddings**, or a re-run produces different hypotheticals and the table is noise with a border around it.
- **+reranker** — needs a cross-encoder. `OnnxReranker` rather than `CohereReranker`: local, free, deterministic, provisioned the way the embedder already is, and no API key or per-call cost in a table meant to be re-runnable. [What nobody knew when this was written: `OnnxReranker`'s tokenizer was not WordPiece, and the first row it produced measured that defect rather than the model — see the Completed paragraph.]

**What the table must be able to show:** lift where lift is expected (HyDE on FiQA), and **no lift where none is expected** (HyDE on ArguAna). A table that only ever goes up is indistinguishable from a table that cannot go down, which is why ArguAna is the negative control and the most valuable single dataset here. [**Measured: the table can go down — but the lift landed where nobody predicted it.** ArguAna held (−0.0014); FiQA, the named positive control, was flat (−0.0054); SciFact took the lift (+0.0541). Two of the design's three predictions failed and are recorded as failed — see the Completed paragraph.]

**Also carried into this phase, from 3.12:**
- **FiQA's real-chunking run**, deferred out of 3.12 with a measured cost basis that 3.16 has since re-based: the leg was 429,850 chunks and an estimated eight to nine hours; packing cuts it to **121,236 chunks and a derived ~1.5–2 h** — 121,236 chunk embeddings plus 6,648 query embeddings at the ~27 embeddings/s observed across the two packed real legs. **Derived, not measured**: nobody has run it, and the first run is the measurement. It adds a **third corpus shape** — documents long and heterogeneous in their own right, where ArguAna's fan-out was mostly the chunker's short-part behaviour (9.5× before packing, 2.8× after — 3.16 confirmed that attribution) and SciFact's abstracts are uniform — rather than the only evidence about whether max-pooling helps or hurts, which SciFact (**+0.03148**) and ArguAna (**−0.02873**), both re-measured under packing in 3.16, already answer in both directions. **This phase needs a cached-embeddings artifact regardless**, which is what makes it the natural home. [**Run in this phase:** 0.35569 against parity 0.37086, in 1 h 4 m — the derivation overshot; see the Completed paragraph.]
- **The 38 empty FiQA corpus entries**, one of them judged relevant, which make the real leg index 38 fewer documents than the parity leg. State it alongside FiQA's real number. [**Done** — 57,600 of 57,638 indexed, stated with the number; the debt has moved to the Closed list.]
- ~~**A one-line correction to `BeirDatasetDescriptor.FiQA`'s remarks**~~ — **done in the 3.12 whole-phase review**, not carried here. The remark credited FiQA's 51% of over-long documents with making it "the first dataset where chunk-to-document max-pooling is not a no-op"; the protocol makes it a no-op, not the document length, and SciFact exceeds the chunk size more often (99.2%). The same false premise was also still in `DocumentRanking`'s own summary ("SciFact abstracts and ArguAna arguments are mostly single-chunk") and was corrected with it.
- **TREC-COVID** — the first graded-relevance dataset. `IrMetrics` uses `2^rel - 1` and has a graded fixture, but no graded dataset has ever been through it. [**Deferred again** — re-recorded in the follow-up-debts list → Milestone 4.]
- **EnronQA**, for the private-corpus and multi-tenant story. [**Deferred again**, with TREC-COVID → Milestone 4.]
- ~~**What the nightly runs.**~~ **Settled in 3.12 rather than carried here.** `BeirRunBudget` records what every dataset costs under every protocol and gates the four cases the job cannot afford behind `RAGNET_BEIR_LONG_RUNS`; the SciFact and ArguAna parity legs still run unasked, so the job reports a parity number rather than a timeout. What remains for this phase is narrower and is the *reason* for the artifact: with cached embeddings, FiQA and the real legs could come back into a 120-minute job instead of staying opt-in. [**Not taken:** the nine ablation cells joined `BeirRunBudget` as gated cases instead, and what re-checks every figure on a push is `BeirReproduction`'s fast-tier pin — see the Completed paragraph.]

**The runs-after-3.16 condition is satisfied** — 3.16 ran and completed 2026-07-31 — so the chunk counts this phase budgets against are the packed ones above, not the ones the short-part defect produced.

**Completed:** 2026-08-02 (**the table, all nine cells measured** — parity protocol, judged queries only, each cell against its dataset's dense anchor: SciFact 0.64593 → +BM25 hybrid **0.69913** (+0.0532) → +HyDE **0.70001** (+0.0541) → +reranker **0.68442** (+0.0385); FiQA 0.37086 → **0.35665** (−0.0142) → **0.36543** (−0.0054) → **0.38458** (+0.0137); ArguAna 0.50432 → **0.51173** (+0.0074) → **0.50293** (−0.0014) → **0.47917** (−0.0252). **Every technique helps somewhere and hurts somewhere.** No row is free lift, which is what makes the table credible rather than promotional — the design's demand that it be able to go down is met on every row, not only the one built for it. **The design committed to per-dataset HyDE predictions before anything was built, and two of the three failed — recorded as failed rather than reframed.** FiQA, the positive control ("clear lift"), was flat: −0.0054. ArguAna, the negative control ("no lift, plausibly negative"), held: −0.0014. SciFact ("modest lift, smaller than FiQA's") gained the most of the three: +0.0541. The design named "FiQA shows no lift" as the outcome that would make the table uninterpretable, because a weak model and an unhelpful method are indistinguishable in a run that is flat everywhere — **that escape hatch did not apply**: SciFact gained +0.0541 from the same model, the same prompt and the same cache, so FiQA's flat cell is a measurement, not an artefact. The explanation that survives — HyDE helps when the hypothetical sits closer to the corpus register than the query does — is recorded **as post-hoc**, because it is one. ArguAna's negative control has an **observed mechanism**, recorded during generation independently of the measurement: its hypotheticals are compressed restatements of the input argument, recycling its own statistics, and ArguAna asks for the best *counter*argument — so HyDE moves the search vector toward the query's own position and away from the target. **Two library defects were found and fixed, and neither is what the phase set out to measure.** First: `OnnxReranker.TokenizePair` was not a WordPiece tokenizer (`a912187`). It whitespace-split and looked up whole lowercased words, mapping every miss to `[UNK]` — measured over both corpora in full, **26.59% of SciFact's 1,112,417 words and 17.62% of FiQA's 7,660,017 reached the model as `[UNK]`**; through WordPiece, 0.01% and 0.10%. The first reranker measurement showed harm everywhere — SciFact 0.56693, FiQA 0.34085, ArguAna 0.41806 — and after the fix the row **gains 0.117 on SciFact, 0.061 on ArguAna and 0.044 on FiQA from tokenization alone**. It was found because the row hurt on FiQA too, the MS MARCO-like corpus where the design predicted a cross-encoder helps, and uniform harm across in-domain and out-of-domain corpora is more consistent with a defect than with a technique. **No guard could have caught it**: `AssertRerankerReordered` proves the cross-encoder *moved* the ranking, and garbage-but-varying scores reorder every query. The new guard is an offline tokenizer round-trip test that fails on the old algorithm. The fix also corrected hardcoded `[UNK]`/`[CLS]`/`[SEP]` ids, a truncation rule that starved long queries, and a `MaxLength ≤ 3` case that exceeded its own ceiling; the shared plumbing lives in `src/Shared/BertWordPieceTokenization.cs`, linked into both ONNX packages. Second: **the harness retrieved unjudged queries** (`339f3d6`). `MeasureAsync` retrieved for every query while `IrMetrics` scores only judged ones — SciFact retrieved 1,109 to score 300, FiQA 6,648 to score 648 — waste everywhere, and it **broke the HyDE row**, whose refuse-on-miss cache failed on the first unjudged query. ArguAna concealed it: all 1,406 of its queries are judged. Metrics unchanged by construction and verified — parity reproduced 0.64593 and 0.50432 exactly — and every recorded query counter was restated across nine files. **FiQA's real leg, deferred out of 3.12 and re-based by 3.16, is measured at last: nDCG@10 0.35569 against parity 0.37086, delta −0.01517** — 121,236 units over **57,600 of 57,638** documents, the 38 empty entries (one judged relevant) contributing nothing, stated here because 3.12's debt required it stated with the number; all 648 judged queries pooled; **1 h 4 m against the derived ~1.5–2 h — the estimate overshot, and that is recorded rather than quietly replaced.** The three real deltas now exist — SciFact **+0.03148**, ArguAna **−0.02873**, FiQA **−0.01517** — and they support the explanation 3.12 proposed and 3.16 tested, that the sign tracks whether relevance is passage-level or document-level: recorded as **consistent with three corpora, not as newly proven**. **The HyDE row is reproducible by construction:** 7,062 hypotheticals for the 2,354 judged queries at `HypothesisCount = 3`, `openai/gpt-4o-mini` at `HydeOptions.HypothesisTemperature` (0.8), **$0.66**, zero failures. The cache identity is `openai/gpt-4o-mini@t0.8` — the temperature is in the key, added after a review found that sampling settings outside the key would silently serve text drawn from another distribution. The table run never calls an LLM; a cache miss fails naming the key. **The cache is never committed** — it derives from BEIR queries, and this project's standing position is that nothing is redistributed. All nine ablation figures and FiQA's real leg are pinned in `BeirReproduction` at ±0.005 (`899f4b2`), with a fast-tier theory so a mutated figure fails on every push rather than only under an opted-in run. **The BM25 comparability debt is closed by labelling**: the `+BM25 hybrid` row is published as a Rag.NET-internal comparison with no published reference, and 3.7 §2's rejection of a benchmark-only analyzer stands — moved to the Closed list, with the FiQA empty-corpus debt the real number now states. **Three debts recorded in the follow-up list, each with its origin:** the reranker row permutes only the ten documents it is evaluated on — `TopK` equals the cutoff, so Recall@10 is frozen by construction, visible in SciFact's reranker Recall@10 of 0.78667, identical to dense; **a design flaw in this phase's own plan, not a defect in the code**, and the row understates what a cross-encoder can do → the next re-measure of the table, backstopped by Milestone 4 [backstop re-based to Milestone 6, 2026-08-03, at the v1.0 postponement — the deadline's basis is the v1.0 docs, which ship with the tag, and the tag moved there; see the follow-up-debts entry]; `docs/reference/ci.md` still counts "eleven cases" and does not list the nine ablation cells now gated in `BeirRunBudget` → Milestone 4, with 4.1; and TREC-COVID and EnronQA, deferred again unchanged from 3.12 — the `2^rel − 1` path has still never seen a graded *dataset* → Milestone 4, with the release-readiness work. [**Re-pointed 2026-08-02 by the Milestone 4 replan, design §5: TREC-COVID and EnronQA stay in Milestone 3's scope** — run or explicitly declined before this milestone closes, not smuggled into 4; the FiQA-qrels check recorded on that debt still comes first. See the follow-up-debts list.])

### Phase 3.16: Recursive Chunking Short-Part Merge [status: complete]
**Goal:** Stop `RecursiveChunkingStrategy` emitting every split part as its own chunk, so a document of short lines does not become one chunk per line. (Not a features.md row — a probable library defect measured in Phase 3.12 and recorded in the follow-up-debts list at the top of this file, now moved to that list's Closed section.)
**Plan:** `docs/plans/2026-07-31-recursive-chunking-short-part-merge-design.md` + `-implementation.md`

Measured at stock `ChunkingOptions` — 512 characters, 50 of overlap: **FiQA 429,850 units from 57,638 documents** (7.5×, up to **1,723** from a single document), ArguAna 82,618 from 8,674 (9.5×), SciFact 56,707 from 5,183 (10.9×). FiQA's median document is 522 characters against a 512-character chunk size, which suggests roughly 2×.

**This is a library problem, not a benchmark one.** Every user of the default chunker pays it in embedding calls, vector-store rows and query-time sorting, and the multiplier is largest on the corpora people have most of. It was found only because 3.12 was costing an embedding run and the arithmetic did not work.

**Scope:**
- **Decide what the fix is.** A merge pass over the emitted parts, a minimum chunk size, or a split-and-pack loop that fills towards `MaxChunkSize` are three different answers with three different effects on chunk boundaries. Not decided here.
- **Overlap interacts with all three** and must be reasoned about explicitly rather than left to fall out.
- **Every downstream number in the project moves**, including the real-chunking runs in `docs/reference/retrieval-quality.md`. Whatever ships re-measures them rather than leaving the page describing the old chunker.
- **Confirm it is a defect before fixing it.** The counts are measured; the intent behind the current behaviour is not, and a strategy that deliberately preserves split boundaries is a different conversation from one that forgot to pack them.

**Not in scope:** the other chunking strategies, unless the same shape is found in them — in which case say so rather than widening quietly.

**Completed:** 2026-07-31 (**confirmed a defect — the precondition this entry set — and it was three faults rather than one.** First, the size limit was not consulted before splitting: `SplitRecursively` checked whether text fit within `MaxChunkSize` only on the branch where the current separator was absent, so a 35-character section became 2 chunks against a 512-character limit. Second, split parts were never packed back: every part that fit was emitted as its own chunk, and with no sentence separator present the recursion reached the `" "` separator and emitted **one chunk per word** — 150 words became 150 chunks of 4 characters, which is what settled the "is it deliberate?" question, because nobody deliberately makes word boundaries chunk boundaries. Third, `Split(". ")` destroyed sentence punctuation and nothing put it back. Also fixed: chunk positions had a silent fallback that reported a wrong position as a real one — now an exception, justified by 500 generated-input iterations proving it unreachable. **The existing tests asserted the defect and the docs drew it.** `ChunkAsync_SplitsByParagraphsFirst` asserted 2 chunks for a 35-character input and passed; the chunking guide's flowchart drew "fits in MaxChunkSize? → yes → emit chunk" with no merge step. Code, tests and docs agreed with each other and all three were wrong — the sixth instance of that shape in this milestone. **Chunk counts, re-measured at the same stock options:** SciFact 56,707 → **20,155** units from 5,183 documents (10.9× → **3.9×**, worst single document 221 → 25); FiQA 429,850 → **121,236** from 57,638 (7.5× → **2.1×**, worst 1,723 → 41); ArguAna 82,618 → **24,003** from 8,674 (9.5× → **2.8×**, worst 285 → 16). FiQA's 522-character median against a 512-character chunk size suggested ~2× and produced 7.5×; it now produces **2.1×** — the discrepancy that opened the investigation is closed. **Parity runs unmoved, which was the phase's regression gate:** SciFact 0.64593 and ArguAna 0.50432, both separators, identical to Phase 3.12 to five decimal places. FiQA's parity 0.37086 was not re-run: it is gated, and the parity protocol indexes one chunk per document and never calls the split path. **Both real runs improved in absolute terms:** SciFact 0.65589 → **0.67742** (delta against parity +0.00995 → **+0.03148**; Recall@10 0.81322, MRR@10 0.63757, all 1,109 queries pooled) and ArguAna 0.42594 → **0.47559** (delta −0.07839 → **−0.02873**; Recall@10 0.77240, MRR@10 0.38435, all 1,406 queries pooled). **The design made a falsifiable prediction and it held.** §6 said: if 3.12's explanation was right that ArguAna's −0.0784 came from fragmenting whole counterarguments, packing should shrink the loss substantially — and said explicitly that if ArguAna did *not* improve, 3.12's recorded explanation was wrong and the roadmap must be corrected. ArguAna recovered about **63%** of the loss, so the explanation stands. The signs remain opposite, so "where relevance lives" still holds: the residual is what packing cannot touch — whole-argument queries scored against 512-character pieces. **FiQA's real-leg cost is revised from an estimated 8–9 h to a derived ~1.5–2 h** — 121,236 chunk plus 6,648 query embeddings at the ~27 embeddings/s observed across the two packed real legs — still Phase 3.15's run, not this one's. [**Measured there, 2026-08-02: 1 h 4 m** — the derivation overshot, and 3.15 records that rather than replacing it.] **The audit of the other strategies found the inverse defect**, and per this entry's own not-in-scope rule it is said rather than quietly widened into: `HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize` at all, and `BookChunkingStrategy`, `LegalChunkingStrategy` and `AcademicPaperChunkingStrategy` all delegate to it, so a user setting `MaxChunkSize` on any of those templates gets no effect from it — recorded in the follow-up-debts list → Milestone 4, with 4.1. Two more debts recorded with it: `docs/reference/benchmarks.md`'s Recursive rows predate packing → re-measured immediately after this phase closed, `cfea8e9` — packing made Recursive faster at every size, allocation down at 500 characters and up at 50 KB (closed; full numbers in the Closed list), and a failure in `Rag.NET.Benchmarks.Quality.Tests` — seen once in this phase, 86 clean runs, then **seen a second time during the whole-phase review and again unnamed**, because the run logged summary-only; still not diagnosed, and the open entry's `--logger trx` instruction stands vindicated. **The whole-phase review also found and closed a test gap:** every chunk was proven a substring of the source, but nothing proved the converse — a mutation deleting `SplitParts`' mid-stream flush silently discarded every run of short parts preceding an oversize sibling and all 1,340 core plus 110 quality tests stayed green. `9682967` adds a coverage property — every character not covered by a chunk span at `Overlap = 0` must be whitespace or a `'.'` on a pack boundary — plus a deterministic case, both failing under the mutation; the suite is now **1,342**. The shipped code never dropped anything — a missing test, not a shipped bug.)

## Milestone 4: Release Readiness [status: complete — audited and archived 2026-08-11, docs/plans/2026-08-11-milestone-4-audit.md]
**Goal:** Make Rag.NET shippable — CI, NuGet publishing, first-class configuration, logging, telemetry, and runnable samples — and prove that what ships works, which the first half of this sentence cannot do on its own: a green build has now been watched to coexist with four live defects.
**Started:** 2026-08-02

> **Replanned 2026-08-02** (`docs/plans/2026-08-02-milestone-4-replan-design.md`, motivated by the
> Milestone 3 audit of the same date). Verification is this milestone's dominant cost, not a
> footnote to it — Phase 4.0 measured **61 of 71 packages at `VerifiedBy=unit`**, exercised only
> against fakes — and the phase list below will grow a **recorded-responses phase** (design §3)
> covering the ~20 packages that talk to live services; that phase is referenced by design section
> rather than number until it is scheduled [scheduled 2026-08-03 — as **Phase 6.1**, in
> Milestone 6 rather than this phase list; see the retitle note below]. v1.0 covers all 71
> packages and all 53 Done claims
> (54 when this was written; the OTel section was withdrawn from Done at Milestone 3's close,
> 2026-08-03, `81163af`): no preview tier.

> **Retitled 2026-08-03 — v1.0 is postponed until after hardening.** This milestone was
> "Release Readiness (v1.0)" and its DoD ended in the tag. The grounds for moving it are this
> project's own record: too many defects have been found by measuring something against reality
> for the first time — late chunking, the one-chunk-per-word chunker, the reranker's `[UNK]`
> flood, the dataset-cache races, the false `features.md` claims — so the tag belongs after the
> work that finds them, not before it. That work is **Milestone 6: Hardening & v1.0**, the new
> terminal milestone at the bottom of this file, which takes from this DoD the recorded-responses
> phase (design §3, scheduled there as **Phase 6.1**), its recording criterion, and `Release
> tagged v1.0`. Nothing is renumbered — this milestone keeps its number, its phases 4.0–4.6 and
> its remaining gates, and every existing cross-reference stays valid — it becomes the
> shipping-readiness *work* — packaging, options, logging, telemetry, samples, tooling — rather
> than the release itself.

**Definition of Done** (rewritten 2026-08-02 by the replan's §6. The previous DoD — all phases
complete, 0 warnings from a clean restore, non-Docker unit tests passing, CI produces packages,
tag v1.0 — was **already fully satisfied while four defects were live**: late chunking inert since
Phase 1.1, the default chunker emitting one chunk per word, `OnnxReranker` sending 26% of every
document to the model as `[UNK]`, and `features.md` advertising a package that does not exist. Not
one was found by a test. Every criterion below can be false, and something checks it. **Amended
2026-08-03, at the v1.0 postponement:** the recording criterion — "every package talking to a live
service has a scrubbed, dated recording" — and `Release tagged v1.0` moved to **Milestone 6's**
DoD, the recording criterion widened there to recording-or-recorded-reason; completing this
milestone no longer tags anything, and every other criterion is unchanged):
- [x] All planned phases complete (**resynced 2026-08-08, by the phase that closes this box**: 13
  of 13 — 4.0 through 4.12 — are `[status: complete]`, confirmed by re-reading every phase header
  in this file rather than trusting the stale count below. **Phase 4.5** (Sample Applications) was
  the last one pending; its own entry below records what closed it: the doc-site build repair,
  `docs.yml`, `samples/Rag.NET.QuickStart`, and `Rag.NET.Security.AspNetCore` off `VerifiedBy:
  none`. History, kept rather than deleted: 6 of 11 as of 2026-08-05 (4.0, 4.1, 4.7, 4.8, 4.9,
  4.10 — the phase list grew 4.9 and 4.10 that week), then went stale by four more closures — 4.2
  (2026-08-08), 4.3, 4.4, 4.11 and 4.12 — none of which updated this line, exactly the gap this
  resync exists to close)
- [x] Full solution builds 0 warnings / 0 errors from a clean restore (**closed 2026-08-08 at the milestone's close**: every `obj/` and `bin/` directory deleted, `dotnet restore Rag.NET.slnx` from empty — 0 warnings — then `dotnet build -c Release --no-restore`: **0 Warning(s), 0 Error(s)**. Deleting the intermediate output first is the load-bearing part; an incremental build is not a measurement, a lesson this milestone learned twice)
- [x] All test projects passing — **and no test is gated behind a condition nothing satisfies** (`TestGateTests`, Phase 4.0). **The gate half holds as of 2026-08-03** (Phase 4.1): both `KnownUnsatisfiable` ledgers are empty, and every formerly-unsatisfiable gate is satisfiable by a fenced procedure in `docs/reference/ci.md` — `ENABLE_OCR` and `RAGNET_TESSDATA` by the `-p:EnableOcr=true` source-build procedure, **executed green on 2026-08-03** (the gated test's first run anywhere); `RAGNET_DOCINTEL_ENDPOINT`/`_KEY` by the `az` F0 free-tier provisioning procedure — written and satisfiable, deliberately not executed, the live run being Phase 6.1's. The box stays open on the all-projects half, checked at the milestone's close. **Corrected 2026-08-04 (Phase 4.8): the clause that used to end this note — "4.1's own workflow changes have not yet had a genuine Actions run" — is no longer true**; the last DoD criterion below now cites the run that made it false. This box stays open regardless: it needs every project passing on the tree at the milestone's close, and Phase 4.8's own tree has not itself been through Actions yet **Closed 2026-08-08: the all-projects half was measured for the first time by running every tier.** **71 test projects, 3,874 passed, 54 skipped, 0 failed** — ungated 55 projects/3,345 passed; Docker 9/238 (all six vector-store backends against real containers, PgVector alone 61); Secrets 6/283; LLM (`Rag.NET.E2ETests`) 1/8 with **zero skips**, so the hosted-model path genuinely executed rather than falling through. The 54 skips are capability-specific, not inert projects: `Benchmarks.Quality.IntegrationTests` skips 38 while still running 56, and `Parsers.Pdf` skips none of its 51. Each is recorded with its reason and re-checked by `TestGateTests`, which also asserts every recorded-unsatisfiable gate is *still* unsatisfiable, so the ledger cannot rot in either direction.
- [x] **Every `features.md` Done claim names code that exists** (`FeatureClaimTests`, Phase 4.0; **holding as of 2026-08-03**: both false claims were corrected at Milestone 3's close, `81163af` — `KnownFalseClaims` is empty and all 72 package claims across 53 Done sections are verified directly. Failing knowingly from 4.0's sweep until then, with the two claims allow-listed under owners → 4.4 and 4.1; both closed early instead, in the Closed debts list)
- [x] **No package declares `VerifiedBy=none`** (the ledger's release gate, Phase 4.0. **Closed
  2026-08-08 in Phase 4.5**: `Rag.NET.Security.AspNetCore` moved to `VerifiedBy: unit` — the last
  package at `none`, `Rag.NET.Mcp.Tool` having closed 2026-08-08 in Phase 4.6. **Verified rather
  than assumed**: `PackageVerificationTests` re-run at this close — 49 passing, 0 skipped;
  `PackagesAllowedToDeclareNone` is empty; `NoPackageIsVerifiedByNothing` is now a plain passing
  assertion rather than a reported skip, exactly the state its own doc comment says the Definition
  of Done requires. See the debts list above for what each closure found)
- [x] CI pipeline builds, tests, and produces NuGet packages (the build-and-test half has been green since Phase 3.5; the pack half shipped in Phase 4.1 — `pack-validate` packs every package [all 70 at the time; **66** since Phase 4.7's decomposition, 2026-08-04, with `ExpectedPackageCount` moved by stated arithmetic], validates them as a failing test step and pushes them to a local feed twice on every push, `publish-nuget` gated to 6.3. **Ticked 2026-08-04 (Phase 4.8), on the evidence this box asked for rather than the wiring**: PR #18 — Phase 4.1's own branch — ran `ci.yml` for real and gated its own merge on it: `commitlint`, `pack-validate` and both `build-test` legs all green (run **30828032049**, 2026-08-03). Every push to `main` since has run the identical pipeline for real, including the case this repository's own record predicted would eventually happen: the Qdrant `SearchAsync` break went red on a genuine `build-test` run on `main` (**30919869612**, 2026-08-04, no commit involved) and the fix went green on the next one (**30926805555**). The pipeline has now executed, repeatedly, against real pushes — this criterion is about the mechanism, and the mechanism is proven. What it does **not** cover: Phase 4.8's own tree has never itself been through Actions — this branch is unpushed, and the honest gap moves to the DoD's all-projects criterion above, not this one)

**What these guards do not fix** (design §7, stated so the milestone does not claim more than it
does — the recording clause now describes Milestone 6's guard, and holds there unchanged): a
recording proves one exchange happened once, not that the API still behaves that way; the
ledger proves a package was exercised, not exercised *well* — `VerifiedBy=unit` on a package with
one trivial test satisfies its letter; the agreement test checks that named code exists, not that
it does what the row says; and **none of them would have caught the reranker tokenizer** — that was
found by a prediction stated in advance and reported honestly when it failed, which is Milestone
3's transferable practice and is not automatable.

### Phase 4.0: Verification Ledger and Claim Agreement [status: complete]
**Goal:** Open the milestone with a measurement: three mechanical guards that make the new Definition of Done falsifiable — every `features.md` Done claim must name code that exists, no test may be gated behind a condition nothing satisfies, and every package must declare how it has been verified. Builds no features, ships nothing. (Not a features.md row — the replan's opening phase.)
**Plan:** `docs/plans/2026-08-02-milestone-4-replan-design.md` + `2026-08-02-phase-4-0-verification-ledger-implementation.md`
**Completed:** 2026-08-02 (**three guards, all cheap, and the numbers they produced are the phase's output.** **(a) `FeatureClaimTests` (`c235a9b`, `d77036f`) parses `docs/reference/features.md` and checks all 54 sections marked `✅ Done` — 54 of 54, not the ~51 the plan predicted — resolving 73 package claims at a measured false-positive rate of 0 of 73.** The residue the plan expected to need risky identifier-extraction turned out to be structured SaaS-connector tables rather than prose, so none was written. **Two claims are false, and both now live in a `KnownFalseClaims` allow-list with evidence and an owning phase, each held by a staleness test that fails the moment the entry is fixed *or* the claim leaves the docs** — an allow-list nothing re-checks is how a known defect becomes furniture. `Rag.NET.Telemetry` is **genuinely false** — the audit's finding (A), now machine-guarded rather than only recorded: no such package, no `.UseTelemetry()`, no `gen_ai.*` attribute, metric names (`ragnet.retrieve.latency`, `ragnet.answer.tokens`, `ragnet.embed.batch_size`) matching nothing in `src/Rag.NET/Telemetry/RagTelemetry.cs`, where the real instruments are `internal` under different names, and its own matrix row unchecked → Phase 4.4 owns the fix. `Rag.NET.Parsers.CSharp` is a **wrong name, not a ghost**: the feature is real and lives at `src/Rag.NET.Chunking.CSharp` → 4.1, with the packaging pass that reads every package identity anyway. **(b) `TestGateTests` (`c613fe1`) enumerates every gating site — 29 on the phase's final tree: 26 `Assert.SkipWhen`/`SkipUnless` call sites, 2 permanent `[Fact(Skip)]`s and 1 conditional-compilation symbol. (The guard first counted 28 — not its plan's 29, whose 29th was an `Assert.SkipWhen` inside a doc comment — and then guard (c)'s own release gate became the 26th call site, a correction the whole-phase review caught after the count was written down.) It asserts each gate is satisfiable somewhere, reading raw source and never compiled output**, because a compiled-output check is blind to the worst case: an `#if` block that is not compiled reports nothing at all. Prose does not satisfy a gate — only a fenced, runnable command counts — and a `secrets.*` workflow mapping is not accepted as evidence either, because the repository cannot show a secret exists. The distribution: **0 gates satisfiable in `ci.yml`** (by design), **5 only in the nightly**, **1 only locally** (`RAGNET_BEIR_LONG_RUNS`, via the fenced command in `docs/reference/ci.md`), and **4 satisfiable nowhere**: `RAGNET_DOCINTEL_ENDPOINT`/`RAGNET_DOCINTEL_KEY` (secrets never configured anywhere — the Document Intelligence live suite has never run, as its debt entry records), `RAGNET_TESSDATA` (its only reader sits inside an uncompiled block), and `ENABLE_OCR` — which is worse than a test gap: nothing sets `EnableOcr`, and the flag **also compiles the production Tesseract engine out**, so the shipped PDF parser has no real OCR in any default build. Two **permanent** `[Fact(Skip)]`s are now visible with their reasons rather than latent: `PineconeVectorStoreTests` (Pinecone Local rejects sparse-on-dense) and `AzureAISearchVectorStoreTests` (the simulator has no OData filters — the skip the audit found in no planning record). **(c) Every package under `src/` now declares `<VerifiedBy>` (`46b6bd8`, `1b206e4`)** — `unit`, `container`, `recorded`, `live` or `none` — extending the `<RequiresDocker>`/`<RequiresSecrets>` convention `ci.yml` already selects on rather than inventing a parallel one. Two gates, deliberately split: "every package declares a value" hard-fails today; "no package declares `none`" is the **release** gate and does not fail the build, because punishing an honest `none` is how a ledger becomes fiction. **The distribution across 71 packages: `unit` 61, `container` 8, `recorded` 0, `live` 0, `none` 2.** The two `none` are `Rag.NET.Mcp.Tool` (host scaffold, no test references it) and `Rag.NET.Security.AspNetCore` (two types, zero test references). The eight `container` are `Rag.NET` itself, `Rag.NET.Security`, `Rag.NET.Ingestion.AzureServiceBus`, and the PgVector, Qdrant, Chroma, Weaviate and Pinecone stores. Two judgments went against the mechanical answer and are recorded as judgments: **AzureAISearch is `unit`, not `container`, despite having Docker-tier tests** — its container is a community simulator without OData filters and of unconfirmed fidelity — and **`Parsers.Pdf.AzureDocumentIntelligence` is `unit`, not `recorded`**, because its WireMock cassettes were hand-written, never recorded from the live service, and a hand-written cassette verifies the code against *our belief* about the API, the exact shape the reranker defect punished. **The number that should shape the rest of this milestone: 61 of 71 packages have only ever been exercised against fakes** — the state late chunking was in for five phases, now visible in every csproj rather than latent. **The ledger also forced a count correction: there are 71 packages, not 72.** `src/Rag.NET.PgVector` is an empty leftover of the rename to `VectorStores.PgVector` — untracked `bin`/`obj`, no csproj — with a matching ghost at `tests/Rag.NET.PgVector.Tests`; recorded as a debt in the follow-up list, since one of the pair already broke a `dotnet run` in Phase 3.16 by making a project name ambiguous. **One §5 routing did not happen here and is said rather than absorbed:** the design sent the FiQA-qrels check ("one read settles it") to this phase, but the implementation plan scoped 4.0 to the three guards and the read was not performed — it stays with the TREC-COVID debt, first thing for whoever runs that dataset.)

### Phase 4.1: NuGet Packaging & Publishing [status: complete]
**Goal:** NuGet packaging, versioning and publishing on top of a pipeline that already builds and tests.
**Backlog items:** NuGet Publishing Pipeline
**Plan:** `docs/plans/2026-08-03-nuget-packaging-design.md` + `2026-08-03-nuget-packaging-implementation.md`
**Completed:** 2026-08-03 (**everything except the credential and the endpoint now runs on every
push — design §1's rule, kept:** `dotnet pack` for all **70** packages, validation as a failing
test project (`tests/Rag.NET.PackageValidation.Tests` — exactly 70 packages in both directions,
MIT licence agreeing with the repository `LICENSE`, README genuinely inside each package,
description non-empty, non-placeholder and unique, repository URL + SourceLink commit, a
`.snupkg` beside every `.nupkg`, no package empty of both `lib/` and `tools/`), and a genuine
`dotnet nuget push` of every package to a local directory feed, twice; only the nuget.org push
is gated, to Phase 6.3, as `publish-nuget` — dispatch-only on `main` with
`publish_to_nuget=true` plus `NUGET_API_KEY`, recorded to the standard `TestGateTests` holds
every other gate to. `TestGateTests` does **not** cover workflow gates — it scans test gates and
knows nothing of a workflow `if:` — stated rather than assumed away, and extending its scanner
was declined: one workflow gate is not a category. `WorkflowWiringTests` pins the gate's
condition, endpoint, command text and fenced procedure instead.
**The measurement that falsified the plan's own premise, recorded because the ledger exists for
exactly this:** the plan and design both asserted `dotnet pack` emits `NU5xxx` for missing
licence, README and description, so warnings-as-errors makes an incomplete package fail the
build. **Measured before Task 1 changed anything: the SDK enforces no package metadata at all.**
Missing licence, authors, URLs and tags emit *nothing*; a missing README is a codeless advisory
(71 of them, failing nothing); a missing description silently becomes the literal `"Package
Description"` in the shipped nuspec. The only genuine `NU5xxx` on the whole tree was a layout
defect, not metadata — `NU5100` ×8 / `NU5118` ×42 from `Whisper.net.Runtime`'s natives flowing
into the audio package as content. So the validation step is **the only guard there is**, not a
second one, and the plan grew Task 1b mid-phase to say so (`5405c7b`) rather than absorbing it.
The premise's counts were wrong too: 71 csproj under `src/` (one, `Benchmarks.Quality`,
deliberately unpackable), **70 packages produced** — not the 71 the plan's final verification
still says. Three packability defects fixed on the way: the audio natives (`4093bf8`),
`Rag.NET.Mcp.Tool` silently unpackable because `Microsoft.NET.Sdk.Web` defaults
`IsPackable=false` under a `PackAsTool=true` contract (`a74e55e` — packability fixed here, its
first tests stay 4.6's), and `samples/` + `benchmarks/` packing into every solution pack
(`618206b`). The description audit (`20612e8`): 0 duplicates across 71, 1 generic, 3 inaccurate
against the code (QueryTechniques claimed core's self-query, AnswerEngines omitted FLARE,
Chunking omitted late/proposition), tags added to 15 where they carry terms the ID does not.
**The local-feed rehearsal measured three things nobody had claimed** (`57e1814`): the quoted
glob delivers flat, one file per package; duplicates against a directory feed are **silently
overwritten at exit 0** and `--skip-duplicate` is unsupported for that push type — so the second
push proves re-running is harmless, and the real 409-and-skip is part of the 6.3 residual; and a
`.snupkg` push to a directory feed is a **complete silent no-op**, which the workflow asserts as
non-arrival so the day NuGet starts delivering them the step fails and the rehearsal widens.
**Versioning** (`1217791`): GitVersion per the house convention — measured `0.1.0-preview.1495`
on `main`, the produced packages on this branch carrying `0.1.0-nuget-packaging.1` read from the
nuspec, and a `v1.0.0` tag in a throwaway clone deriving a stable `1.0.0` with **no config
change**, so 6.3's mechanism is proven before 6.3 needs it. A trap, measured and recorded in
`GitVersion.yml`: GitVersion 6's `ContinuousDeployment` mode *strips* the prerelease label
(`main` derived a stable 0.1.0 — exactly what Milestone 4 must not produce); `main` uses
`ContinuousDelivery`. `EveryPackageCarriesTheVersionGitVersionDerives` re-derives after every
pack and reads the nuspec, so a dropped `-p:Version` ships no silent 1.0.0. Commitlint was
measured against all **1,506** commits before being allowed to fail anything: stock
config-conventional rejects 184, the tuned rules still reject 70, none newer than 2026-07-29 —
so the job lints only a PR's base-to-head range. All **eight routed debts closed**, none
silently dropped — **five moved whole to the Closed list above** with what was found, and the
other three (the `ENABLE_OCR`/`RAGNET_TESSDATA` OCR half, `.commitlintrc.yml`,
`renovate.json`) **closed by dated bracket-annotation on open-list entries that deliberately
stay open for their other halves**: the Azure `RAGNET_DOCINTEL_*` live run (→ Phase 6.1) and
`docs.yml` (→ Phase 4.5).
**Residuals, named rather than implied:** (1) a local feed is not nuget.org — authentication,
API-key scoping, package-ID availability (none of the 70 IDs is reserved), the service's own
validation, the real 409-skip and `.snupkg` delivery are exercised for real exactly once, at 6.3
(`docs/reference/ci.md` § "What the rehearsal cannot prove"); (2) `release-please.yml` is the
one genuinely unexercisable path — its only observable effects *are* the release — gated
dispatch-only, procedure fenced in ci.md, pinned by `WorkflowWiringTests`; (3) `renovate.json`
is inert until the Renovate app is enabled — a hosted service, not a runnable workflow [**stale by
2026-08-08, Phase 4.5**: the app is enabled and has been opening PRs since 2026-08-05 — see the
closed "Three pieces of house furniture" debt entry above]; (4) on
feature branches the prerelease number does **not** increment per commit — this whole branch
packed as `0.1.0-nuget-packaging.1` — only `main`'s `preview.N` counts up, which is the number
6.3 depends on; (5) the DOCINTEL gates are satisfiable, not exercised (their entry above); and
(6) **none of this phase's workflow changes has had a genuine GitHub Actions run** — the branch
is unpushed, `pack-validate` and `commitlint` are new check names outside the required
branch-protection set (adding them is scheduled, on Phase 6.3's checklist — routed there
2026-08-03), and this repository's record (the post-3.15 nightly failed on its first
real run) says the first PR run is the verification, not the local rehearsal.
**Not done, recorded rather than absorbed:** the XML-documentation blocker in the blockquote
below was not taken up — `GenerateDocumentationFile` is still set nowhere, so the 70 packages
ship without IntelliSense XML and the CS1574 backlog stands unmeasured beyond the two projects
probed in 3.2. No task in the plan scoped it and no commit decided against it; it is a new debt
entry in the follow-up list, not a silent drop.)

> **Narrowed 2026-07-29, and the tooling corrected.** This entry used to read *"GitHub Actions CI
> (build + test) and NuGet packaging/publishing with **MinVer** versioning"*. Two things were wrong
> with it.
>
> **The CI half is Phase 3.5's, and is done.** `ci.yml` builds the solution and runs every test
> project in its tier on each push; `nightly.yml` carries the LLM and env-gated jobs. 4.1 no longer
> owns build-and-test, only what is packed and pushed on top of it. (Two phases quietly both owning
> a deliverable is how one of them ends up skipped — which is what 3.5 found when it started.)
>
> *"Every test project" is 64 of 64, and it was 63 when this paragraph was first written.*
> `Rag.NET.WebSearch.Tavily.Tests` was in no solution file, so the build never produced it and its
> tier's `dotnet test --no-build` exited 0 having run none of its four tests. Two guards now hold
> the sentence up: `tests/Rag.NET.RepoConventions.Tests` asserts every test project is listed in
> `Rag.NET.slnx`, and each tier loop refuses to run — and fails — a project whose test assembly is
> not on disk, whatever the reason it was not built.
>
> **The versioning tool is GitVersion, not MinVer.** The house convention in
> `MarcelRoozekrans/AdoNet.Async` is **GitVersion** (`GitVersion.yml`, a `.config/dotnet-tools.json`
> entry, output parsed with `jq`) plus **release-please** for the release itself. Different tools,
> different configuration. The MinVer entry was written before anyone looked at how these
> repositories are actually set up.
>
> **`pack-push` is a job in the existing `ci.yml`, not a new workflow file.** That is how
> `AdoNet.Async` lays it out — `build-test` and a conditional `pack-push` in one file, the latter
> gated on push-to-main — and matching it keeps the two repositories readable side by side.

> **Known blocker, found in Phase 3.2 (2026-07-28): turning on XML documentation will fail the build.**
> `GenerateDocumentationFile` is set **nowhere** in this repo, so `CS1574` (unresolvable `<see cref>`)
> has never been emitted and broken crefs accumulate invisibly. Packaging normally enables doc
> generation, and with `TreatWarningsAsErrors` every one becomes a build failure.
>
> Measured 2026-07-28 by enabling doc generation on one project at a time: **9 distinct CS1574
> sites in `Rag.NET.Abstractions`** — `IRagDataManager`, `ITagIndex`, `IRagBuilder`,
> `DocumentMetadata` (×2), `CodeChunkingOptions`, `RetrievalOptions` (×2), `TagRetrievalOptions`.
> (Raw build output shows 18; MSBuild reports each twice.) Plus four found and fixed in
> `Rag.NET.Evaluation.Ragas`, introduced by moving properties to a base class — **C# does not bind
> a qualified `cref` to an inherited member**, and nothing in the build could catch it.
>
> **Only those two projects have been measured.** Roughly 35 others have never had their XML
> compiled at all, so treat 9 as a floor rather than an estimate.
>
> Enable `GenerateDocumentationFile` across `src/` early in this phase and clear the backlog, rather
> than discovering it while trying to pack.

### Phase 4.7: Package Decomposition, Consolidation & Per-Package READMEs [status: complete]
**Goal:** Make the `Rag.NET` core package stop shipping the dependencies of features nobody switched on, consolidate three satellite families, give every package its own verified README, and answer "what do I install?" on one page. (Not a features.md row — created 2026-08-04 out of Phase 4.1's own residue: 70 packages a user cannot choose between; numbered after 4.6 because it was created mid-milestone, executed between 4.1 and 4.2.)
**Plan:** `docs/plans/2026-08-04-package-decomposition-design.md` + `2026-08-04-package-decomposition-implementation.md`
**Completed:** 2026-08-04 (**the headline, measured at every step rather than once: core's
transitive closure fell 49 → 28 packages** — 49 → 43 extracting SQLite (`f2518d5`), → 30
extracting resilience (`7a1a661`, thirteen left rather than the predicted fifteen), → 28 on the
caching reference swap (`e46fe26`, two rather than the expected six — the reason is a routed
debt below), re-measured **28** at the phase's close. The `.nupkg` sizes were never the problem
— core packs at ~133 KB against a ~19 KB median on the close's artifacts — the weight was
entirely transitive, which is why the catalogue was the visible symptom and the dependency
closure the actual defect: **31 of the 43 packages a consumer downloaded served features they
had to explicitly switch on.** A Qdrant user who never called `UseSqlitePersistence()` shipped
a SQLite engine with native binaries for every RID.
**The package count went 70 → 66, measured by packing, not by counting directories**
(`fcd3337`): +3 extracted satellites (`Rag.NET.Storage.Sqlite`, `Rag.NET.Resilience`,
`Rag.NET.Caching` — each carrying its builder methods per the existing
`PgVectorBuilderExtensions` convention, namespaces unchanged so no consumer source breaks)
−7 merged (Word/Excel/PowerPoint → `Rag.NET.Parsers.Office`, `a8db630`; the four Graph
connectors → `Rag.NET.DataProviders.Microsoft365`, `a32f860`; TokenAware and Semantic folded
into `Rag.NET.Chunking`, `9ef4048`). Both shapes are mechanically enforced from the shipped
nuspecs, never the csproj: `DependencyClosureTests` walks the produced package graph from core
and fails on any extracted cluster, and pins each merged package to the union of what its
sources declared — **both guards proven red before shipping** (re-adding `Microsoft.Data.Sqlite`
to core and adding a dependency to `Parsers.Office`, each reverted). The default pipeline
composition was pinned **before** anything moved (`DefaultCompositionTests`, `acc6e0c`) and is
byte-identical after all of it. Every test move carries its arithmetic in its commit body
(core 1321 → 1150 across the three extractions; the satellites run exactly the moved tests).
**One deliberate behaviour change, recorded as a behaviour change and not a refactor:**
`UseCostBudgeting()` defaulted to the SQLite-backed ledger; it stays in core and now defaults
to `InMemoryCostLedger`, with `UseSqliteCostLedger()` in `Rag.NET.Storage.Sqlite` restoring
persistence. **Daily and monthly spend limits are now enforced against a ledger that resets on
process restart, where they previously persisted** — a financial consequence, decided by the
repository owner on 2026-08-04. `TryAdd` semantics are unchanged (an earlier-registered
`ICostLedger` still wins), and constructing the in-memory default logs a warning naming
`UseSqliteCostLedger()`, so the owner's choice is never invisible.
**One public-API addition, recorded as a surface change the phase said it would not make:**
`IVectorStoreDecorator` was added to `Rag.NET.Abstractions`. `Rag.NET.Memory`'s
`PersistentConversationMemory` type-checked `ResilientVectorStore` to name the store behind the
decorator in its opaque-scale warning, and referencing the satellite instead would have dragged
the resilience closure into every Memory consumer — measured at the close: **14 packages**
(the 13-package Polly/`Microsoft.Extensions.Resilience` subtree plus the satellite itself).
Additive, and it follows the existing `IScoreScaleAware` probe pattern — but the phase's design
promised no public-surface change, so this is on the record as one.
**Task 10 was stopped, not completed** — its debt entry below carries the detail; the short
form: moving the two Templates document parsers to parser packages cycles, because both
constructor-require Templates option types while Templates registers them by compile-time
type, and every escape route violated a phase constraint. `Rag.NET.Chunking.Templates` still
ships `MimeKit`, `CsvHelper` and `ClosedXML`.
**The tokenizer extraction was cancelled after measurement, not attempted:** core
hard-references `Rag.NET.QueryTechniques`, which pulls `Microsoft.ML.Tokenizers` and
`Data.Cl100kBase` independently, so removing core's own references leaves the closure
identical — proven with `dotnet nuget why` in the Task 1 registration audit
(`docs/plans/2026-08-04-registration-audit.md`, `bc94f8f`), which gated all four extractions
and overrode two of the plan's expectations. **Reopening condition: decoupling core from
`QueryTechniques`; until then the extraction saves nothing.** The same audit turned the caching
extraction into a reference swap — `HybridCache` is defined in
`Microsoft.Extensions.Caching.Abstractions`, not `Caching.Hybrid`, so both cache behaviours
stay in core on the light reference, the `_types` list is untouched, and the ordering risk the
design called central never existed to take.
**Every package now ships its own README, and the repo's first doc-snippet verification guards
them** (`PackageReadmeTests`, `a466a28`, written failing before any README existed): each
README must exist, not be the repo README, name its own package id in its install line, and
every type and builder method in its C# fences must resolve as public against that package's
compiled assembly by reflection. Writing the 66 READMEs against that guard surfaced **five
members `docs/guide/data-providers.md` documents that do not exist in the code** (`17e698d`) —
the READMEs are correct, the guide is a routed debt below. Stated limit, not absorbed:
reflection cannot check semantics — argument lists, receiver types and behaviour claims still
pass — and full snippet compilation is recorded as a possible later strengthening, not
scheduled.
**The chooser** (`docs/guide/choosing-packages.md`, linked from `docs/getting-started.md`, the
root README and the sidebar) states what the audit found was a documentation failure rather
than a packaging one: `Rag.NET` brings `Abstractions` and `QueryTechniques` transitively, every
SaaS connector brings the `DataProviders` base (and with it core), the default chunker
(`RecursiveChunkingStrategy`) is already in core, and the opt-in features each name their own
package. Its worked example is the one that motivated the phase: SharePoint + Qdrant is two
genuine package choices, where the pre-decomposition catalogue had a user reasoning about
seven. Writing it also fixed `getting-started.md`'s two install commands that named the retired
chunking packages; the wider sweep of stale package ids in other pages is routed with the guide
debt below.
**`Rag.NET.Mcp.Tool`'s 19 MB is explained by measurement, and mostly dissolved by this phase:**
the close's pack is **1.87 MB** — 34 entries, all dependency assemblies under `tools/`, no
native binaries — because a `PackAsTool` package ships its entire dependency closure inside the
`.nupkg`, so the 19 MB the design recorded at the phase's start was the pre-decomposition core
closure (SQLite natives for every RID, the resilience tree) made visible in bytes. What
remains owed before 6.3 publishes is only confirming the remaining 1.87 MB shape is intended,
routed with `Rag.NET.Mcp.Tool`'s first tests → Phase 4.6.
**Verification at the close:** full solution builds 0 warnings / 0 errors, `RepoConventions`
33 + 1 by-design skip, `PackageValidation` **20/20** (was 15 — the five new tests are the two
closure guards and the three README guards). **Nothing here has run on GitHub Actions:** the
branch is unpushed, same as 4.1's residual, and the first genuine run remains the
verification.)

### Phase 4.8: Dependency Pinning & Renovate [status: complete]
**Goal:** Pin every dependency version through Central Package Management so the floor each of
the 66 packages publishes is chosen rather than decided by pack timing, and configure Renovate
to propose upgrades as reviewable PRs. (Not a features.md row — created 2026-08-04 out of `main`
going red with no commit pushed to it; numbered after 4.7 because it was created after, executed
last in the milestone's phase list.)
**Plan:** `docs/plans/2026-08-04-dependency-pinning-design.md` + `2026-08-04-dependency-pinning-implementation.md`
**Completed:** 2026-08-04 (**the trigger:** on 2026-08-04 `main` went red with **no commit pushed
to it** (CI run 30919869612). `Qdrant.Client` was referenced `1.*`; it floated to 1.18.1
overnight, and that release marked `QdrantClient.SearchAsync` obsolete — warnings-as-errors
turned an upstream deprecation into a build failure on unrelated code. Fixed separately, in PR
#20 (`18fec71`).
**The defect underneath is worse than one broken build:** a floating `PackageReference` does not
ship as a range. It resolves once, at pack time, and freezes into the published nuspec as a
concrete floor NuGet reads as `>=` — so the dependency contract every published package carries
was being decided by *when `dotnet pack` happened to run*, not by a choice anyone made, and it
becomes permanent the moment 6.3 ships it. **The defect demonstrated itself mid-phase, while
being measured** (`9c144f7`, `5924f9a`): the baseline this phase captured before touching
anything recorded `Qdrant.Client` at **1.19.0** — one minor past the 1.18.1 the design had been
written against hours earlier, the floating reference moving the shipped floor again while the
phase that fixes it was already running.
**The measured scope corrected the design's own premise:** the design estimated "~120 references
across ~66 projects" from `src/` alone. Measured repository-wide: **497 `PackageReference`
entries carrying a `Version` attribute across 131 `.csproj` files**, plus **6 more in
`Directory.Build.props`** (the analyzer references) that even that recount missed until the sweep
ran (`0b0f036`) — independently re-verified here by diff: exactly 497 removed across 131 files,
exactly 6 in `Directory.Build.props`. **100 distinct package+version pairs, 4 packages at more
than one version** — CPM permits exactly one. Two of the four were text-level conflicts that
resolution made moot: `Microsoft.Data.Sqlite` and `Microsoft.Extensions.Logging` already resolved
to `10.0.10` everywhere despite one project spelling it `10.*`, so pinning them moved no shipped
floor and needed no decision. The other two needed one (`81c1233`): `Microsoft.Extensions.DependencyInjection`
pinned at `10.0.10` (three test/sample projects moved off `9.x`, all passed, no escape hatch
needed) and `Microsoft.Extensions.AI.OpenAI` pinned at `10.8.3`. **Zero `VersionOverride` used**
— independently confirmed here by a repository-wide grep — so none of the four conflicts became
debt.
**What shipped** (`0b0f036`, `daafdf3`): `Directory.Packages.props` at the repository root, each
version pinned to what `obj/project.assets.json` actually resolved after a fresh restore, not the
range in any `.csproj` — **100 `PackageVersion` entries** (independently recounted: 99 from the
sweep, +1 `Tesseract`, below), and `CentralPackageTransitivePinningEnabled=false`, deliberately —
turning it on would pin transitive dependencies too and rewrite every shipped nuspec's transitive
set, exactly what this phase exists not to do. `PrivateAssets`/`ExcludeAssets` survived the sweep
verbatim: **78 occurrences measured before and after, byte-identical line-for-line** (independently
diffed here against the branch's own base commit, `18fec71` → `HEAD`; six of the 78 sit in
`Directory.Build.props` and are the ones that keep six analyzer packages out of every consumer's
dependency closure, the other 72 are per-project entries — mostly `Microsoft.NET.Test.Sdk`'s and
the ZeroAlloc source generators' — that the sweep had no business touching and did not).
**The evidence — the phase's actual deliverable, not the build passing:** every produced nuspec's
external dependency lines, diffed against a baseline captured before any edit, came back
**byte-identical — empty — over 156 lines**. Independently re-run here rather than taken on
trust: packed all 66 again (`dotnet pack Rag.NET.slnx -c Release -o artifacts/verify`, no
`-p:Version`, matching the baseline's own capture condition), extracted and diffed — `diff`
exited 0 over the same 156 external lines, and the 76 internal `Rag.NET.*` lines carried one
consistent version (`1.0.0`, the same convention the baseline used), never a mixture. A green
build proves the code compiles against the pinned versions; it says nothing about whether a
published floor moved — this diff is what does, and it held on re-verification.
**The guard found a real defect before it shipped** (`daafdf3`): `Rag.NET.RepoConventions.Tests`
gained `DependencyPinningTests` — three facts: no `PackageReference` carries its own version, or
`VersionOverride`, or a `Version` child element; every `PackageReference` has a matching
`PackageVersion`; no `PackageVersion` floats. The second assertion failed on its first run, before
it was committed: `Tesseract` had **no central pin at all**, because it sits behind
`Condition="'$(EnableOcr)'=='true'"` — a default restore never resolves it, so the version sweep
that read `project.assets.json` never saw it. NU1010 confirmed the gap; the OCR build path was
broken and **no default build would ever have revealed it**. Pinned at 5.2.0; a
`-p:EnableOcr=true` restore and build now succeed. **An honest limit on the third guard, recorded
in its own remarks rather than left overclaiming** (`a3a5f70`): `NoCentralPinFloats` can only
fire if someone sets `CentralPackageFloatingVersionsEnabled=true` — otherwise NuGet's own
**NU1011** rejects a floating `PackageVersion` at restore, before any test runs; the guard's real
coverage is the narrower case where that switch gets flipped off.
**Counts:** `RepoConventions` 33+1 skip → **36+1** (three new facts; independently re-run here:
36 passed, 1 skipped). `PackageValidation` **20/20** (independently re-run here). `Rag.NET.Tests`
1151, `Storage.Sqlite` 78, `Resilience` 95, `Caching` 2, `Parsers.Office` 19,
`DataProviders.Microsoft365` 70, `VectorStores.Qdrant` 14 (Docker, ran locally), `PgVector` 60 —
all at the same baselines Phase 4.7 closed at, since pinning changes no resolved version. Full
solution build: 0 warnings, 0 errors (independently re-run here).
**Renovate** (`renovate.json`, `docs/reference/ci.md`): `renovate.json` gained one `packageRules`
entry — patch and minor bumps batched into a single PR on a weekly schedule
(`before 6am on monday`); majors get no rule and fall through to `config:recommended`'s default —
ungrouped, unscheduled, already "one PR per major, proposed as soon as it's available" — which is
deliberate, since majors are where breakage lives and Qdrant is this phase's own worked example.
Validated with `renovate-config-validator` 2026-08-04 (`Config validated successfully`). The
enable procedure is documented in `docs/reference/ci.md`'s Renovate section: installing the
Renovate GitHub App is the repository owner's action, taken in a browser, and there is no CLI or
API equivalent to fence — the one gate on this page with no runnable procedure, recorded as such
rather than papered over. **Two claims recorded separately, not conflated:** *dependency pinning
is delivered and provable* — the empty 156-line diff, above; *upgrade automation is configured
and unexercised* — `renovate.json` has never proposed a PR, because the app has never been
enabled. Only the first is demonstrated by any work in this repository to date. [**Stale by
2026-08-08, Phase 4.5:** the app is enabled and has been opening PRs since 2026-08-05 — five
`renovate/*` branches live at that correction, several already merged, including a major bump.
Both claims are now demonstrated; see the closed "Three pieces of house furniture" debt entry
above for the evidence.]
**What this does not buy, stated plainly:** pinning does not prevent deprecations. `SearchAsync`
would still have gone obsolete in 1.18.1 whether the reference was pinned or not. What changes is
*how the repository finds out* — a Renovate PR whose CI goes red, reviewed on the owner's
schedule, instead of `main` going red with no commit pushed to explain it.
**A correction worth recording, because it propagated through the plan:** `dotnet pack` **without**
`-p:Version` silently produces the SDK default `1.0.0` — GitVersion is *not* wired into a bare
`dotnet pack`; only `ci.yml`'s `pack-validate`/`publish-nuget` jobs pass it explicitly
(`dotnet dotnet-gitversion /output json | jq -r '.SemVer'`). Several agent briefings during
planning said the opposite, which is why Task 1's baseline was captured at `1.0.0` rather than a
derived prerelease. It did not invalidate the phase's result — external floors do not depend on
this repository's own version — and it is exactly why the correctness diff compares **external
lines only**: the 76 internal `Rag.NET.*` lines legitimately change with whatever version a build
derives.
**A DoD box moved on this phase's evidence, not its own work**
(`docs/planning/ROADMAP.md`'s Milestone 4 DoD, "CI pipeline builds, tests, and produces NuGet
packages"): PR #18 (Phase 4.1's branch) ran `ci.yml` for real and gated its own merge on it —
`pack-validate` and both `build-test` legs green, run **30828032049**, 2026-08-03 — and every
push to `main` since has run the same pipeline for real, including the Qdrant break itself
(red: 30919869612; green again after the fix: 30926805555). That box is ticked now, citing those
runs; this branch itself (Phase 4.8) has never been through GitHub Actions, so the DoD's
all-projects criterion stays open on that basis, not on the pipeline's existence.)

### Phase 4.9: Provider Creation Time [status: complete]
**Goal:** Stop provider-ingested documents claiming they were created at ingestion time, and make
the timestamps connectors already emit actually drive time-weighted retrieval. (Not a features.md
row — created 2026-08-04 out of the `BuildMetadata`-drops-`CreatedAt` debt open since Phase 2.2,
numbered after 4.8 because it was created after, executed next in the milestone's phase list.)
**Plan:** `docs/plans/2026-08-04-provider-creation-time-design.md` +
`2026-08-04-provider-creation-time-implementation.md`
**Completed:** 2026-08-04, branch `fix/provider-creation-time`.
**The defect:** `DocumentMetadata.CreatedAt` defaulted to `DateTime.UtcNow`
(`src/Rag.NET.Abstractions/Models/DocumentMetadata.cs:22`), and nothing on the provider-ingestion
path (`RagPipelineExtensions.BuildMetadata`) ever set it. `MetadataBehavior` wrote that fabricated
value into every chunk's `created_at` tag, and `TimeWeightedRetriever` ranks on it — so a 2019
Confluence page and a document added this morning scored identically on recency. **Not a missing
value — a wrong one, asserted confidently**, which is why it read as working.
**The roadmap's own estimate for this defect was wrong, and the evidence was already on file.** It
had been routed to Phase 4.2 with the note *"the fix is one copied property plus a test: a slot,
not a phase."* The document that routing itself cited —
`docs/plans/2026-07-26-connector-metadata-design.md:237-240` — already said plainly that "a
connector's real creation timestamp cannot reach `DocumentMetadata.CreatedAt` — only a tag." Four
reasons, each measured rather than argued, why the one-line fix could never have worked:
`baseMetadata` is a **per-call**, not per-document, parameter on `IngestFromProviderAsync`
(`RagPipelineExtensions.cs:67-76`), so copying it would stamp every document in a run with one
identical value, not each document's own creation time; **no production caller sets it** —
`BackgroundPollingTrigger`, the only production caller found, never passes `baseMetadata`;
`FileEntry`/`FileHandle` carry **no timestamp field**, so a connector has no typed channel to
supply one even if it wanted to; and `created_at` is a **reserved** tag key — a connector that
emits it gets `ReservedMetadataKeyException`, so the tag channel is closed too. That routing is
corrected on the debts list (moved to Closed), not left standing beside this entry.
**Task 1 (red first, `2166eab`):** added
`IngestFromProvider_WithNoTimestampFromTheConnector_DoesNotFabricateACreationTime` to
`IngestFromProviderTests.cs` — the join between the connector side and the behavior side that no
existing test covered. Confirmed red first: *"Connector supplied no timestamp; chunk metadata must
not fabricate one, but found '2026-08-04T19:40:09.9483802Z'."* Made `CreatedAt` a nullable
`DateTime?` with no default — a **breaking change to an unpublished type**; nothing is published
and no package ID is reserved, so no `[Obsolete]` shim was needed or added — and made
`MetadataBehavior` write `created_at` only when a value exists, keeping `TryAdd` so a
connector-supplied tag still wins. The three other read sites (`ContainerContext`,
`ContainerEntryDispatcher`, `EmbeddedMessageMetadata`) are plain pass-throughs and compiled
unchanged; no existing test assertions needed to change.
**Task 3 (`258a6b9`):** `BuildMetadata` copied `ContentType` from the caller-supplied
`baseMetadata` but silently dropped `CreatedAt`. Wired it through — stated precisely as what it
is, not oversold: this makes the batch-level override real, it does **not** give any individual
document its own real creation time, and overstating it would recreate exactly the "one copied
property" misunderstanding the routing correction above exists to fix.
**Task 4 (`7a5bb61`):** `TimeWeightedOptions.FallbackMetadataKeys` — the mechanism
`TimeWeightedRetriever` already implements to resolve a timestamp from a connector tag when
`created_at` is absent — defaulted to `[]`, built and wired to nothing. Verified against the
actual connector source, not the design doc's claim: default is now `["updated_at", "published_at",
"lastmod", "received_at"]`, broadest coverage first — `updated_at` (Asana, Jira, Notion, Zendesk
tickets and articles), `published_at` (RSS/Atom), `lastmod` (Sitemap), `received_at` (Exchange — a
distinct key, since "when this message arrived" is Exchange's own vocabulary, not a value it
copies into `updated_at`). **One correction this verification forced**: the design listed Linear as
covered by `updated_at`; it is not — Linear's `updatedAt` feeds only the connector's `ETag` and
delta-token watermark and is never copied into a chunk tag, so it was dropped from the list before
shipping. `"date"` (Gmail, Slack, Teams) stays excluded from the default by design, not oversight:
Gmail's is a full timestamp, Slack's and Teams' are day-granularity only, and the key is generic
enough that a caller's own metadata may mean something unrelated by it — documented as a one-line
opt-in.
**Task 5 (`b35a835`):** pinned the property the whole design rests on —
`TimeWeightedRetriever.ComputeDecay` returning `1.0` for an absent timestamp was incidental,
covered by no test. Added a test asserting a chunk with no `created_at` and no matching fallback
key scores exactly its base score, and proved it can fail: mutated the null-timestamp branch to
`return 0.9;`, confirmed four tests went red, reverted (`git diff` on the file empty before
committing).
**What this phase does not fix, priced rather than left a slot:** those connectors now rank
**neutrally rather than wrongly** — better, and honest about being incomplete. Of 25 providers,
**17 hold a real timestamp today and discard it** (folded into an opaque `ETag` or an un-promoted
tag), **4 more do not even fetch it** (Confluence, Jira, Box, GoogleDrive — the same four of Phase
2.2's five narrowed-field-selection connectors that touch a creation/update timestamp; widening
them needs DTO changes and re-recorded WireMock cassettes), and **4 genuinely have none**
(Bitbucket, GitHub, GitLab, WebCrawler). Closing the gap needs a typed timestamp field threaded
through `FileEntry`/`FileHandle`/`BuildMetadata` plus ~17 connector changes — scheduled as **Phase
4.10**, not left as a slot a second time.
**Documentation:** `docs/guide/retrieval.md`'s time-weighting section corrected two false
statements — `FallbackMetadataKeys` documented as defaulting to `[]` (now the shipped default,
with the per-connector coverage table and the `date`/Linear exclusions spelled out) and
`DocumentMetadata.CreatedAt` documented as defaulting to `DateTime.UtcNow` (now: no default, and
what "no timestamp" means for ranking). While already in `docs/guide/data-providers.md` for this
change, also fixed the five documented members Phase 4.7's README guard found and routed there:
Slack `ChannelIds` → `ChannelId`, Gmail `EmailAddress`/`ImapHost`/`ImapPort` → `UserName` (the IMAP
host and port are not configurable at all — `imap.gmail.com:993` is hardcoded), Confluence
`SpaceKeys` → `SpaceKey`, GitLab and Bitbucket `Branch` → `Ref` — each verified against the actual
option class before writing it, not copied from the routed debt's own table.
**Counts:** `Rag.NET.Tests` 1151 → **1159**. `RepoConventions` unchanged at 36 + 1 skip.
`Rag.NET.DataProviders.Tests` **69**. Full solution build: 0 warnings, 0 errors.
**The test gap that let this survive:** nothing asserted what `CreatedAt` became after provider
ingestion — `IngestFromProviderTests` only checked that a connector *emitting* `created_at` throws,
and `MetadataBehaviorCreatedAtTests`/`TimeWeightedRetrieverTests` tested the two ends in isolation.
The path between them was untested, which is exactly where the defect lived.

### Phase 4.10: Connector Timestamp Threading [status: complete]
**Goal:** Give connectors a typed channel for the real creation/update timestamp they already
hold, so it reaches `DocumentMetadata.CreatedAt`/`UpdatedAt` instead of only ever a tag (or
nothing).
**Not a features.md row** — created 2026-08-04 out of Phase 4.9's own measurement, priced there
rather than left as a routing arrow a second time (Phase 4.9, above).
**Plan:** `docs/plans/2026-08-05-connector-timestamps-design.md` +
`2026-08-05-connector-timestamps-implementation.md`
**Completed:** 2026-08-05, branch `feature/connector-timestamps`.
**Two typed fields, not one.** `DocumentMetadata` gains `UpdatedAt` beside `CreatedAt` (both
`DateTime?`, neither defaulted, neither ever backfilled from the other), threaded through
`FileHandle`/`FileEntry` as optional trailing parameters and through `FileContentProviderBase` for
the connectors that go through it. `RssDataProvider`, `SitemapDataProvider` and
`WebCrawlerDataProvider` implement `IFileContentProvider` directly and never touch `FileHandle`, so
the field was threaded onto `FileEntry` for that path independently — missing it would have failed
silently, indistinguishable from a connector with no timestamp at all (`e6b834b`).
**`updated_at` reserved, five hand-writers migrated (`3a9fdb7`).** Reserving the key before its
existing writers stopped would have thrown `ReservedMetadataKeyException` for every one of them at
ingest time, not compile time, so reservation and migration landed in one commit: Asana, Jira,
Notion, Zendesk Articles and Zendesk Tickets each dropped their hand-written
`metadata["updated_at"]` tag in favour of `FileHandle.UpdatedAt`, parsed via a new
`ConnectorTimestampParser`. `ReservedMetadataKeyGuardTests` (`Rag.NET.RepoConventions.Tests`) now
scans every connector source file for a hand-written reserved-key assignment and was proven red
first by reinstating the Asana line.
**~17 connectors populated across three commits (`d6ba3ea`, `f3e6658`, `2812915`):** Microsoft 365
(OneDrive, SharePoint, Teams, Exchange), Web (RSS/Atom, Sitemap, WebCrawler), and the rest
(Dropbox, Linear, Airtable, Gmail, Slack, LocalFiles, AzureBlob, GitHub, GitLab, Bitbucket) — the
last three recorded, with a test each, as genuinely having no vendor timestamp rather than being
skipped by oversight.
**Confluence, Box, GoogleDrive widened (`0c1230f`, `0975abe`, `b3f026c`)** — the three connectors
whose real cost this whole phase turned on, and the three that produced this phase's most valuable
findings:
1. **The cassette cost was fictional.** Phase 2.2's "Recorded, not fixed" section priced this
   widening as needing re-recorded WireMock cassettes; Phase 4.9's design cited that price; this
   phase's own design repeated it — three planning documents agreeing with each other and all
   wrong. **There are no WireMock cassettes anywhere near these three connectors' test suites.**
   Confluence uses inline JSON literals fed to a fake `HttpMessageHandler`; GoogleDrive uses a fake
   HTTP handler of the same shape; Box has no offline HTTP layer to fake at all — its tests call
   the internal `ToHandle` mapping directly against a real `BoxClient`. Corrected on Phase 2.2's own
   entry above, so the false cost stops propagating from there too.
2. **Confluence needed no `expand` widening at all.** The default `expand=body.storage,version`
   already returns `version.when` — `IConfluenceApi` needed no change, only the DTO mapping and
   `ToHandle` did, contrary to this phase's own design's expectation.
3. **Jira never needed DTO work.** `IJiraApi` already requests `fields=…,updated`; this was
   already corrected once, inside this phase's own design (§1), which caught its own prior
   framing (inherited from Phase 4.9's design, which listed Jira correctly in §3 among connectors
   already writing the tag and wrongly in §4 among connectors that do not even fetch it) before any
   code was written. Of the original four "does not fetch it" connectors, only Confluence, Box and
   GoogleDrive actually needed widening.
4. **An unresolvable analyzer conflict on `DateTime?` scalar parameters, worth recording as a house
   pattern.** EPS05 (ErrorProne.NET) wants a large-readonly-struct parameter passed by `in`; RCS1242
   (Roslynator) rejects `in` on a non-readonly struct, and `DateTime` is exactly that. Both Box's
   `ToHandle` and GoogleDrive's `BuildHandle` sidestep the conflict by taking the source
   reference-typed object (`BoxItem`, `Google.Apis.Drive.v3.Data.File`) instead of two `DateTime?`
   scalars — a wrinkle neither connector's task description anticipated, and the pattern the next
   connector hitting this same pair of analyzers should reach for first.
5. **GoogleDrive's `CreatedTime`/`ModifiedTime` are `[Obsolete]`**, and their `DateTimeOffset`
   replacements (`CreatedTimeDateTimeOffset`/`ModifiedTimeDateTimeOffset`) are themselves
   `[JsonIgnore]` — only the `*Raw` string properties round-trip through the test project's
   Newtonsoft-based JSON builder, so the fixtures are built from those. Neither wrinkle was
   anticipated by the task description either.
6. **A gap, stated rather than implied.** GoogleDrive has four field masks needing the same
   widening (whole-drive, folder-traversal, and two Changes.List pages); three are covered by a
   dedicated test each. **The fourth — delta pagination's second page — has no test**, because no
   existing test drives GoogleDrive delta pagination at all, matching pre-existing coverage. Recorded
   as debt, not implied as complete.
**The `updated_at` writer count was five, not eight.** This phase's own design (§2, §4) said eight,
double-counting writers of `published_at`, `lastmod` and `received_at` — three different keys that
stay unreserved and connector-specific (see `docs/guide/data-providers.md`). Only Asana, Jira,
Notion, Zendesk Tickets and Zendesk Articles ever hand-wrote `updated_at` itself.
**Documentation:** `docs/guide/retrieval.md`'s time-weighting section rewritten in place (not
appended) for the new `UpdatedAt` → `CreatedAt` → `FallbackMetadataKeys` → neutral resolution
order, with the now-false "Linear is not covered" claim removed (Linear's `updatedAt` now reaches
`FileHandle.UpdatedAt` directly, no longer only its `ETag`). `docs/guide/data-providers.md` gains a
Timestamps section (which connector supplies which of the two, verified against source, including
Bitbucket's investigated-but-unconfirmed status — see above), an eighth reserved key in the
Reserved keys table, and the now-stale "`updated_at` is not comparable across connectors" caveat
rewritten to describe the new centrally-formatted reality.
**What remains unfixed, stated plainly rather than left implicit:** connectors with no vendor
timestamp on the objects they fetch — GitHub, GitLab, WebCrawler, and Bitbucket after
investigation — still rank neutrally under `TimeWeightedRetriever`. That is correct, not a gap:
there is nothing to wire up. Bitbucket's `commit` object embedded in the src-listing/diffstat
responses is documented as a minimal reference distinct from the full commit resource (which does
carry `date`) behind a separate per-commit endpoint; confirming it and reaching it would both need
a second API call per file, outside this phase's "populate from data already in hand" scope — left
unset rather than guessed. Slack's and Teams' `date` tags remain day-granularity
(`yyyy-MM-dd`); normalising them to the full precision their new typed fields now carry internally
was out of scope and stays open.
**Counts:** `Rag.NET.Tests` 1159 → **1169**, `Rag.NET.DataProviders.Tests` 70 → **71**,
`RepoConventions` 36+1 → **37+1**, `Microsoft365.Tests` 70 → **74**, `Web.Tests` 27 → **31**,
`Confluence.Tests` 20 → **21**, `Box.Tests` 13 → **15**, `GoogleDrive.Tests` 10 → **13**;
Dropbox/Linear/Airtable/Gmail/Slack/AzureBlob/GitHub/GitLab/Bitbucket each +1. Full solution build:
0 warnings, 0 errors.
**Explicitly out of scope for this phase** (per the 4.9 design, §6, reaffirmed by this phase's own
design §8): no change to `created_at`'s reservation; no `ModifiedAt` field name (settled as
`UpdatedAt`, since the emitted tag is `updated_at` and the pre-existing `FallbackMetadataKeys`
default already used that vocabulary); no normalisation of Slack's/Teams' day-granularity `date`
tags; no removal of `lastmod`/`published_at`/`received_at` as connector-specific tags.

### Phase 4.11: Chunk Index Collision Fix [status: complete]
**Goal:** Make `ChunkIndex` unique within a document, as its own documentation already requires,
so a chunk stops overwriting another at write time and two unrelated chunks stop merging into one
at read time. (Not a features.md row — created 2026-08-06 out of a documentation pass, numbered
after 4.10 because it was created after, executed next in the milestone's phase list.)
**Plan:** `docs/plans/2026-08-06-chunk-index-collision-design.md` +
`2026-08-06-chunk-index-collision-implementation.md`
**Completed:** 2026-08-06, branch `fix/chunk-index-collision`.
**How it was found — not by a test.** Found while **documenting `IChunkingStrategy.ChunkAsync`**
during the XML documentation pass: a draft summary claimed callers renumber `ChunkIndex` across
sections, and reading `ParseBehavior.ChunkPerSectionAsync` to verify that claim showed they do
not. **The contract was already written down** — `TextChunk.ChunkIndex`'s own documentation says
it "must be unique within a document" — and nothing enforced it.
**The defect:** `ParseBehavior.ChunkPerSectionAsync` (`ParseBehavior.cs:82-101`) called
`ChunkingStrategy.ChunkAsync(section, …)` once per section. Every built-in strategy — the default
`RecursiveChunkingStrategy` (`:57`) and `FixedSizeChunkingStrategy` (`:48`) — assigns
`ChunkIndex = chunkIndex++` from a counter local to that single call, so indices restarted at 0
for every section and `ParseBehavior` appended them unchanged. Nothing else in `src/` assigns
`ChunkIndex` (verified by grep). Because `RecursiveChunkingStrategy` implements `IChunkingStrategy`
and not `IDocumentChunkingStrategy`, this is the **default** path — any multi-section document,
any Markdown or PDF with headings, was affected.
**Blast radius — seven identity-key sites keyed on `(DocumentId, ChunkIndex)`:**
`DeterministicChunkId.Derive` (Qdrant sparse store `:147`, Weaviate `:223` — **one chunk silently
overwrote another at write time**), `MultiQueryBehavior.cs:44`'s `GroupBy`, `RrfMerger.cs:57`,
`DeepResearchRetriever.cs:79`, and `FederatedVectorStore.cs:181` (all four — **unrelated chunks
merged at read time**), `ParentChunkKeyHelper` (**wrong parent** returned for a child chunk), and
`RagPipelineReindexExtensions.cs:192` (its own comment already documents that it replaces by that
pair — a promise the collision broke silently).
**Task 1 (`32983b4`):** `ParseBehaviorChunkIndexTests.cs` — drove `ParseBehavior` end-to-end with
the default `RecursiveChunkingStrategy` and two stub sections, each engineered to force at least
two chunks (`MaxChunkSize=50` against text far longer, word-boundary-packed with no long
unbroken run — confirmed independently by a companion sanity test,
`ChunkCountPerSection_IsAtLeastTwo`, so the collision assertion could not pass vacuously off a
single chunk per section). **Actual failure, confirmed before any fix:** `Assert.Equal()
Failure: Values differ — Expected: 10, Actual: 5` — 10 total chunks, only 5 distinct `ChunkIndex`
values, because both sections independently produced 5 chunks numbered 0–4.
**Task 2 — verified, not assumed:** `ChunkDocumentAsync` (`ParseBehavior.cs:71-80`) passes **all**
sections to `docStrategy.ChunkDocumentAsync(...)` in **one** call, so a strategy *can* number
globally — read all twelve `IDocumentChunkingStrategy` implementers in `src/` rather than trusting
that: `SemanticChunkingStrategy`, `HierarchicalMergerChunkingStrategy`, `LateChunkingStrategy`,
`PropositionChunkingStrategy`, the six `Rag.NET.Chunking.Templates` strategies (`AcademicPaper`,
`Book`, `Email`, `Legal`, `QAPairs`, `Resume` — one more than the plan's "five", since `QAPairs`
and `Resume` implement only `IDocumentChunkingStrategy`, not both interfaces, but both were still
in scope), and `Image`/`VideoChunkingStrategy` in `Rag.NET.Parsers.Vision`. **Verdict: none
restarts its counter per section** — every implementer keeps a single running index for the whole
`ChunkDocumentAsync` call, several by explicit delegation to `HierarchicalMergerChunkingStrategy`'s
own document-wide counter. Pinned by `ParseBehaviorDocumentChunkingIndexTests.cs` (`1f8bd1e`), a
real `HierarchicalMergerChunkingStrategy` over a three-heading document through `ParseBehavior`
end-to-end, asserting distinct indices. No implementer changed — this is a pinning test, not a
fix, and the branch needed none.
**Task 3, the fix (`64e48c1`):** a running `documentChunkIndex` counter in
`ChunkPerSectionAsync`, renumbering as each chunk is appended:
`ctx.Chunks.Add(chunk with { ChunkIndex = documentChunkIndex++ })`. `TextChunk` is a `sealed
record`, so `with` copies cheaply; nothing in `src/` sorts by `ChunkIndex` (verified), so
renumbering has no ordering consequences. **Deliberately does not touch any chunking strategy**:
a strategy sees one section and cannot know its offset within the document, and
`IChunkingStrategy` is a public extension point implemented by user-written strategies too — the
fix belongs where sections are joined, not in any one of the many places that produce them.
**Two pre-existing tests failed as a direct, reported-not-silently-fixed side effect** —
`ParseBehaviorTests.SingleSectionSingleChunk_PopulatesChunksAndSections` and
`ParseBehaviorRefinementTests.WhenRefinementStrategyIsNull_ChunksPassThroughUnchanged` — both
asserted `Assert.Same(rawChunk, ctx.Chunks[0])`: reference identity with the chunk instance the
strategy returned. That identity is never preserved now, because every chunk is copied via `with`
even when its `ChunkIndex` does not change. This is a test depending on an incidental
implementation detail (instance identity, not value), not a test encoding the per-section-restart
defect — left unmodified per the plan's instruction to report rather than adjust.
**Task 4, the consumers (`cf50dff`):** write-time — `DeterministicChunkIdTests.cs` pins that two
chunks from different sections of the same document (distinct `ChunkIndex`) derive **distinct**
GUIDs via `DeterministicChunkId.Derive`, the id shared by Qdrant's sparse store and Weaviate.
Read-time — `RrfMergerSectionCollisionTests.cs` pins that `RrfMerger.MergeMany`, which dedups on
`(DocumentId, ChunkIndex)`, keeps two genuinely different chunks from different sections as two
separate results rather than collapsing them via `chunkLookup.TryAdd` — the exact mechanism that
silently discarded one chunk's content at read time before the fix. Both internal types;
`Rag.NET.Tests` already has `InternalsVisibleTo` access to `Rag.NET.Abstractions`, so no
accessibility changes were needed.
**Why no existing test caught this:** no test anywhere covered multi-section chunking producing
more than one chunk per section — the exact condition needed to observe a collision. Read
`PipelineIngestorChunkingValidationTests.cs`, which already exists in the same area, to check
whether it validates chunking itself: **it does not — it validates `ChunkingOptions`
(`MaxChunkSize`/`Overlap`) rejection via `PipelineIngestor.IngestAsync`'s validation step (invalid
size, invalid overlap, and a valid-options success case). It never inspects the chunks produced
or their indices.** Its name describes the options being validated, not chunk output being
checked — nothing in the repository did the latter until this phase.
**Existing data — stated plainly:** `DeterministicChunkId` output changes for every chunk whose
`ChunkIndex` was renumbered, so previously ingested multi-section documents need re-ingestion to
land at their new deterministic ids. **This is not data loss caused by the fix — the data was
already corrupt**: under the old numbering, colliding `(DocumentId, ChunkIndex)` pairs meant a
document's later-section chunks had already been silently overwriting or merging with its
earlier-section chunks at write and read time. The fix recovers from data loss already in
progress; it does not create it.
**Counts:** `Rag.NET.Tests` 1172 → **1179** (7 new tests: 2 Task 1, 1 Task 2, 2 Task 4 write-time,
2 Task 4 read-time). **1177 passed, 2 failed** — the two identity-assertion tests named above,
left unmodified and reported rather than adjusted. `RepoConventions` unchanged at 37+1 skip (not
touched by this phase). Full solution build: 0 warnings, 0 errors.

### Phase 4.12: SystemPrompt Coverage (Issue #56) [status: complete]
**Goal:** Establish, provably rather than by reading source, whether `RagOptions.SystemPrompt`
does what [issue #56](https://github.com/MarcelRoozekrans/Rag.NET/issues/56) reported it does
not. Changes no production behaviour — this phase is coverage and documentation only. (Not a
features.md row — created 2026-08-07 out of a user report, numbered after 4.11 because it was
created after, executed next in the milestone's phase list.)
**Plan:** `docs/plans/2026-08-07-system-prompt-coverage-design.md` +
`2026-08-07-system-prompt-coverage-implementation.md`
**Completed:** 2026-08-07, branch `fix/system-prompt-coverage`.

**The origin.** Issue #56, *"SystemPrompt ?"*: a user's `RagOptions.SystemPrompt` against Azure
OpenAI GPT-5 asked for the exact sentence *"I cannot find any relevant information."* and the
model returned a paraphrase followed by *"Sources used: Source 1, Source 2…"*.

**The verdict: `SystemPrompt` was never broken.** It is applied in all four answer engines and
preserved by `PromptHardeningAnswerEngineDecorator`
(`src/Rag.NET.Security/PromptHardeningAnswerEngineDecorator.cs`). Two things explain the report,
neither a bug: the `[Source N]` context labels invite citations regardless of what the system
prompt says, and the model paraphrased a canned string — ordinary LLM behaviour, not a defect.
**The actual defect was that none of this could be established without reading the source** —
untested, invisible behaviour, not a bug fix. `docs/guide/retrieval.md` now documents the literal
`Context:`/`[Source N]`/`Question:` shape the final user message carries, the conversation-history
ordering rule below, and how to register an `IPromptObserver` to see the assembled prompt directly
(`1a9046a1`) — the seam that would have let the reporter answer his own question in one run.

**The existing test passed for the wrong reason.** `AskAsync_WithCustomSystemPrompt_UsesIt`
asserted `msgs[0].Text == "Custom prompt"`. That held only because its fixture supplied no
`ConversationHistory` — with a leading history system message, `ChatAnswerEngine` places it before
the caller's `SystemPrompt` (deliberately, so a host-injected prompt-hardening prefix is never
shadowed by a per-request prompt), so the caller's prompt lands at index 1, not 0. Any change
moving the prompt's position would have been caught only when a user had history *and* noticed.
Fixed to assert by role and content instead (`f5b23728`), and a second test,
`AskAsync_WithHistorySystemMessageAndCustomSystemPrompt_OrdersHistorySystemFirst`
(`c2151ef4`, `tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs`), pins the full
ordering the first assertion had been silently depending on.

**Three coverage layers, three tiers:**

| Layer | Test(s) | Proves | Tier |
|---|---|---|---|
| Engine-level mock | `ChatAnswerEngineTests` (existing + `c2151ef4`) | the message list is built correctly | gating |
| Full-pipeline mock | `AskAsync_WithCustomSystemPrompt_ReachesChatClientAsSystemMessage`, `AskStreamingAsync_WithCustomSystemPrompt_ReachesChatClientAsSystemMessage` (`9febb9be`, `tests/Rag.NET.Tests/Pipeline/RagPipelineFacadeTests.cs`) | `SystemPrompt` survives `RagPipeline.AskAsync` **and `AskStreamingAsync`** | gating |
| Real model, no sources | `AskAsync_CustomSystemPrompt_MarkerAppearsInRealProviderResponse` (`3345bbda`, `tests/Rag.NET.E2ETests/SystemPromptE2ETests.cs`) | the prompt reaches the provider and changes output | nightly `RequiresLlm` |
| Real model, sources retrieved | `FullPipeline_CustomSystemPrompt_HoldsWhenSourcesAreRetrieved` (`987d7bb6`, `tests/Rag.NET.E2ETests/FullPipelineTests.cs`) | the prompt survives **real retrieved context** — the only case #56 was about | nightly `RequiresLlm`, OpenRouter-gated |

Before this phase, only the first layer existed. The full-pipeline mock closes a real gap: the
pre-existing pipeline tests substitute `IAnswerEngine` entirely, so they never touched
`ChatAnswerEngine`'s message-building code — a change swallowing the prompt between `RagOptions`
and `IChatClient` would have passed CI.

**The second finding — unrelated to #56, and the more valuable one.** Running the real-model test
surfaced a live defect in the test infrastructure itself: `TestChatClientFactory`'s default
OpenRouter model, `nvidia/llama-3.1-nemotron-70b-instruct`, had been **delisted** — absent from the
400 models OpenRouter's catalogue now returns — so every request against it failed with an opaque
`HTTP 404: No endpoints found` rather than anything resembling a test failure. It went unnoticed
because `OPENROUTER_API_KEY` appeared nowhere in CI: the nightly LLM tier always took the Ollama
fallback, so the OpenRouter branch was unreachable in CI and rotted unobserved. This is the same
**inert-path** failure shape the repository has hit before — a fallback that quietly becomes the
only path, and a primary path nobody watches go green. Fixed in `405460d7`, two changes because
either alone leaves the trap armed: the default replaced with `meta-llama/llama-3.3-70b-instruct`
(`tests/Rag.NET.Testing/TestChatClientFactory.cs`, with a note on where to re-check when it starts
404ing in turn), and `OPENROUTER_API_KEY` wired into the nightly LLM tier (`.github/workflows/nightly.yml`)
so the branch is exercised rather than merely present. Side effect: `OllamaFixture` now skips the
`llama3.2:1b` pull when the key is set, roughly halving the tier's model download; unset, both
suites still pass on Ollama.

**Why the marker test is trustworthy.** The first instruction wording was **ignored by the
`llama3.2:1b` fallback** — it answered *"The capital of France is Paris."* with no marker at all.
The assertion was **not weakened** to accommodate that: the instruction was made blunter and
shorter, and the fallback then followed it in **3 of 3** runs. Verified on both paths — OpenRouter
and Ollama. For the source-free test, gating on `IsOpenRouterAvailable` was considered and
rejected, because a skip in the tier when no key is set is exactly how the OpenRouter path went
stale in the first place. The test deliberately does not assert the reporter's own case (an exact
requested sentence) — asserting exact text would make it flaky for precisely the reason the issue
exists.

**The empty-store gap, caught in review.** The first real-model test ran against an empty
`InMemoryVectorStore`, so the context block was empty and the `[Source N]` labels never appeared at
all — it proved the prompt reaches the provider, but not that it survives retrieved context, which
is the only situation issue #56 describes. `987d7bb6` closes that in `FullPipelineTests`, which
already ingests three documents into PgVector. All three of its assertions are load-bearing, and
the weakest-looking one earned its place immediately: instructed to append a marker, `llama3.2:1b`
returned **the marker and nothing else**, so a marker-only assertion would have gone green on an
empty answer. Unlike the source-free test, this one **is** gated on `IsOpenRouterAvailable` — with
a context block competing for its attention the 1B model either emitted the marker alone or filled
the brackets in as a template (`<<Paris, France>>`) in 3 of 3 runs. Two rewordings did not move it,
so the model is the limit rather than the wording and the assertion was left intact. The gate is
live rather than inert because the nightly now supplies the key, and it was verified in both
directions: skips without a key, passes with one.

**The question this phase did not answer.** The reporter asked *"What is my address?"* and got
*"There isn't enough information in the provided context…"*. They raised it as a `SystemPrompt`
complaint — they wanted a literal canned sentence — and that framing is what this phase answers.
But what they actually wanted was the address, and the issue never establishes whether it was in
their documents at all. The evidence points at retrieval rather than the prompt: with
`TopK = 5, MinScore = 0.5` the model reported using Sources 1–5, so five chunks cleared the floor
and nothing was filtered into silence. If the address is in the corpus, the failure is **ranking** —
and `"What is my address?"` is close to a worst case for dense retrieval, being four words
dominated by a pronoun against a target chunk that shares almost no semantic surface with it. The
maintainer reply asked about `Temperature` and pointed at `IPromptObserver` but never asked the one
question that settles it: *is the address actually in your documents, and does it appear in the
five retrieved sources?* Recorded as an open question needing the reporter's data, **not** as a
finding, and deliberately not pursued on this branch.

**Left open, not recorded as fact.** The reporter was asked whether `Temperature` is the actual
cause, since several recent OpenAI reasoning models reject or ignore it. No answer yet — an open
question awaiting the reporter, not a finding of this phase.

**What this phase did not do.** It changed no production behaviour — `SystemPrompt`'s own code is
untouched; every commit is `test`, `fix(tests)`, `fix(testing)`, or `docs`. It does not resolve
issue #56 pending the `Temperature` question. It was never one of the milestone's numbered
`features.md` items, the same status Phases 4.9–4.11 had; this entry does not itself update the
DoD's "all planned phases complete" tally above, which as of this phase's close still reads
"6 of 11 as of 2026-08-05" and does not mention Phase 4.11 either, despite 4.11 having completed
2026-08-06 — a pre-existing gap this phase found but did not fix, being documentation-only and out
of that scope.

### Phase 4.2: Parser Registration Ownership [status: complete]
**Goal:** Originally "Options Alignment & Validation." Arrived carrying five workstreams
re-pointed into it by four earlier phases — parser replacement, `message/rfc822` ownership,
options homes, connector deferrals, and repo-wide XML documentation — and the phase's own
measurement (design §0) split two of them back out: documentation and connectors share nothing
with the rest and needed their own scoping. What remained, and what this phase actually built, is
one coherent subject: **who owns a content type, and how that is declared**, so that two parsers
claiming one is either a loud startup error or an explicit, working override — never a silent one.
The general `IOptions`/`ZeroAlloc.Validation` alignment the original name promised is **not** part
of what shipped; see the open debt above.
**Backlog items:** parser replacement (originally routed "with 4.1"), `message/rfc822` ownership
(Phase 3.11's deliberate non-decision), options homes (`CostBudgetOptions.DatabasePath`) — three of
the five re-pointed workstreams; documentation and connectors were split out, unscheduled.
**Plan:** `docs/plans/2026-08-07-parser-registration-ownership-design.md` +
`2026-08-07-parser-registration-ownership-implementation.md`
**Completed:** 2026-08-08 (`cd07bedf`, `9cd89c73`, `0d735ddc`, `cec11537`, `590ce6fd`, `1668267e`,
`5acb3740`).
**The measurement that moved the phase, and its own first version was wrong.** The intended
centrepiece was a convenience API for replacing a built-in parser. Measuring first found that
`ParserClaim` — the guard that makes two parsers claiming one content type a startup error — is
silent for most of this repository's parsers, and that the replacement API is the vocabulary that
silence is missing, not a convenience layered on top of it. **The first pass at that measurement
overstated it in three ways, corrected in design §1.1 rather than quietly rewritten, because the
wrong version was persuasive and nearly reached implementation**: it counted "11 parsers, ~22
content types, declare nothing" as a coverage hole, when seven of those eleven register through
`AddParser<T>()`, a path `ParserClaim`'s own remarks already document as unable to declare
anything — `CanParse` is a predicate, not an enumeration, and probing it against a guessed list of
content types is "a worse mechanism than an undetected collision." It counted **two** live silent
collisions, when `CsvDocumentParser` carries no `[Singleton]` attribute and nothing registers it by
default, so its `text/csv` overlap with `QAPairsDocumentParser` is conditional on a caller adding
it explicitly — only `…spreadsheetml.sheet`, between `ExcelDocumentParser` and
`QAPairsDocumentParser`, was live for everyone. And it counted a third collision, `image/jpeg`
between the two Vision parsers, that did not exist — the string in `VideoDocumentParser` is the
MIME type of an extracted video *frame*, not a `CanParse` claim, found by grepping whole files
rather than reading `CanParse` bodies. **The one genuine oversight the corrected measurement found:
Vision.** It registers two parsers through `AddSingleton<IDocumentParser>`, the mechanism Archive,
Email and this repository's own Chunking.Templates use *with* claims, and declared none — an
inconsistency with its own peers, not the documented `AddParser<T>()` limit. The pattern behind all
three overstatements was the same: a count taken from text matching, then reasoned about as though
it had been read. *Grepping a file is not reading a method.*
**Why the ordering is load-bearing (design §2).** Closing the coverage gap before QA-pairs chunking
can declare an override would turn `UseQAPairsChunking()` into a startup error for anyone also
using `Rag.NET.Parsers.Office` — the `…spreadsheetml.sheet` overlap is legitimate (a caller who
asked for QA-pairs chunking wants that parser to win), but the claim model had no vocabulary for a
deliberate override. So the seven tasks ran API-first: **Task 1** added
`RagBuilder.AddParser<TParser>(replaces:, replacesTypeNames:)`, which removes the replaced parser's
`IDocumentParser` descriptor and its `ParserClaim` together rather than only silencing the conflict
— silencing alone would not have been enough, since selection takes the first registered match and
built-ins register first. **Task 2** had `UseQAPairsChunking()` adopt it, declaring `text/csv`
against `CsvDocumentParser` and `…spreadsheetml.sheet` against `ExcelDocumentParser` by type *name*
(`replacesTypeNames`), so overriding a parser from an optional package that may not be installed is
a no-op rather than a compile-time dependency on it. **The behaviour change this is**: enabling
QA-pairs chunking now means plain CSVs (and Excel workbooks, with Office installed) are parsed as
QA pairs, because that is what the override says — the opposite of the old default, where core's
`CsvDocumentParser` silently won and `QAPairsDocumentParser` never ran. **Task 3** closed the
coverage gap structurally rather than site by site: an opt-in `IDeclaresContentTypes` interface
lets a parser enumerate the content types its own `CanParse` accepts, and `AddParser<TParser>()`
declares one `ParserClaim` per type automatically when `TParser` implements it — adopted by all
nine parsers that can state their set honestly (Audio, Epub, Html, Word, PowerPoint, Excel, Pdf,
Image, Video), closing Vision's oversight along with the rest. `IDocumentParser` and `CanParse`
themselves are unchanged; a parser that cannot enumerate its types honestly simply does not opt in
and keeps today's documented invisibility. Declaring claims from a runtime type with no compile-time
constraint needed this repository's first `MethodInfo.MakeGenericMethod` call — recorded as a new
debt, not a defect (see below). **Task 4** added `ParserClaimCoverageTests`
(`Rag.NET.RepoConventions.Tests`) holding Task 3's declarations to `CanParse` in both directions,
plus the rule that no parser may claim `application/octet-stream` — watched red by deliberately
mismatching one parser's declared list, confirmed the failure named that parser, then reverted.
**Task 5** retired `EmailTemplateDocumentParser` outright: it duplicated `Rag.NET.Parsers.Email`'s
strictly more capable `EmailDocumentParser`, and `UseEmailChunking`'s own remarks already recorded
that the chunking strategy "does not care which parser produced" its sections. `UseEmailChunking`'s
`registerParser` parameter is removed with it — **breaking change**: `.eml` ingestion alongside
this chunking strategy now needs `Rag.NET.Parsers.Email` (`AddEmailParser()`) added separately —
and `MimeKit` drops from `Rag.NET.Chunking.Templates`, verified in the packed nuspec's
`<dependencies>` per Phase 4.7's own lesson that a floating reference freezes into the nuspec
regardless of intent. `QAPairsDocumentParser` was deliberately not touched — `CsvHelper` and
`ClosedXML` stay, because `QAPairsChunkingStrategy` reads the answer out of `DocumentSection.Heading`
as a documented internal contract with that parser, and an earlier design that proposed retiring
both templates on symmetry was wrong. **Phase 4.7's Task 10 is therefore partly complete, not
finished** — only `MimeKit` drops. **Task 6** removed `CostBudgetOptions.DatabasePath`,
`DefaultDatabasePath`, and the `UseCostBudgeting()` guard that turned a non-default value into a
runtime error, together — after removal the compiler is the error, which is strictly better than
the runtime one it replaces. **Task 7** (this entry, and the guide-page updates it references) is
documentation only.
**Three new debts found during implementation, all recorded above rather than fixed inline**:
`AddParser<T>(replaces:)` cannot reach a factory-registered parser (`ImplementationType` is `null`
for it) — the same silent-no-op shape this phase set out to remove, now narrowed rather than
closed; the first `MakeGenericMethod` call in `src/`, with no AOT/trim analyzer enabled anywhere to
have caught it, routed to Phase 5.1 as the first phase that measures Native AOT startup time; and
`ParserClaimCoverageTests` reading `ContentTypeMap`'s private `s_map` field by reflection, because
the map has no public enumeration surface — fails loudly if the field is renamed, but the coupling
is real.
**Definition-of-Done honesty.** This phase closes three of the five workstreams re-pointed into
it — parser replacement, `message/rfc822` ownership, `CostBudgetOptions.DatabasePath` — and
explicitly does **not** close the general `IOptions`/`ZeroAlloc.Validation` alignment its original
name promised (`features.md:1117` stays unchecked; recorded as its own open debt above rather than
implied done by this phase completing). Documentation and connectors were split out by design §0
and are not this phase's to schedule.

### Phase 4.3: Structured Logging Enrichment [status: complete]
**Goal:** Consistent scoped/structured logging across ingestion, retrieval, and answer generation.
**Backlog items:** Structured Logging Enrichment
**Plan:** `docs/plans/2026-08-05-observability-design.md` + `2026-08-05-observability-implementation.md`
(shared with Phase 4.4 — both roadmap entries were one-liners, and measuring the code moved both
at once, so they were planned and closed together).
**Completed:** 2026-08-05, PR #48 (`bf486b8c`).
**Retroactive note (added by this phase's own closing pass, Task 10 of 4.4, 2026-08-06):** this
entry and Phase 4.4's below did not exist when PR #48 merged — both this section and
`MILESTONE.md`'s phase list still read `[status: pending]` a day after the phase closed, with
no full write-up. Nothing enforces that a merged PR updates these two files, and nothing caught
the gap until this closing pass looked. Written now from the merged commit's own history rather
than from memory.
**The measurement that shrank the phase before any code was written:** the roadmap's one-liner
("Consistent scoped/structured logging") reads as one undone thing. Measured: 140
`[LoggerMessage]` source-generated declarations, 12 structured `ILogger.Log*` templates, and
**zero** plain string interpolation already existed — structured logging was ~92% done, with no
interpolation-cleanup pass to run. `BeginScope` appeared **zero** times — scoped logging did not
exist at all. The phase's first act was recording that the structured half was already true, so
nobody re-did it.
**Task 2 — scopes (one commit):** `PipelineIngestor.IngestAsync` gained a scope carrying
`document_id`; `PipelineRetriever.RetrieveAsync` and `RagPipeline.AskAsync`/`AskStreamingAsync`
each gained one carrying `query_hash`, wrapped around the call the scope exists to cover (`Pipeline.ExecuteAsync`
for ingest/retrieve; both the retrieval call and the answer-engine call for ask). `RagPipeline.RetrieveAsync`
deliberately gets no scope of its own — it is a pure pass-through to `PipelineRetriever.RetrieveAsync`,
which already opens the identical scope, and `RagPipeline` itself logs nothing that scope would
cover. `query_hash` reuses `PipelineRetriever.HashQuery` (widened from `private` to `internal`)
rather than computing a second hash — the raw query text never enters a scope, the same discipline
`query.hash` follows on the `ragnet.retrieve` span. No scope opens inside a hot loop (chunking,
embedding). Proven with `FakeLogger<T>`: a downstream log call made while the scope is active is
asserted to carry the expected key/value via `record.Scopes`. `Rag.NET.Tests` 1169 → 1172 (three
new scope tests).
**Task 3 — event-name standardisation (13 commits, one per package family):** every pre-existing
`[LoggerMessage]` declaration across the repository gained an explicit snake_case `EventName`
(`ingest_failed`, `mmr_candidate_count_less_than_top_k`, …) in place of the generator's PascalCase
method-name default. **The trap this phase's own plan recorded before writing any code, and
avoided in practice:** `EventId.Name` and `EventId.Id` are not independent — the source generator
derives the numeric id as a deterministic hash of the name string. Adding an `EventName` without
also pinning `EventId` would have silently renumbered every one of the 139 pre-existing event ids
in one commit; anyone filtering logs or alerts on a numeric id would have gone dark with no error
and no warning. Every touched declaration therefore carries an explicit
`EventId = <the value the generator already produced>` alongside its new `EventName` — verified,
not assumed, by rebuilding with `EmitCompilerGeneratedFiles` and diffing the generated
`EventId(...)` calls before and after each commit: **zero numeric id changes** across every
package. A repeated method name across several classes (`LogInjectionDetected` across 6 Security
classes, `LogCaptureFailed` across 7 Diagnostics classes) already shared one generator-computed id
before this phase — the generator's hash is a pure function of `EventId.Name`, independent of the
declaring class — and each group got one identical `EventName` and one identical pinned `EventId`,
preserving that pre-existing equivalence rather than inventing per-class ids that never existed.
`DataProvidersLog` (`Rag.NET.DataProviders`) resolves `[LoggerMessage]` through
`Microsoft.Gen.Logging` rather than the plain `Microsoft.Extensions.Logging.Generators` used
elsewhere — a transitive effect of `Microsoft.Extensions.Http.Resilience` — but that generator
hashes the same way, so the same pin-before-rename approach applied unchanged.
`PersistentConversationMemoryScoreScaleTests` (`Rag.NET.Memory`) needed updating: it hardcoded
the expected `EventId.Name` as a string constant to assert "exactly one warning" via a
`CountingLogger`, and that constant was asserting against the generator's old PascalCase default —
a genuine, intended consequence of the rename, not a regression.
**Not touched:** `MessageId`/named-property additions (`document_id`, `chunk_index`,
`vector_store`, `strategy`) the design doc noted `features.md` already promises in some log
messages — 26 `document_id` and 3 `chunk_index` placeholders already existed in
`RagPipelineLog`, and no core log statement names a specific vector store or synthesis strategy at
its call site, so inventing either value there would have meant fabricating data behind a tag
that looks real. Recorded, not done — the design's own instruction was to report this rather than
invent the missing field.
**Counts:** `Rag.NET.Tests` 1169 → **1172** (3 new scope tests). No other test project touched;
`RepoConventions` unaffected by this phase (37+1 skip, unchanged — the pinning tests this phase
depended on for verification were rebuild-and-diff checks run by hand, not new automated guards).
Full solution build: 0 warnings, 0 errors.

### Phase 4.4: OpenTelemetry Tracing & Metrics [status: complete]
**Goal:** First-class OTel wiring (exporter guidance, resource attributes, sample dashboards) on top of the existing RagTelemetry ActivitySource/Meter.
**Backlog items:** OpenTelemetry Tracing & Metrics
**Plan:** `docs/plans/2026-08-05-observability-design.md` + `2026-08-05-observability-implementation.md`
(shared with Phase 4.3, see above).
**Completed:** 2026-08-06, branch `feature/otel-tracing`.
**The measurement that set the phase's real scope:** the roadmap's one-liner undersold what
already existed — 8 spans and 11 instruments, covered by 13 telemetry tests — while overselling
what was wired: a repo-wide grep for `AddOpenTelemetry`, `AddSource(` and `AddMeter(` returned
**zero** matches outside two test projects pinning a transitive package version. The gap was
wiring and per-package coverage, not the instrumentation surface itself.
**The 2026-04-04 package-specific-spans deferral was overruled by the owner, on the deferral's own
terms.** It said spans should wait "until evidence demands it," written when the library was a
fraction of its current size. The evidence now exists: a user watching slow retrieval gets one
generic `ragnet.retrieve` span with a `vector_store` tag holding a type name, and cannot tell
whether the vector store, the reranker, or graph traversal is the cost. All nine packages the
deferral left untraced — the six vector stores, both rerankers, GraphRag, Raptor, Graph, and
Security — are instrumented in this phase.
**Task 4 (`dbe8ca8`) — the shared `ActivitySource`, first, because everything else depends on it.**
`RagTelemetry` is `internal` to `Rag.NET` core, so a satellite could not trace onto its source
without a `ProjectReference` — exactly the coupling Phase 4.7's package decomposition exists to
avoid. `src/Shared/RagTelemetrySource.cs` follows the repository's existing linked-file pattern
(`GraphErrorMapping.cs`, `BertWordPieceTokenization.cs`): it is **linked**, not referenced, into
core and every instrumented satellite, so each assembly compiles its own `ActivitySource`
instance sharing the name `"Rag.NET"`. OpenTelemetry matches a listener to a source by name, not
object identity, so one listener on `"Rag.NET"` hears all of them — proven by
`RagTelemetrySourceCrossAssemblyTests`, which reaches core's real `RagTelemetry.ActivitySource`
and `Rag.NET.Testing`'s own linked copy through a small public `TelemetryProbe` wrapper (the
internal type cannot be named directly from a third assembly without an `extern alias`, since two
linked copies share the identical namespace-qualified name).
**`ZeroAlloc.Telemetry` was evaluated and rejected — with evidence, not on convention.** The
org's own source-generated OTel library (`[Instrument]`/`[Trace]`/`[Count]`/`[Histogram]`, zero
transitive dependencies) was measured against this phase rather than assumed suitable. **It
cannot set span tags at all** — its entire API is four attributes, none parameter-targeted, and
of the 13 traced units examined, zero had their wanted tags settable by the generated proxy. Three
structural mismatches beyond that: its generated proxy is `internal sealed`, constructible only
from the assembly declaring the annotated interface — `IVectorStore` lives in
`Rag.NET.Abstractions`, so adopting it would have put a new dependency on the most foundational
assembly, inverting what Phase 4.7 achieved; GraphRAG and RAPTOR implement the generic
`IIngestionBehavior`/`IRetrievalBehavior` shared by ~30 implementers, so one `[Trace]` name would
cover all of them indistinguishably; Caching has no interface or class to annotate at all. **The
probe still paid for itself**, validating two assumptions this phase's design rests on: real
cross-assembly source-name sharing (proven before the linked-file mechanism was built), and
measured allocation at 200k calls, Release, no listener — bare `StartActivity` **72 B**, a
hand-written no-op decorator **144 B**, the generated proxy **144 B**. `StartActivity` allocates
**zero** when unobserved; the extra cost either approach pays is decorator-shaped, not
telemetry-shaped, so spans placed directly inside existing methods — this phase's approach — are
cheaper than any proxy.

**Revisited 2026-08-07 after ZeroAlloc.Telemetry v1.5.0, and declined again — for a different
reason.** The three issues filed off the back of the evaluation above
([#35](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/35),
[#36](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/36),
[#37](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/37)) all shipped, and they
worked: tag expressiveness went from ~7% to ~50% of this repository's **131** hand-written tags
across **48** spans in **30** files. (The first count taken was 68 spans and 175 tags — wrong,
because it swept in `obj/`, where the ZeroAlloc.Rest and Mediator generators emit their own
instrumentation. *An unfiltered grep is not a measurement either.*) What blocks adoption is no
longer tags but **span naming**: `[Trace("name")]` sits on the interface, so every implementation
shares one span name, and **not one traced type in this repository has an interface to itself**
(`IRetrievalBehavior` ~23 implementations, `IIngestionBehavior` ~16, `IVectorStore` ~10). Since
Phase 4.4 exists precisely to make Qdrant distinguishable from Weaviate, converting would undo its
central benefit — a cleanup that made traces less informative would be a regression in a costume.
The 21 hand-written `GetType().Name` tags are the visible workaround for that same gap. Raised
upstream as [#53](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/53); full
measurement in `docs/plans/2026-08-07-telemetry-conversion-assessment-design.md`. Note also that
the 144 B proxy figure above is **contradicted** by v1.5.0's published benchmark, which reports
parity at 72 B; the shapes differ (ours are `Task`-returning async interface methods, where
wrapping allocates a second state machine) and neither number should be trusted here without
re-measuring, which was not done because the blocker is architectural, not performance.
**Task 5 (`6904d2c`) — the span/tag convention, decided once before any package was
instrumented,** so nine packages did not produce nine conventions. Span names extend core's
two-segment `ragnet.<operation>` to `ragnet.<area>.<operation>` for satellites; packages sharing
an abstraction (the six vector stores, the two rerankers) share one area and one span name with
the backend as a *tag*, not a name suffix (`ragnet.vectorstore.search` + `vector.store`, not
`ragnet.qdrant.search`); packages with no shared abstraction (GraphRag, Raptor, Graph, Security)
get their own area. Tags are dotted, no exceptions. Documented explicitly what must never appear
in a tag: raw query text, document/chunk content, anything a Security guard removed or blocked
(a count and a classification only, never the matched substring), credentials and connection
strings.
**Task 6 (`a76e9bb7`) — `gen_ai.*` on the LLM surface, pinned to GenAI semconv v1.41.0.** The last
tag in `open-telemetry/semantic-conventions` before the `gen_ai.*` definitions moved to a
dedicated repository that has cut no release of its own to pin against instead. Every attribute
adopted is `Development`-stability in that revision and may be renamed upstream;
`gen_ai.provider.name` is used in place of the already-deprecated `gen_ai.system`. Nothing was
invented to fill a gap the spec does not cover: `source.count` and `synthesis.strategy` stay
`ragnet.*` because OTel's GenAI conventions have no concept of "how many retrieved sources fed the
prompt" — a plausible-looking invented `gen_ai.rag.*` name would misrepresent them as
standardised when they are not, worse than an honest proprietary name.
**Task 7 (`9eace303`) — the two snake_case tag outliers normalised**, `top_k` → `top.k` and
`vector_store` → `vector.store`, so the convention Task 5 wrote down was internally consistent
before nine satellites adopted it. Nothing published consumed either name, so no compatibility
shim was needed; the asserting tests (`RetrieveTelemetryTests`, `IngestTelemetryTests`) were
updated as a stated consequence.
**Task 8 (12 commits) — all nine packages instrumented**, one commit per package or shared-family
group: Qdrant, PgVector, Pinecone, Weaviate, Chroma, Azure AI Search (`ragnet.vectorstore.upsert`/
`search`/`delete`, tagged `vector.store` + `vectorstore.collection` + operation-specific counts;
Weaviate and Azure AI Search's hybrid overloads share the `search` span name with
`vectorstore.hybrid=true` distinguishing them, per Task 5's shared-abstraction rule); Onnx +
Cohere (`ragnet.rerank`, tagged `reranker.type` + `reranker.candidate.count`); Graph
(`ragnet.graph.cluster` around the clusterer, `ragnet.graph.pagerank` around `PageRank.Compute`);
GraphRag (`ragnet.graphrag.extract`, `.communities`, `.search` — local/global search share one
span name with `graphrag.search.mode` distinguishing them); Raptor (`ragnet.raptor.build` around
whole-tree construction, `ragnet.raptor.summarize` as its per-level child); Security
(`ragnet.security.sanitize` around every `IChunkSanitiser`, `ragnet.security.guard` around every
`IRetrievalGuard`, both count-and-classify only per Task 5's tag rule). Every span nests under the
core span modelling the step it participates in. **Three packages left deliberately
uninstrumented**, each recorded rather than silently skipped: `Rag.NET.Caching` (`UseCaching()`
only registers `HybridCache`; the hit/miss logic is core's); `RaptorRetrievalBehavior` (a no-op in
its default retrieval mode — a span there would read "ran, did nothing" on every trace);
`IQuerySanitiser` (`RegexQuerySanitiser`, `LlmQuerySanitiser`) — it runs before `ragnet.query`
opens, on the raw user question, so it has no core span to nest under, the same problem
`ragnet.query` itself was added to solve for `ragnet.retrieve`/`ragnet.ask`. **A real defect this
task caught, not invented by it** (`90afc456`): `TestProjectTierTests` flagged the new
`OnnxRerankerTelemetryTests`, which reads `RAGNET_ONNX_RERANK_MODEL`/`_VOCAB`, for not declaring
`<RequiresSecrets>` — without it, `nightly.yml` would never select the project and the env vars
would never be set, so the test would have skipped **silently, forever**, rather than running
whenever a real model becomes available in CI. Fixed by declaring the property, matching
`Rag.NET.Embeddings.Onnx.Tests`'s existing precedent. `Rag.NET.RepoConventions.Tests`: 42 passed,
1 skipped (was 1 failing).
**Task 9 (`fb183faa`) — `Rag.NET.Telemetry`, the one-call setup.** `AddRagNetInstrumentation()`
registers the shared `"Rag.NET"` `ActivitySource`, **both** meters, and `telemetry.distro.*`
resource attributes on the `OpenTelemetryBuilder` it returns for the caller to chain an exporter
onto. **Its whole reason to exist:** a consumer who hand-wires `.AddMeter("Rag.NET")` per the
docs' own quick-setup snippet silently misses every counter `Rag.NET.Evaluation`'s
`ShadowTelemetry` publishes under the second, previously-undocumented meter name
`"Rag.NET.Evaluation"` — `ShadowTelemetry` builds its own meter for the identical reason
`RagTelemetry` is internal (`Rag.NET.Evaluation` must not depend on core), and nothing before this
task named that second meter anywhere a consumer would read it. Proven with a real
`TracerProvider`/`MeterProvider` and in-memory exporters — both meter names reaching the exporter,
not merely a same-named `Meter` object existing. Package count **66 → 67**.
**Task 10 (this closing pass, `docs`/`test`/`planning` commits, 2026-08-06) — docs, one guard
test, and both phase closes.** `docs/reference/opentelemetry.md`'s metrics table had already
drifted once, exactly as Task 5's own commit predicted it might: it listed 8 of `RagTelemetry`'s
11 instruments, predating `ragnet.ratelimit.wait.duration`, `ragnet.llm.tokens`, and
`ragnet.llm.cost` — `docs/reference/features.md`'s own instrument list was correct throughout and
never drifted, confirming the defect was in one file, not the design. Fixed the table; documented
the satellite spans and their tags in a concrete reference table (and, while at it, corrected two
smaller inaccuracies the Task 5 convention doc's own worked examples carried since before Task 8
existed: `ragnet.rerank.<operation>` implied a per-operation suffix neither reranker emits — both
use the bare `ragnet.rerank` — and `ragnet.caching.lookup` named a span Caching was always going
to leave uninstrumented); documented `gen_ai.*` and its semconv pin (already present, left as
found); documented both meters and why `Rag.NET.Evaluation` is separate; documented the
`telemetry.distro.*` resource attributes and `AddRagNetInstrumentation()` as the recommended
one-call setup, with the manual two-meter hand-wire kept as the explicit alternative for a
consumer who wants to avoid the package dependency. Added
`RagTelemetryMetricsDocumentationTests` (`Rag.NET.RepoConventions.Tests`) asserting the doc
table's instrument names against `RagTelemetry.cs` directly, so this cannot silently drift a
second time — proven red first by deleting the `ragnet.llm.cost` row (failure:
`Defined in RagTelemetry but missing from the doc table: ragnet.llm.cost`), then reverted.
`RepoConventions` 42+1 → **44+1**.
**No sample Grafana dashboard shipped.** 4.4's own roadmap description promises "sample
dashboards," and Task 5's own commit already flagged that none existed. This environment has no
running Docker daemon, no Grafana, no Prometheus, and no `promtool` — nothing to import a
dashboard JSON into or run its PromQL against real exported `ragnet_*` series before committing
it. A dashboard built and never validated risks wrong Prometheus metric-name mangling (dots to
underscores, histogram `_bucket`/`_sum`/`_count` suffixes), invalid panel JSON that fails to
import, or PromQL that silently returns nothing — and it would carry the same authoritative look
as a validated one while being unverified. Recorded as debt rather than shipped: producing and
validating a sample dashboard needs a session with a real OTel/Prometheus/Grafana stack available
(for example, docker-compose alongside `Rag.NET.Sample` exporting to a local Prometheus and
importing the JSON via Grafana's API to confirm it renders), not routed to a numbered phase since
Milestone 4's remaining phases (4.2, 4.5, 4.6) do not touch telemetry.
**Counts:** `Rag.NET.Tests` 1179 → **1181** (`RagTelemetrySourceCrossAssemblyTests` plus GenAI tag
coverage added to `AskTelemetryTests`; `RetrieveTelemetryTests`/`IngestTelemetryTests` updated in
place for the Task 7 rename, not added to). `Rag.NET.RepoConventions.Tests` 37+1 skip → **44+1**
(Task 8's `TestProjectTierTests` fix plus Task 10's two new documentation-drift facts).
`Rag.NET.PackageValidation.Tests` **20**, unchanged in count (`ExpectedPackageCount` moved 66 →
67 inside the existing test, not a new one). `Rag.NET.Telemetry.Tests` **2** (new project,
Task 9). Each satellite's own `*.Telemetry.Tests` (Qdrant, PgVector, Pinecone, Weaviate, Chroma,
Azure AI Search, Onnx, Cohere, Graph, GraphRag, Raptor, Security) were proven at Task 8's own
commits against real containers/WireMock, per their commit messages; this closing pass did not
re-run the container-gated suites (no local Docker daemon in this session) and does not claim to
have re-verified them independently. Full solution build: 0 warnings, 0 errors (re-verified by
this closing pass, `--no-incremental`).

### Phase 4.5: Sample Applications [status: complete]
**Goal:** End-to-end runnable samples covering the main library scenarios — which turned out to
need the documentation site to build first, since a sample is only honest if it follows docs a
reader can actually reach.
**Backlog items:** Sample Applications
**Plan:** `docs/plans/2026-08-08-docs-site-and-samples-design.md` + `2026-08-08-docs-site-and-samples-implementation.md`
**Completed:** 2026-08-08 (**the documentation site did not build, for two independent reasons,
and nothing had ever reported it.** `@docusaurus/core` 3.7.0's `webpack: ^5.95.0` caret resolved
to 5.109.2, whose tightened `ProgressPlugin` schema rejects options Docusaurus itself still
passes — and there was no lockfile, so this arrived on any fresh `npm install`, not just this one.
Separately, 25 links across 7 pages pointed at paths that do not exist (`guide/*` and
`reference/*` prefixes dropped, one link into the unpublished `docs/plans/`). **No CI job had ever
built the site** — that is why neither defect was known, and why acquiring `docs.yml` mattered
more than fixing either one: a repair with no guard rots back to exactly this state. Fixed in
order: upgraded Docusaurus 3.7.0 → 3.10.2 (clears the `ProgressPlugin` schema mismatch), committed
`package-lock.json` (there was none — `npm ci` could not run at all before this), fixed all 25
links (`onBrokenLinks` left at its hard-fail default throughout — the one forbidden move this
phase's own plan named was weakening it to `'warn'`), then added `docs.yml` to build the site on
every pull request, modelled on `ci.yml`'s job shape and confirmed to fire on a normal PR trigger
rather than shipped inert. **Deployment is deliberately not part of it**: the site is configured
for `rag-net.github.io/Rag.NET/`, the repository is `MarcelRoozekrans/Rag.NET`, the `RAG-Net`
GitHub org exists but does not hold this repository, and GitHub Pages is enabled nowhere for it
(the Pages API answers 404) — `organizationName` was left visibly wrong rather than "fixed" to a
plausible-but-unverified value, a decision this phase does not have grounds to make. Closes the
`docs.yml` debt open since 2026-08-02 — see the closed "Three pieces of house furniture" entry
above, which also carries the correction this phase made to that entry's stale renovate note: the
Renovate GitHub App is enabled and has been opening PRs since 2026-08-05, not "still inert" as
Phase 4.8 last recorded it.

**Samples:** `samples/Rag.NET.Sample` was the only sample, for 69 packages. Added
`samples/Rag.NET.QuickStart`, driven by `Rag.NET.Hosting`'s `AddRagNetPipelineFromConfiguration`
rather than hand-registering `IChatClient`/`IEmbeddingGenerator`/`IVectorStore`, so it reuses
Phase 4.6's own startup validation instead of re-implementing it, and follows
`docs/getting-started.md`'s flow end to end (ingest, re-ingest with `Overwrite`, retrieve, ask, ask
streaming, delete). Defaults to a local Ollama endpoint and the in-memory store, needing no API
key; `RagNet__*` environment variables switch it to OpenAI. Registered in `Rag.NET.slnx`,
`IsPackable=false` like its sibling — `dotnet pack` still produces exactly 69 packages.
**Following the getting-started page while building this is what surfaced the documentation
defects below** — the phase's charter, and the finding worth more than the sample itself.

**Documentation defects found and fixed, all re-verified against the pinned packages before
editing, not taken on trust:**
1. `docs/getting-started.md` step 2 — `OpenAIClient.AsChatClient(...)`/`.AsEmbeddingGenerator(...)`
   do not exist on `Microsoft.Extensions.AI.OpenAI` 10.8.3 (CS1061). Fixed to the real chain,
   `.GetChatClient(model).AsIChatClient()` / `.GetEmbeddingClient(model).AsIEmbeddingGenerator()`
   — the pattern `Rag.NET.Hosting`, `Rag.NET.Cli`, `Rag.NET.Mcp.Tool` and `Rag.NET.Sample` already
   use.
2. `docs/getting-started.md` step 3 and `docs/guide/vector-stores.md`'s Azure AI Search example —
   `using Rag.NET.VectorStores.PgVector;` / `using Rag.NET.VectorStores.AzureAISearch;` name the
   **package id**, not the namespace (CS0234); the namespaces are `Rag.NET.PgVector` and
   `Rag.NET.AzureAISearch`. Both fixed; every other `using Rag.NET.*` line in `docs/` (excluding
   the unpublished `docs/plans/`) was checked against its package's actual namespace declarations
   in `src/` — `choosing-packages.md`'s `Rag.NET.DataProviders.SharePoint`/`Rag.NET.Qdrant` and
   every other occurrence already matched; no further instances of this defect exist.
3. **A third compile defect this phase found, not previously reported**: even with (1) and (2)
   fixed, `getting-started.md`'s step 1 package list (`Rag.NET`, a vector store, a parser) is
   missing `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.AI` and
   `Microsoft.Extensions.AI.OpenAI` — `Rag.NET` itself references only the `Abstractions` split of
   the AI package, so `ServiceCollection`, `AddChatClient`/`AddEmbeddingGenerator`, and any OpenAI
   client type are all unavailable without installing them explicitly, exactly as
   `samples/Rag.NET.Sample`'s own csproj already does. Verified by compiling the whole
   getting-started flow (steps 1 through 8, plus the SharePoint/Qdrant worked example in
   `choosing-packages.md`) against `ProjectReference`s to the real `src/` projects in a throwaway
   project — 0 errors after adding the three packages to step 1, and choosing-packages.md's own
   snippet compiled unmodified.
4. `docs/guide/choosing-packages.md` said "Rag.NET ships as 66 packages" — stale; the real count
   is **69** (`ExpectedPackageCount`, `Rag.NET.PackageValidation.Tests`, and an actual
   `dotnet pack` at this close). Phase 4.6 added `Rag.NET.Hosting` and `Rag.NET.Cli` after that
   line was written. The same stale count, found by the same sweep, appeared four more times in
   `docs/reference/ci.md` ("packs the 70 shippable packages", "70 `.nupkg` plus 70 `.snupkg`", "a
   push that dies partway through 70 packages", "none of the 70 IDs is reserved") — all describing
   `ci.yml`'s current behaviour in the present tense, not a dated historical record, so all four
   corrected to 69 as well. (`docs/planning/ROADMAP.md`'s own historical "70 packages, at the
   time" notes inside already-closed phase entries were left alone — those are dated records of
   what was true then, not live claims.)
5. `docs/reference/ci.md`'s Renovate section carried the same stale "inert until the Renovate
   GitHub App is enabled" claim as the ROADMAP debt entry above — corrected with the same
   evidence (five `renovate/*` branches, PRs merged since 2026-08-05).

No further instances of `.AsChatClient(`/`.AsEmbeddingGenerator(` exist anywhere under `docs/`
outside `docs/plans/` (which is not part of the published site — Task 2 of this phase's plan
established that a link into it can never resolve, for the same reason its code is not verified
here). `npm run build` re-run after every documentation edit in this phase: still succeeds, 0
broken links, the only warning being the pre-existing `onBrokenMarkdownLinks` deprecation notice
(recorded as a debt below).

**`Rag.NET.Security.AspNetCore` off `VerifiedBy: none`** (`398c595f`) — see the closed "Two
packages have never been exercised by any test at all" debt entry above for the full account.
Unlike Phase 4.6's `Rag.NET.Mcp.Tool`, running the package's tests found **no production defect**:
the two types do what their names say. **This was the last package at `none`** — the release gate
this milestone's Definition of Done has required since Phase 4.0 is now genuinely satisfied, and
the DoD box above is ticked on that verified evidence, not assumed.

**New debts recorded, all with origin and no owning phase yet — see the follow-up debts list at
the top of this file for the full entries:** nothing compiles documentation code snippets, so two
of getting-started's six numbered steps were broken and every existing guard (including this
phase's own `docs.yml`) passed anyway; `npm audit` reports 25 vulnerabilities (6 moderate, 19
high) in the site's build-time tooling, measured after the Docusaurus upgrade; `docs/index.md`'s
Quick Links point at `docs/plans/` as inline code rather than a Markdown link, so no link checker
catches that it resolves to nothing published; and `onBrokenMarkdownLinks` is a deprecated config
key (`markdown.hooks.onBrokenMarkdownLinks` is its replacement), a cosmetic warning on every build.

**Counts:** `Rag.NET.Tests` **1180**, unchanged (this phase touched documentation and one new test
project, not `Rag.NET.Tests` itself). `Rag.NET.RepoConventions.Tests` **48 + 1 skip → 49 passing, 0
skipped** (the `Rag.NET.Security.AspNetCore` closure — confirmed by an independent re-run at this
close, not carried over from the commit message). `Rag.NET.PackageValidation.Tests` **20**,
unchanged in count; **69 packages** produced by a real `dotnet pack` at this close (69 `.nupkg`,
69 `.snupkg`, counted directly), matching `ExpectedPackageCount`.
**Full solution build re-run at this close:** `dotnet build Rag.NET.slnx -c Release
--no-incremental` — 0 warnings, 0 errors. `npm run build` — succeeds. Docker/secrets/LLM-gated
test tiers were **not** re-run in this session (no local Docker daemon confirmed, no secrets
configured) — the milestone DoD's "All test projects passing" box stays open for that reason,
recorded rather than assumed clean, and "Full solution builds 0 warnings / 0 errors" is left open
on the same conservative basis even though this phase's own `--no-incremental` run came back
clean: a milestone-close verification deserves its own dedicated pass, not a side effect of a
documentation phase's rebuild.

**Closes:** the `docs.yml` half of the "Three pieces of house furniture" debt (the whole entry now
closed, above); the `Rag.NET.Security.AspNetCore` half of the "two packages never exercised" debt
(the whole entry now closed, above); Milestone 4's "No package declares `VerifiedBy=none`" DoD box
(ticked above, verified); Milestone 4's "All planned phases complete" DoD box (ticked above,
resynced — this was the 13th and last of 4.0–4.12). **Opens:** four new debts, listed above and in
the follow-up debts list, none silently absorbed.)

### Phase 4.6: Rag.NET CLI Tool [status: complete]
**Goal:** `dotnet tool` for ingest/query/evaluate against a configured pipeline.
**Backlog items:** Rag.NET CLI Tool
**Plan:** `docs/plans/2026-08-08-executable-configuration-design.md` + `2026-08-08-executable-configuration-implementation.md`
**Completed:** 2026-08-08 (**scoped as "build a CLI"; became "make an executable configurable" the
moment the repository's one existing `dotnet tool` was measured before writing a second one.**
`Rag.NET.Mcp.Tool` could not work as published, in three separate ways, all hidden behind
`<VerifiedBy>none</VerifiedBy>`: **(1) no pipeline** — `RagMcpTools(IRagPipeline)` needs one from
DI, `AddRagNetMcpServer()` never registered one, and `IConfiguration` appeared nowhere in either
project, so every MCP tool call failed; **(2) no transport** —
`McpServerBuilder.WithStdioTransport()`/`WithHttpTransport()` set fields on an
`McpTransportOptions` singleton nothing read: HTTP threw at `app.MapMcp()`, and stdio — the
default, and what every MCP client uses — silently started a bare Kestrel web server instead of
speaking MCP; **(3) logging to stdout**, the exact channel MCP JSON-RPC travels on, so even a
fixed transport would have had its protocol stream corrupted by log lines. `WithApiKey()` was
equally fake — nothing read it either. **All three were found by running the tool**, which nobody
had ever done — the ledger was doing its job by recording an honest `none`; nothing was reading
the record. That is this phase's transferable lesson, not the three bugs individually.

Three decisions collapse the provider matrix rather than widening it (design §1): one
OpenAI-compatible client (`Microsoft.Extensions.AI.OpenAI`) covers OpenAI, Azure OpenAI,
OpenRouter, Ollama and LM Studio; a bounded three-kind vector-store set (`InMemory`, `Qdrant`,
`PgVector`) with real fixtures in this repository; anything else is served by hosting
`Rag.NET.Mcp` directly, stated as the designed answer rather than an apology (§1.3).

**Task 1 (`802ef0ea`) adds `Rag.NET.Hosting`, a new, deliberately non-packable seam package** —
`AddRagNetPipelineFromConfiguration(IServiceCollection, IConfiguration)` binds a `RagNet` section
(`ChatClient`, `Embeddings`, `VectorStore`; `VectorDimensions` sits under `Embeddings`, not the
store, because it is a property of the embedding model every store must agree with) and registers
a full `IRagPipeline`. **Not in `Rag.NET.Mcp`** — that package is what a user references to host
MCP tools in their own application (design §1.3's path), and putting Qdrant, PgVector and
`Microsoft.Extensions.AI.OpenAI` into it would force those on every such user, the 19 MB mistake
in miniature and the exact thing Phase 4.7 existed to undo. `Rag.NET.Mcp.Tool` references
`Rag.NET.Hosting`; so does the CLI; `Rag.NET.Mcp` does not. Ships at `VerifiedBy: unit` with tests
in the same commit — a new package arriving at `none` is what this phase exists to stop repeating.

**Task 2 (`8cdba242`)** makes a misconfigured tool fail while the host is being built rather than
the first time an MCP client invokes a tool and hits an unresolvable-service error: every
validation message names both the setting and the `RagNet:…` key that fixes it (an unknown
`VectorStore.Kind`, missing kind-specific settings, a missing chat/embeddings endpoint or model,
an absent/non-positive `VectorDimensions`), aggregated into one exception rather than a
one-error-at-a-time loop. What cannot be validated — whether the configured `VectorDimensions`
matches what the endpoint actually returns — is documented rather than faked; that mismatch
surfaces as a vector-store failure at first ingest.

**Task 3 (`29591b5d`)** makes the `InMemory` default loud: resolving `IVectorStore` for an
explicit or defaulted `InMemory` kind logs a warning naming the consequence (every ingested
document is lost when the process exits) and the fix. The guard against repeating exactly the
silence `UseCostBudgeting()`'s in-memory cost ledger already cost this repository once, with real
money behind it.

**Task 4 (`8762c181`) moves `Rag.NET.Mcp.Tool` off `VerifiedBy: none`** — see the debts list
above for the ledger arithmetic. What `VerifiedBy: unit` does **not** cover, stated rather than
implied: choosing between the stdio and HTTP hosts, and actually running either one — launching a
process or a Kestrel listener is not a unit-testable computation.

**Task 5 (`11dc7810`) wires `Program.cs` to the seam** and deletes the impossible header comment
("Edit this file after install") — it cannot be followed; the file is compiled into the installed
tool. Replaced with what is true now: configure via `appsettings.json` or environment variables,
host `Rag.NET.Mcp` yourself for providers outside the bounded set. A sample `appsettings.json`
ships alongside the installed binaries, confirmed by unzipping the packed `.nupkg` rather than
assumed — Phase 4.7's own "intent and artefact differ" lesson, applied again. **Running the built
tool for the first time — this task, not a separate audit — is what found Task 5a.**

**Task 5a (`9dd5b4e6`, a `BREAKING CHANGE`, not in the original plan) makes transport registration
real.** `AddRagNetMcpServer()` now returns the SDK's own `IMcpServerBuilder` as
`McpServerBuilder.Server`; `WithStdioTransport()` calls the SDK's `WithStdioServerTransport()`
directly (available through `Rag.NET.Mcp`'s existing `ModelContextProtocol` reference), while the
real `WithHttpTransport()` — living in `ModelContextProtocol.AspNetCore`, which `Rag.NET.Mcp`
deliberately does not reference — is called by the consumer through `.Server` instead of through a
wrapper that package cannot honestly provide. The broken no-op methods are deleted outright rather
than left as a silent trap, meeting the plan's non-negotiable: **a transport method that silently
does nothing must become impossible — met by making it a compile error** for any caller still
relying on them. `Program.cs`'s stdio path also moved off `WebApplication` (which was starting
Kestrel even for stdio) onto a plain `Host.CreateApplicationBuilder` host, with console logging
redirected to stderr. Verified by running the built `ragnet-mcp.exe` directly: stdio produced
exactly one JSON-RPC response line on stdout with all logging on stderr and no port bound; HTTP's
`/mcp` returned 200, and the `X-Api-Key` middleware genuinely rejected/accepted requests. Tests
assert what the SDK registered into `IServiceCollection`, not the package's own flag — asserting
the flag is exactly what let the original bug ship undetected.

**Task 6 (`55da2827`) decided the package shape** — recorded in full above, in this same debts
list: re-measured at **4.97 MB, 55 entries**, 2.7× the pre-repair 1.87 MB, and judged intended.

**Task 7 (`2c2e6d61`) builds `Rag.NET.Cli`, the second consumer of the seam, not a second
scaffold**: `ragnet ingest <path> [--overwrite]` and `ragnet query "<question>" [--top-k N]`
against a pipeline built by `AddRagNetPipelineFromConfiguration` — the same configuration, the
same startup validation, the same `InMemory` warning. Command handlers (`CliArguments`,
`IngestCommand`, `QueryCommand`) are `internal` types reachable from `Rag.NET.Cli.Tests` via
`InternalsVisibleTo`, pure computations over an already-resolved `IRagPipeline` — the same shape
`Rag.NET.Mcp.Tool`'s `ProgramArguments` established, for the same reason: a top-level program's
local functions compile `private` onto its generated `Program` class and are unreachable from any
test assembly. Ships at `VerifiedBy: unit` with 35 tests **landing in this same commit**, not
added later — the arriving-at-`none` mistake this phase exists to stop repeating. `Rag.NET.Cli` is
plain `Microsoft.NET.Sdk`, not `.Sdk.Web`: it never accepts an inbound connection, so there is no
reason to carry the ASP.NET Core shared framework. Added to `Rag.NET.slnx`, confirmed present.
`ragnet evaluate` prints its deferral reason to stderr and exits non-zero rather than half-working
— see the new debt below. The published package total moved **66 → 67**
(`ExpectedPackageCount`, `Rag.NET.PackageValidation.Tests`); `Rag.NET.Hosting` is deliberately
**not** packable — an internal wiring seam, referenced by `ProjectReference`, never published.

**Two traps recorded for whoever next copies this phase's pattern, neither a defect in what
shipped:** `Host.CreateApplicationBuilder(args)` throws on positional arguments — the default
command-line configuration provider expects `--key value` pairs, so `Rag.NET.Cli`'s `Program.cs`
deliberately builds with `HostApplicationBuilderSettings` and `Args` left unset, keeping its own
bare-path/unquoted-question arguments out of that provider entirely while appsettings.json and
environment variables still layer in normally. And ErrorProne.NET's EPC13 flags any type named
`Result`, regardless of namespace — not just `Rag.NET`'s own `Result<T, TError>` — which is why
`IngestCommand`/`QueryCommand` return an `Outcome`, not a `Result`, a repo-wide gotcha rather than
a local naming choice.

**Counts:** `Rag.NET.Hosting.Tests` **27**, `Rag.NET.Mcp.Tests` **11** (Task 5a rewrote these to
assert what the SDK registered, not the package's own flag), `Rag.NET.Mcp.Tool.Tests` **16**,
`Rag.NET.Cli.Tests` **35** — four new or newly-tested projects; `Rag.NET.Tests` **1180** and
`Rag.NET.RepoConventions.Tests` **48 + 1 skip**, both unchanged baselines throughout.

**Closes:** the `Rag.NET.Mcp.Tool` half of the "two packages never exercised" debt (only
`Rag.NET.Security.AspNetCore` → 4.5 remains open, both above) and the `Rag.NET.Mcp.Tool` package
shape debt (decided at Task 6, above). **Opens, and left open with a stated reason:** `ragnet
evaluate` needs a dataset file format that does not exist anywhere in this repository yet, and
`Rag.NET.Cli`/`Rag.NET.Mcp.Tool`'s `unit` coverage does not reach host selection or process-level
behaviour — both recorded above, neither faked closed here. **No Definition-of-Done box is ticked
by this entry**: Milestone 4's "All planned phases complete" stays open (4.5 remains pending) and
"No package declares `VerifiedBy=none`" stays open (`Rag.NET.Security.AspNetCore`) — both updated
above to say so rather than left stale.)

## Milestone 5: Evaluation Depth [status: complete — audited and archived 2026-08-15, docs/plans/2026-08-15-milestone-5-audit.md, verdict PASS on all five criteria]
**Goal:** Extend the evaluation programme along the axes Milestone 3 deliberately did not take:
what each library **costs** rather than what it scores, multi-hop retrieval, graded relevance and
the datasets declined at Milestone 3's close, and the two IR metrics `IrMetrics` does not compute.
Milestone 4 remains the active milestone and its phases are unchanged; nothing here starts before
its work needs to yield.

**Definition of Done** (written in the falsifiable style Phase 4.0 established for Milestone 4 —
every criterion below can be false, and something checks it — not the older "all phases complete"
shape, though that box is still here doing its share):
- [x] Phases 5.1–5.4 complete, sub-phases included (5.5 deliberately schedules nothing and is
      outside this box by design — see its entry)
      *(Was ticked 2026-08-15, when 5.2's comparative run closed the last open phase. It answered
      "does GraphRAG help" with **no** — nDCG@10 0.56897 against 0.59658 for the candidate-set
      control, **−0.02761** — and a finding is a completion: this box asks whether the question was
      asked, not which way it came out. **Re-opened the same day, deliberately, when Phase 5.2.1
      was added inside the milestone** to act on that run — #232, #174, #226 — rather than carry
      them into Milestone 6. A ticked box over a pending phase in its own range would be the drift
      this milestone's file warns about at its top. **5.2.1 closed 2026-08-15** with #174, the last of its three PRs — #232 measured, #226 done, #174 done — and the box stays open past that for **5.2.2**, added the same day at the operator's request to score answers rather than rankings — **re-ticked the same day, with 5.2.2's close**: every phase in the range is complete, sub-phases included.)*
- [x] **No cross-ecosystem latency figure is published without the confound statement beside
      it**: the results page states the mechanism that made in-process .NET and subprocess Python
      rows comparable, or publishes them per-ecosystem labelled non-comparable. A latency number
      on a page without that statement fails this criterion; the check is reading the page — the
      same check that held the `+BM25 hybrid` row to its label.
      *(Met 2026-08-10. The published latency table is cross-ecosystem and carries the
      default-in-memory-store caveat inline plus the startup exclusion; indexing publishes as two
      per-ecosystem tables labelled non-comparable, with the reason — the Python spans include each
      library's chunker and are warmed by an untimed rehearsal — stated between them.)*
- [x] **`IrMetrics`' graded gain has scored a real dataset**: at least one dataset whose qrels
      carry a grade above 1 has been through `Evaluate`, and the FiQA-qrels contradiction
      (`IrMetrics.cs:31-32` against the TREC-COVID debt entry) is settled by reading the cached
      `qrels/test.tsv`, with the losing sentence corrected. Fixture-only exercise of `2^rel − 1`
      fails this criterion, exactly as it has since Phase 3.7.
      *(Met 2026-08-12 by TREC-COVID: 10,456 qrels rows at grade 1 and 14,217 at grade 2, counted
      from the downloaded archive, through `Evaluate` to nDCG@10 = 0.45427. The contradiction
      settled against FiQA — all 17,110 of its judgements are exactly 1 — which is why this needed a
      new dataset rather than a new run.)*
- [x] **Every dataset this milestone lands carries the full Milestone 3 per-dataset checklist**
      — descriptor, `BeirRunBudget` timing (the budget table throws on an untimed dataset, so
      that half checks itself), a revision-pinned published reference where one exists, a licence
      determination from upstream rather than a mirror, and every published figure pinned in
      `BeirReproduction` at ±0.005 on the fast tier. A figure re-checked by nothing fails this
      criterion — Milestone 3's close declined TREC-COVID and EnronQA precisely because none of
      this existed for them, so landing them without it would repeat the decline's grounds as
      defects.
      *(Met for both datasets landed so far, 2026-08-12. TREC-COVID: counts from the archive,
      timing measured across all ten protocols, reference pinned to MTEB's `ndcg_at_10` 0.47232 at
      revision `bb9466ba`, licence read from `allenai/cord19` and NIST directly, figure pinned at
      ±0.005. MultiHop-RAG: counts measured from the pinned revision rather than taken from the
      paper, timing measured, licence `odc-by` read from the authors' own repository, figure pinned
      at 0.63967 — and **"where one exists" is doing real work in its row**: there is no published
      reference for `all-MiniLM-L6-v2` at nDCG@10 on this dataset, so the target holds `NaN`,
      recorded as a determination and not as an omission.)*
- [x] All test projects passing; solution builds 0 warnings / 0 errors from a clean restore
      — re-run at close. *(Met 2026-08-15 by the close audit,
      `docs/plans/2026-08-15-milestone-5-audit.md`: every `bin/` and `obj/` deleted, restore
      forced, `dotnet build -c Release` 0 warnings / 0 errors, all 73 suites green on `main` at
      `32fa597f`. The first pass reported twelve failures — Docker Desktop not running, and a stale
      `artifacts/packages` — both recorded there as the artefacts they were, and re-run green.)*

> **Where this milestone comes from (2026-08-03).** An external handover document ("RAG.net —
> Evaluation & Benchmarking Handover") proposed an evaluation programme. **Most of it Milestone 3
> had already delivered**, and the remainder was assessed against the repository on 2026-08-03;
> this milestone captures only what is genuinely open, plus the corrections a future reader of
> that document needs so nobody re-derives the assessment. Already delivered, and **not
> re-scheduled here**: BEIR parity (SciFact 0.64593, FiQA 0.37086, ArguAna 0.50432 — all within
> 0.003 of MTEB's published figures, pinned in `BeirReproduction` at ±0.005); the four-row
> ablation table across three datasets; native IR metrics with no `pytrec_eval` dependency; the
> qrels parser; metric validation against published figures; frozen dataset versions and recorded
> configuration; a pinned LLM judge with prompt-versioned cached judgments; results as committed
> markdown.

> **Three corrections to that handover, recorded rather than silently dropped — each verified
> against the tree on 2026-08-03, so the next person reading it beside this roadmap starts from
> the assessment instead of re-deriving it.**
>
> **1. It asserts three components exist. None does, and none is planned anywhere:**
> - **LanceDB** — no `src/*LanceDB*`, and no match for the string anywhere in the repository.
>   The vector stores are Qdrant, Weaviate, PgVector, Chroma, Pinecone and AzureAISearch.
> - **Ollama embeddings** — the only embedding package is `Rag.NET.Embeddings.Onnx`. Ollama
>   appears as a Testcontainers fixture (`tests/Rag.NET.Testing/OllamaFixture.cs`, consumed by
>   `Rag.NET.E2ETests`) and as an `IChatClient` example in the docs — never as an embedding
>   generator.
> - **OpenRouter routing** — the only `src/` match is
>   `Rag.NET.Benchmarks.Quality/HypotheticalModelIdentity.cs`, added by Phase 3.15 as the HyDE
>   cache identity for the hypotheticals generation tool; OpenRouter otherwise appears as the
>   optional test chat-client backend in `Rag.NET.Testing`. A consumer of OpenRouter's endpoint
>   in two test-adjacent places, not a routing feature.
>
> **Assessed and neither built nor planned — and deliberately not scheduled here**: nobody has
> asked for them. They were asserted, not requested, and scheduling a component because a
> document claimed it already exists would be backwards.
>
> **2. One of its validation criteria is measured false, and using it would fail a correct
> implementation.** The handover states HyDE should help on FiQA and hurt on ArguAna. Phase 3.15
> measured the opposite half-and-half: ArguAna **−0.0014** (correct — the negative control held),
> but FiQA **−0.0054** — **no lift on the named positive control** — while **SciFact took the
> large gain at +0.0541**, which nobody, the handover included, predicted. This matters beyond
> bookkeeping: anyone validating a HyDE implementation against "expect + on FiQA" would conclude
> a *correct* implementation is broken. The measured nine-cell table in
> `docs/reference/retrieval-quality.md` is the acceptance criterion now, not the handover's
> prediction.
>
> **3. Its "no Python dependencies anywhere" constraint holds for the library, not for the
> harness — and it treats that constraint as load-bearing for the pitch, so the distinction must
> be precise.** Phase 3.14 committed `benchmarks/library-comparison-python/` with a `uv.lock`
> and LangChain/LlamaIndex/Haystack entrants — approved deliberately when the comparison scope
> was chosen. **No shipped package has a Python dependency**, so the claim survives for
> everything a user installs; the harness half does not, and repeating the constraint
> unqualified would be false.

### Phase 5.1: Library Performance Comparison [status: complete 2026-08-10 — full matrix gated and published]
**Goal:** Compare **cost** across the Phase 3.14 comparators — indexing throughput (docs/sec),
query latency p50/p99, allocations per query, Native AOT startup time, RSS. (Not a features.md
row — the only item the handover proposes that Phase 3.14 did not touch; it calls this table
"the one nobody else has".)

3.14 compared retrieval *quality* at defaults and published five rows nobody can attack on
configuration; nobody has published what those five stacks **cost**. The comparators are the ones
3.14 already wired — Semantic Kernel in-process, and the pinned Python harness for LangChain,
LlamaIndex and Haystack — and **3.14's infrastructure is reusable**: the TREC run-file boundary,
`BeirRunBudget`, the pinned embedder at its pinned revision and
`docs/reference/library-comparison-defaults.md` all exist, so this phase is measurement rather
than infrastructure.

**The known hazard, recorded before any number exists: mixing in-process .NET and subprocess
Python measurement is a latency confound.** Process boundaries, interpreter startup and
serialization land in the Python rows and nothing in the .NET rows pays them. 3.14's design
deliberately **withheld** cross-ecosystem latency for exactly that reason — so this phase must
state how it handles what 3.14 refused to publish, before publishing rather than after: either a
mechanism that genuinely removes the boundary from the measurement (each ecosystem timed
in-process on its own side of the run-file boundary, the boundary excluded), or per-ecosystem
tables labelled non-comparable, the way the `+BM25 hybrid` row is labelled internal. Publishing
quietly what 3.14 refused to publish is the failure mode; the DoD's confound criterion holds it.

**2026-08-09 — the measurement half landed; nothing is published.** Design:
`docs/plans/2026-08-09-library-cost-comparison-design.md`. Plan:
`docs/plans/2026-08-09-library-cost-comparison-implementation.md`.

The hazard above dissolves once measured properly, and the reason is architectural: **there is no
subprocess in the measured path.** Python entrants are run out of band and emit a run file and
nothing else; the .NET side reads that file back. 3.14's boundary is a *file*, not a pipe. So each
ecosystem now times itself in-process on its own side of it and emits a **timings sidecar**
(`<run-file>.timings.json`) carrying raw per-query latencies — never its own percentiles, because
an entrant reporting its own p99 would let five definitions of "p99" into one table.
`LatencyStatistics` computes them once, nearest-rank, for everyone. A guard scans `benchmarks/`
and the BEIR integration tests and fails any file that both times and launches a subprocess — the
design's central claim, checked rather than asserted. A **Python-written format fixture** is read
back by the .NET reader in a test, because the two sides disagreeing about the format is the risk
nothing else would have caught.

**What was left explicitly unbuilt: the table.** The design's §6 asks for an explicit choice
between a cross-ecosystem latency table and per-ecosystem tables labelled non-comparable, and says
in as many words that it must not be inherited from a design document. Everything above is
required identically under both, so it was built; the table was not, and merging the design was
not treated as making the choice.

Three findings, each of which the decision should weigh:

- **The old `elapsed` was not a latency measurement.** One `time.monotonic()` span covering
  `entrant.build`, the whole query loop, the run-file write *and* the self-line re-read of the
  file's bytes — printed, never emitted as data. It conflated indexing with query latency. Any
  figure derived from it would have been wrong in a way no test could have shown.
- **Indexing is measured with the embedding cache warm**, so it is index construction with
  embedding already paid for — not "the cost of indexing", which is mostly embedding. Defensible,
  and the only thing measurable consistently across entrants sharing one pinned embedder, but the
  sidecar now carries the cache hit/miss counts so a cold run is visibly different *in the data*
  rather than in a caveat someone might write later.
- **A second confound the design did not identify, and the more serious one: the indexing spans do
  not bracket the same work.** `entrant.build` includes each Python library's own chunker, while
  the .NET rows receive their units pre-built — `BeirHarness.OneChunkPerDocument(...)` is called by
  the *caller* and passed in, so it sits outside the span. That asymmetry is not incidental: it is
  the parity protocol 3.14 chose so that *quality* would be comparable. The consequence is that
  **a cross-ecosystem indexing row would partly measure a protocol difference rather than a
  library one**, while the per-query latency rows — both bracketing the retrieval call and
  excluding pooling — are clean. So the two rows are not equally publishable, and §6's decision
  may reasonably differ between them.

**§6 decided 2026-08-09: split by row.** Latency (p50/p99) publishes as a **cross-ecosystem**
table, because those spans genuinely bracket the same work with the boundary excluded. Indexing
publishes **per ecosystem, labelled non-comparable**, the way the `+BM25 hybrid` row is labelled
internal — because of the third finding above. The decision follows the evidence per row rather
than applying one rule to both, which is what the third finding made possible; before it, the
choice looked like a single call about the whole table.

**2026-08-09, later the same day — the first real run found the measurement was fiction, and the
third finding above is retracted.**

The harness was run for the first time. **Every timed span contained disk I/O**: both embedding
caches read one file per text (`VectorCache.try_read` → `path.read_bytes()`;
`EmbeddingCache.File.ReadAllBytes`), ~5,500 reads inside the indexing span. Identical runs
therefore differed by **23×** on OS page-cache state alone — LangChain SciFact indexing measured
**55.2 s** cold and **2.4 s** hot, same corpus, same code, same 5,505 hits / 0 misses.

**This retracts the "LlamaIndex indexes 14× faster than LangChain" reading recorded above.** It
was an artefact of run order: LangChain ran first against a cold page cache, LlamaIndex third
against a hot one. After the fix the ordering **reverses** — LangChain 0.86–0.87 s, LlamaIndex
2.30–3.70 s. No library conclusion survived from the pre-fix numbers, and none should have been
drawn from a single run.

Fixed by prefetching every vector the run needs into memory **before any clock starts**, on both
sides, with a cold cache failing loudly rather than quietly paying costs no other run paid.
Verified by the experiment that matters: LangChain indexed 2.1 s, a full `--no-incremental`
Release rebuild churned the page cache, and it then indexed **1.3 s** — the perturbation that was
previously worth 23× is now worth nothing.

**The guard that would have caught it: `CostReproducibility`.** No cost figure may come from a
single run. Two or more repeats, a hard failure above **3×** on indexing seconds or p50, and the
spread is **always reported even when it passes** — an arbitrary threshold that quietly passes at
1.9× is the kind that gets ignored, so the visible spread is the real protection and the bar is a
backstop. The bar is set from measured numbers: 1.4× honest back-to-back jitter, 2.2× when
deliberately disturbed, 23× for the defect. **p99 is reported but deliberately not gated** — at
these sample counts it rides on one to three tail samples, and two healthy LlamaIndex runs
differed 2.6× from tail noise alone.

Two further defects surfaced while running it:

- **The LlamaIndex entrant was broken on `main`** and nothing could have reported it. nltk 3.10.1
  began blocking any nltk-initiated import resolving under the working directory, and `.venv`
  lives under the project directory, so the entrant failed for any normal invocation. A dependency
  update had silently removed one of the five comparators; these runs are manual and gated, so no
  check could fail. `run_entrant.py` now bootstraps its own `sys.path` and a neutral cwd.
- **The gate could only judge the Python rows.** Both .NET entrants called the non-indexed
  `TimingsSidecar.Write`, so a second run overwrote the first and there was nothing to compare —
  a guard covering half the data while reading as coverage. `RAGNET_BEIR_RUN_INDEX` fixes it, and
  `RepoConventions`' `EveryGateVariableIsSatisfiableSomewhereWrittenDown` caught the new variable
  being undocumented within one test run.

**First gated measurement — SciFact, two runs per entrant, all five passing.** Spreads shown
because the point is that they are shown:

| Entrant | Indexing | p50 | p99 |
|---|---|---|---|
| `ragnet-control` | 0.02–0.03 s (×1.71) | 5.6–9.5 ms | 9.5–10.7 ms |
| `semantic-kernel` | 0.04–0.05 s (×1.14) | 2.6–3.1 ms | 3.5–4.5 ms |
| `langchain` | 0.86–0.87 s (×1.01) | 96.3–109.2 ms | 140.3–163.9 ms |
| `llamaindex` | 2.30–3.70 s (×1.61) | 90.7–102.8 ms | 186.2–193.7 ms |
| `haystack` | 1.15–1.21 s (×1.06) | 104.5–108.1 ms | 181.0–215.0 ms |

**The caveat that must travel with the latency column, or it misleads.** The ~20× gap is a
comparison of **default in-memory stores**, and for the Python entrants the default is a
reference implementation nobody runs in production — LangChain's `InMemoryVectorStore` and
LlamaIndex's `SimpleVectorStore` scan candidates in Python-level loops. *"LangChain is 20× slower"*
is false; *"LangChain's default in-memory store is 20× slower than Rag.NET's"* is what was
measured. 3.14's "at their defaults" protocol is what makes the row meaningful and is also what
makes the unqualified claim wrong, so the qualification belongs in the table, not a footnote.

**Still not published, and now for a second reason.** Only SciFact is measured; ArguAna and FiQA
have no repeat runs. And every figure above comes from a shared machine that was in use during
the runs — the gate makes that visible rather than fatal, but §2.2 requires one machine in one
session, so the page must say which machine and that it was not idle.

This is the first cross-ecosystem cost figure this repository will publish, and it is exactly what
3.14 declined to publish — so the latency table must carry, on the page, what §2.2 already states:
that interpreter and runtime startup are excluded by construction, that allocations-per-query and
AOT startup are .NET-only and publish as an internal table, and that every row comes from one
machine in one session with the caches warm. **Remaining work is publication only**; the
measurement, the percentile definition and the boundary guard all landed.

**2026-08-10 — the full matrix ran on an idle machine, and both blockers above are cleared.**
Twelve cells (SciFact and ArguAna × five entrants, FiQA × the two .NET entrants), three repeat runs
each, round-major with the entrant order rotating so no entrant is always the coldest or the
warmest. All 36 sidecars came through `CostReproducibility`; spreads were ×1.02–×1.17 on all but
two, against a ×3 bar. **Published** to
[the comparison page](../reference/library-comparison.md#cost-retrieval-latency-and-index-construction):
latency cross-ecosystem, indexing per ecosystem, every figure a range.

The idle machine visibly mattered — the SciFact figures above, taken while the machine was in use,
were **3–5× slower and far less stable** than the same cells measured idle (control p50
5.6–9.5 ms → 1.5–1.7 ms; LangChain 96.3–109.2 ms → 54.0–56.1 ms; control indexing ×1.71 → ×1.34).
The table above is therefore superseded, not merely extended, and §2.2's "one machine, one session"
rule is doing real work rather than ceremony.

**The run was blocked by a defect the benchmark was the only thing that could find.** The Semantic
Kernel entrant failed all three rounds with `TypeLoadException` on
`Microsoft.Extensions.VectorData.VectorSearchFilter` while every other entrant ran. Renovate's #119
had moved `Microsoft.SemanticKernel` 1.78.0 → 1.79.0, which requires VectorData ≥ 10.5.2 — and
10.5.0 removed the type its own `Connectors.InMemory` still binds to. That connector cannot move:
1.74.0-preview is the newest ever published. Restore succeeded and the build was clean, so the
entrant sat broken on `main` through a full release cycle. Pinning VectorData was the wrong fix
(NU1605 — 1.79.0 states the ≥ 10.5.2 floor); the pin belongs on the package that moved.

Two guards followed, both for the same reason the defect survived:

- **`SemanticKernelEntrantTests` ran on every push, passed throughout, and could never have caught
  it.** All six cases exercise `RetrievalDepth` and `TopDocuments` — pure functions over
  `ScoredDocument` that load no Semantic Kernel type. The only code touching the connector sat
  behind `RAGNET_BEIR_LONG_RUNS` and an hour of corpus. `SemanticKernelConnectorLoadsTests` drives
  create/upsert/search against a stub embedder in **169 ms, ungated**; verified by restoring 1.79.0
  and watching it fail with the exact `TypeLoadException`.
- **The run tag was a literal that had already gone stale.** `"semantic-kernel-1.78.0"` was
  documented as naming "the exact version measured" with nothing enforcing it, so from #119 onwards
  every run file it wrote would have named a version it had not run. It is now derived from the
  loaded assembly — and cross-checked against the `Directory.Packages.props` pin, because this same
  dependency graph proves an assembly can misname its package: **VectorData.Abstractions 10.1.0
  ships an assembly whose informational version reads 1.74.0.**

`CostMatrixDumpTests` gates every cell and emits the published tables, so the page cannot be
rebuilt by hand from numbers nothing checked. **Phase 5.1 is complete.**

### Phase 5.1.1: The Cost Figure Read Back [status: complete 2026-08-11 — optimised, verified and republished; the owed single-session five-entrant sweep paid 2026-08-12]

**Goal:** act on what 5.1 measured, instead of filing it. Publishing a latency number we lost on
was the point of publishing it.

**The owed sweep, paid 2026-08-12.** All twelve cells, five entrants, three gated repeats each, on
one machine in one session, repeat-outermost so drift lands on every entrant equally instead of
becoming a between-entrant difference. `docs/reference/library-comparison.md` now publishes
three-run spreads throughout and no longer carries the two-session caveat. Semantic Kernel, whose
code has not changed since, landed inside both earlier sessions' envelopes and slightly below them
on FiQA — so the session was measurably no noisier than the ones it replaces, rather than merely
declared quiet. The tighter measurement also **corrected a published claim**: FiQA's control index
construction reads 0.10–0.11 s, not 0.11–0.19 s, so most of the "indexing got slower" delta was
session noise and the real cost of hoisting norms is about 0.01–0.02 s over 57,638 documents.

**Five attempts; the four discards carry the transferable part.** Two died of machine contention.
The third died because Phase 5.3's new descriptor joined `BeirComparisonControlTests`' theory —
that class iterates `BeirDatasetDescriptor.All` — and hit a cold embedding cache seven minutes in;
`RAGNET_BEIR_LONG_RUNS` deliberately ungates *everything*, so opting in enrolled a dataset nobody
had measured. **The fourth is the one to remember.** Stopping the third did not stop the processes
it had spawned: the orphan kept writing to the log after truncation (the NUL bytes that appeared
mid-run are the signature) and two `dotnet test` runs then contended for the same run files, one
failing on a file lock. It nevertheless **exited 0**, having written nine of twelve cells per
repeat — silently omitting the Semantic Kernel entrant, the one row that calibrates whether the
machine was quiet. A sweep that reports success while dropping its own control is the inert-guard
shape this repository keeps finding — but the tooling was not what failed. `CostMatrixDumpTests`
refuses a partial matrix by design and names every missing cell; run against that sweep it would
have rejected it in under a second. The defect was procedural: a sweep's exit code was read as
evidence it had measured what it claimed, when the only thing that establishes that is the gate.
Recorded in `docs/reference/ci.md` beside the sweep commands, where the next person runs them.

**The measurement pointed at two defects, and the benchmark suite could not see either.** Ten
benchmarks in `Rag.NET.Benchmarks` call a `SearchAsync` and **every one is a stub** holding the
store still while some other component is measured — correct for those benchmarks, and it left the
real `InMemoryVectorStore.SearchAsync` with no coverage at all. The one retrieval path carrying a
published latency figure was the one path with no allocation profile. `VectorSearchBenchmarks`
fills it at the corpus sizes the comparison used, and reproduced the published control p50 on its
first run — which is what established that the scan, not the surrounding span, was the cost.

- **The dense scan allocated the whole corpus per query.** `SearchAsync` built a `List` pre-sized
  to `_dense.Count` and sorted it to take ten: **901 KB per query** over FiQA's 57,638 documents,
  past the Large Object Heap threshold, with Gen2 collections visible from 8,674 documents up — and
  O(n log n) work for an O(n log k) question. A bounded top-k selector makes allocation **constant
  at 952 B** across every corpus size measured.
- **Scoring recomputed two constants per candidate.** Cosine ran three multiply-accumulate chains
  over 384 floats — the dot product and *both* norms — although the query's norm is fixed for the
  whole scan and a stored vector's cannot change while stored. Hoisting both and vectorising the
  remaining chain with the in-box `Vector<T>` (not TensorPrimitives, which would add a dependency
  to `Rag.NET` itself) leaves one chain per candidate.

Measured on one idle machine in one session: **4.3× / 4.0× / 2.5×** at 5,183 / 8,674 / 57,638
documents, allocation **81.75 KB → 952 B**, **136.3 KB → 952 B**, **901.36 KB → 952 B**.

**Verified against retrieval quality, because vectorising changes summation order.** A vectorised
reduction folds several partial sums instead of accumulating left to right, so scores move in the
last bits — usually *more* accurately — and retrieval turns scores into an ordering, so two chunks
differing in the seventh decimal can swap rank. The full gated BEIR suite passes **89 cases with
every pinned nDCG unmoved**, across control, parity, reproduction, ablation, real-chunking and
Semantic Kernel rows on all three corpora. The three failures are
`fiqa-{langchain,haystack,llamaindex}.trec` not existing — the refuse-on-miss rule for a corpus no
Python entrant has ever run, which scores pre-existing run files and never touches this store.

**A stacked pull request reported itself merged and was not.** #138 was based on #137's branch;
when #137 squash-merged and its branch was deleted, GitHub closed #138 as merged while none of its
commits reached `main`. Caught by checking the file rather than the label — `main` still carried
the scalar loop and no `DenseEntry`. This is the second time a merge state has had to be verified
by content in this repository, and it will not be the last.

**Published.** Post-optimisation the control measures 0.3–0.4 ms (SciFact), 0.9–1.1 ms (ArguAna)
and 7.9–10.0 ms (FiQA) — **fastest of the five entrants on all three corpora**, reversing the
published ordering against Semantic Kernel. Those figures are in the comparison page's tables, not
in a footnote: a result nobody reads is not published, and the earlier "superseded" note left the
old numbers in the table where a skimming reader would take them as current.

**The session split is labelled rather than hidden, and Semantic Kernel is what makes that
honest.** The Python rows are from 2026-08-10; the two .NET rows were re-measured 2026-08-11 in two
separate idle sessions of three gated rounds each, and their columns publish the union across both
— six runs, which is why those ranges are wider than the Python ones rather than tighter. §2.2
still wants a single sweep of all five and that is still owed. What makes publishing before it
defensible is that **SK is the control for the control**: its code did not change between the two
sessions, so its movement is pure session variance — roughly ±20% — against a control that moved
4–5×. Cross-session noise cannot manufacture a change that size, and the Python gap is two orders
of magnitude.

**Index construction moved the other way, and the page says so.** FiQA's control indexing went
0.09 s → 0.11–0.19 s, because each vector's norm is now computed once on write rather than once per
candidate per query. That is the trade working as intended — roughly 0.05 s at index time for
~10 ms off every query over 57,638 documents — and part of even that increase is session variance,
since SK's indexing moved similarly without any code change.

**Still owed:** one sweep of all five entrants in a single session, which collapses the union
ranges back to three-run spreads and removes the caveat entirely.

### Phase 5.2: Multi-Hop Retrieval [status: complete 2026-08-15 (#168 functions, #172 real reports, #173 comparative) — MultiHop-RAG landed and measured, GraphRAG functions, and the comparative run says GraphRAG does not help on this corpus: −0.02761 against the candidate-set control]
**Goal:** Measure multi-hop retrieval — HotpotQA, MuSiQue, 2WikiMultiHopQA, MultiHop-RAG. (Not a
features.md row — evaluation depth past single-hop BEIR.)

The handover's argument, kept because it is worth keeping: **multi-hop is where reranking and
query decomposition show measurable lift** — the single-hop ablation table showed the reranker
*hurting* two of three corpora, part of which is the depth protocol its own debt records — and it
is the natural home for GraphRAG. One correction while keeping it: the handover puts GraphRAG "on
the backlog", but `Rag.NET.GraphRag` shipped long ago and is `✅ Done` in features.md — what is
genuinely open is that **no benchmark has ever measured it**, and multi-hop is where it would
earn or lose its keep. **MuSiQue is described as the hardest and least gameable of the four**,
which is what makes it the one to trust when the numbers disagree.

**2026-08-10 — "no benchmark has ever measured it" stopped being a scheduling note and became a
correctness problem.** The dead-settings sweep (#108) found, in `Rag.NET.GraphRag` alone:

- **`GraphRagOptions.EntityTypes` and `.RelationshipTypes` did nothing.** Documented as
  constraining extraction; the two declarations were the only occurrences in `src/`. Every run
  since the package shipped used open extraction regardless of configuration. Implemented in
  #112.
- **`GraphRagRetrievalOptions.Mode` is never read**, and `GraphRagRetrievalMode.Auto` — documented
  as *"LLM classifies the query and routes to Local or Global automatically"* — has no
  implementation. Which search runs depends on which behaviors are registered. Open as #104.
- Three more settings would have silently disabled or corrupted search (`LocalSearchDepth` or
  `LocalTopEntities` at zero; `PageRankWeight` outside `[0, 1]` giving one blend term a negative
  coefficient; `GlobalBatchSize` at zero hanging global search in an infinite batching loop).
  Validated in #103.

**None of that could have survived a benchmark run**, and none of it was found by tests, review or
use — it was found by asking which public settings are never read. A package marked `✅ Done`,
carrying a `Rag.NET.GraphRag` NuGet package about to be published, had three documented behaviours
that did not exist.

So this phase's GraphRAG item is no longer "measure it to see whether it earns its keep". It is
**"establish that it works at all"**, and the two questions want different things:

1. **Does it function?** A small, cheap, deterministic run that proves entity extraction, community
   detection and both search paths produce sensible output on a known corpus. This does not need
   MuSiQue, a published baseline, or a comparable number — it needs to exist and to fail loudly if
   the pipeline stops working. It is also what would have caught all three defects above.
2. **Does it help?** The comparative question the phase was written for: GraphRAG against the dense
   baseline on multi-hop, where the mechanism should show lift if it is going to.

**(1) should not wait for (2).** MultiHop-RAG is the cheapest honest home for it — 609 articles and
a real shared corpus, per the licence table below — where HotpotQA's 5.23M-document corpus makes
the comparative run expensive enough to keep deferring. Landing (1) against MultiHop-RAG turns
GraphRAG from "shipped and unverified" into "shipped and exercised", which is the claim
`features.md` is currently making on its behalf.

**Until (1) exists, `features.md`'s GraphRAG row overstates what is known.** Not because the code
is wrong — #103 and #112 fixed what was found — but because nothing has ever run it end to end,
and today is the second time this repository has learned that "it is implemented" and "it works"
are different claims.

Every dataset lands under the Milestone 3 checklist the DoD names — descriptor, budget timing,
published reference where one exists, licence from upstream, reproduction pin.

**Licence and shape determinations, from primary sources, 2026-08-09.** The licence was the
gating item; the *shape* finding turned out to matter more.

| Dataset | Licence (upstream) | Retrieval corpus | Verdict |
|---|---|---|---|
| HotpotQA | **CC BY-SA 4.0**, verified | **A real shared corpus — 5.23M docs.** Already a BEIR dataset | **clear to land**, at a cost |
| MultiHop-RAG | **ODC-BY**, verified | **A real shared corpus — 609 news articles**, 2,556 queries | **clear to land** |
| MuSiQue | **CC BY 4.0**, verified from the LICENSE file | **None.** 20 candidate paragraphs per question | **needs new infrastructure** |
| 2WikiMultiHopQA | **Unclear** — Apache-2.0 covers the *repo*; the data sits on unlicensed Dropbox zips | **None.** 10 paragraphs per question | **blocked + needs infrastructure** |

**The shape finding reshapes this phase.** Two of the four do not ship a retrieval corpus at all —
they ship per-question candidate paragraphs. Running them corpus-style means *constructing* a
corpus by pooling and deduplicating those paragraphs and assigning stable document ids, which is
new infrastructure and, more importantly, **a decision that determines whose published numbers you
can compare against**: every retrieval-stage figure for MuSiQue and 2Wiki (HippoRAG, IRCoT) is on
the authors' own 1,000-question pooled corpus, so the figures are comparable only if that exact
construction is reproduced. Corpus construction is not a preliminary here; it is the experiment.

Two corrections to this entry's own framing:

- **MuSiQue is described above as the one to trust when the numbers disagree. That still holds on
  quality grounds — but it is the least reproducible of the four.** No tags, no releases, no DOI;
  distribution is a bare Google Drive file id. The best pin available is the repo commit plus a
  recorded SHA256 of the downloaded zip.
- **HotpotQA is the easiest to land and the most expensive to run.** It is already in BEIR format
  with binary qrels and a published BM25 nDCG@10 of 0.603, so no adapter is needed — but its
  corpus is 5.23M documents, orders of magnitude beyond anything the harness has run. Subsetting
  would make it affordable and would simultaneously break comparability with the published figure,
  which is the whole reason to run it.

**All four use binary judgements** — supporting-paragraph flags, supporting-fact sentence ids,
evidence triples, evidence sets. The form of each was checked rather than inferred from how the
dataset is usually described, on the FiQA precedent. So none of them exercises the graded path;
TREC-COVID in 5.3 remains the only real candidate for that.

On the evidence, **MultiHop-RAG is the one to land first**: a genuine shared corpus small enough to
run, a verified licence, a clean HuggingFace revision pin, and retrieval-stage reference figures
(Hits@10, MRR@10, MAP@10) rather than answer-level ones. Its only work is deriving document-level
qrels from per-query evidence lists, plus a policy for the **301 null queries** that have no answer
and no evidence — which is a real decision, not a detail, since they are 12% of the query set.

> **Correction, 2026-08-12: "retrieval-stage reference figures rather than answer-level ones" is
> true and misleading, and the misleading half is the one that mattered.** The paper's Table 5 is
> indeed retrieval-stage — and it reports MAP@K, MRR@K and Hit@K for ada-002, llm-embedder,
> bge-large-en-v1.5, jina-v2, e5-base-v2, voyage-02 and instructor-large. **There is no MiniLM
> row**, and this repository pins `all-MiniLM-L6-v2` at nDCG@10. Both the model and the metric
> differ, so not one figure in that table can anchor a run here; MTEB does not carry the dataset as
> a retrieval task at all, so the source every other published figure in this programme comes from
> holds nothing for it either. **There is no published reference for our configuration, and this
> entry read as though there were.** The descriptor now carries `PublishedNdcgAt10 = double.NaN`
> — not a placeholder: every comparison against NaN is false, so no parity assertion against this
> dataset can pass by accident. This is the fifth claim in the programme to come from a plausible
> reading rather than from the source, after four licences.

**Landed 2026-08-12, and measured.** MultiHop-RAG carries the full Milestone 3 per-dataset
checklist: licence **ODC-BY (`odc-by`)**, declared by the dataset's own authors on their own
Hugging Face repository `yixuantt/MultiHopRAG`, pinned at revision
`71ac0d0bd1f951d2d6b70311f7d2ae404e1ffa82`; 609 documents, all titled, bodies averaging 10,340
characters; 2,556 queries of which **301 are `null_query` records** answered "Insufficient
information." with an empty evidence list, leaving 2,255 judged; **5,908 qrels rows** after 6,084
evidence rows collapsed on 176 duplicate citations of the same document by the same query;
`BeirRunBudget` timing measured rather than derived; the figure pinned in `BeirReproduction`.

| Leg | nDCG@10 | Notes |
|---|---|---|
| Parity (control only, not pinned) | **0.55724** | Recall@10 0.68906, MRR@10 0.63748, 609 units, 41.1 s |
| Real (chunked, pinned) | **0.63967** | Recall@10 0.78684, MRR@10 0.70150; 17,648 units over 609 of 609 documents, max 201 from one article |
| Chunking delta | **+0.08243** | The largest in the suite |

The delta is large for a reason the corpus explains: at 10,340 characters a document, the parity
protocol truncates each article at the model's 256 tokens and cannot see most of what the chunked
leg indexes. Parity is declared **inapplicable** for that reason and carries no pinned entry; it is
measured only as the control this delta is subtracted from. All 2,255 judged queries were pooled,
none excluded for want of a positive judgement, and every one retrieved two or more units of one
document — so max-pooling is exercised over the whole query set here, which is true of no other
corpus in the table.

**The wall clock is an upper bound and is recorded as one.** The Real leg read **600.2 s** cold, on
a machine at 45% CPU with other applications running; it wants an idle re-measure before it is
quoted as a cost. A partly warm first attempt read **392.6 s** — a **1.6x cache artefact** — and
nDCG@10, Recall@10 and MRR@10 were identical to five decimals across the two. That is the expected
result for a deterministic protocol, and it is the point: the *figure* is stable and the *timing* is
not, so they are pinned in different files with different confidence. *(Re-taken idle and cold,
twice, 2026-08-15 — Phase 5.2.1, #174: 343.5 s and 388.5 s. The bound held and was loose by
1.5–1.75x.)*

**Goal (1) was answered 2026-08-12.** `GraphRagFunctionsTests` runs `Rag.NET.GraphRag` end to
end over a pinned 60-article slice — 8,999 entities, 16,403 relationships, 607 communities, a
known-relevant document in the top 10 for all 27 of the slice's judged queries, and a global-search
map/reduce that runs over the community reports it finds in the unfiltered candidate set. It
publishes **no nDCG**, and it never will: 60 of 609 documents and 27 of 2,255 queries would not be
comparable to the Real row above, to any other row, or to anything outside this repository, and a
number in `BeirReproduction`'s cell is read as comparable by construction.

**Goal (2) was answered 2026-08-15, and the answer is no.** `BeirGraphRagCorpusTests` measured the
whole 609-article corpus under the graph path — 43 m 29 s wall clock, 22 m 18 s of it graph
construction, 35,296 extraction requests and 3,587 report requests all replayed from cache with no
model called. Local search, max-pooled to documents over all 2,255 judged queries:

| Leg | nDCG@10 | Recall@10 | MRR@10 | Store | Depth |
|---|---|---|---|---|---|
| GraphRAG local search | **0.56897** | 0.71696 | 0.63302 | 321,151 units | top-500 |
| Candidate-set control | **0.59658** | 0.74254 | 0.66785 | 321,151 units | top-500 |
| Real leg (2026-08-12) | 0.63967 | 0.78684 | 0.70150 | 17,648 units | top-2,010 |

**The honest comparison is the middle row, not the bottom one.** The candidate-set control scores
the *same* dense top-500 the behaviour was handed, over the *same* 321,151-chunk store, so store
size and candidate depth are held constant and the graph behaviour is the only variable. Against
it GraphRAG is **−0.02761**, with Recall@10 and MRR@10 moving the same way (−0.02558, −0.03483).
**GraphRAG does not help on this dataset. It hurts.** The delta against the Real leg's 0.63967 is
**−0.07070** and is **depth-confounded** — 17,648 chunks at top-2,010 against 321,151 at top-500 —
so it must never be quoted without that sentence attached. Both numbers belong in any account of
the run; only the control answers "does the graph path help".

> **Correction, 2026-08-15, the same day (Phase 5.2.1, #232): "depth-confounded" was a plausible
> reading and it is measured false.** A dense run over the Real leg's own 17,648-chunk store at
> top-500 reproduces the Real leg's nDCG@10, Recall@10 and MRR@10 **to five decimals** — the same
> ten documents in the same order on every one of the 2,255 queries. Depth contributes 0.00000 of
> the −0.07070. It decomposes exactly: **−0.04309 is store pollution** — 303,503 graph-derived units
> competing with the article chunks for rank at the same depth — and **−0.02761 is the graph
> behaviour**. The sentence above stands so the correction has something to correct; the entry
> below carries the measurement.

**One visible mechanism, part artefact and part real.** A community report reached the top 10 on
**891 of the 2,255 queries (39.5%)**. Reports are indexed under a synthetic document id no qrels
row judges, so each is a rank slot scoring zero. The artefact half: a user asking a synthesis
question might genuinely *want* the report, and this protocol cannot credit it. The real half: the
slot is spent either way, so a judged document is displaced down the ranking for a reader who
wanted a document. Both are true, and neither this entry nor the reproduction cell resolves the
tension by asserting one of them. Reports are deliberately not filtered out, because a pipeline
that returns them is a pipeline whose caller sees them.

**Reproducible only with #231 applied**, the fix for #230: `GraphLocalSearchBehavior` keyed its
deduplication on `ChunkIndex` alone rather than on `(DocumentId, ChunkIndex)`, so at corpus scale
candidates from different documents collided and roughly a third of every candidate set was
discarded, arbitrarily by document. The run's own counters are the check — 500.0 chunks in, 500.0
out, 0.0 dropped, 85.8 distinct documents in and out.

**What this does not say.** One dataset, one embedder, one implementation. It says GraphRAG's local
search *as implemented here* does not beat plain dense scoring of the same candidates on
MultiHop-RAG. It does not say GraphRAG is worthless, and it may not be generalised that far from a
single corpus. Global search was exercised and deliberately **not** scored: it map-reduces
community reports into a synthesised answer while these qrels judge documents, so an nDCG for it
would be a category error wearing a comparison's clothes. Described only — query `mhr-0000`, 491
results, 3 map/reduce calls in 44.4 s, reports reached in 1 retrieval, best report at rank 50 in an
unfiltered scan.

**What it cost to get there.** Extraction is one LLM call per chunk plus a gleaning pass — **4,088
calls for 60 articles**, so the full corpus was roughly **41,000**, generated once against
`openai/gpt-4o-mini` at temperature 0 and cached; community reports are a second generation run of
3,587 calls (#172, #226). Neither cache is ever committed, so this case can no more run on a fresh
runner than the Hyde cells can. The derived cost this replaces said 1.5–3 h and ~251,000 indexed
vectors; measured, it was 43 m 29 s and 321,151 vectors — **wrong in both directions at once**,
overshooting the clock by 2–4x while undershooting the store by 28%.

**Running it once found six defects in shipping library code.** All six were live in packages
published at 0.1.0; none was found by a test, a review or a user.

1. **`RecursiveChunkingStrategy` and three other chunkers sliced by UTF-16 code unit** (`6f86f0a7`),
   bisecting surrogate pairs and emitting strings that make `String.Normalize` throw — which kills
   any downstream embedder on text containing emoji. Fixed at four sites. **The defect predates this
   phase; MultiHop-RAG merely has emoji in it.** Every corpus this harness had run before was
   scientific abstracts, and this is the first made of news.
2. **The clusterer dropped edges the store held, on a casing difference** (`e9178aee`, three sites).
   `Leiden` and `PageRank` matched relationship endpoints with `StringComparer.Ordinal` while
   `SqliteGraphStore`'s `name` column is `COLLATE NOCASE`.
3. **Leiden discarded intra-community weight when aggregating** (`929d45a3`), so merging always paid
   and it over-merged unconditionally. It reproduced with no corpus at all: **ten disjoint 10-node
   cliques joined in a ring returned ONE community of 100.** On the real slice the largest community
   held **88.4%** of the graph (7,954 of 8,999) before the casing fix and **89.7%** after it — the
   fix *grew* it, which is what said the real cause lay elsewhere — and **8.8%** (796 entities)
   afterwards.
4. **Community detection appended every entity description to itself** (`46ff566b`). Scores were
   persisted through `AddEntitiesAsync`, whose merge clause concatenates, so each row merged with a
   copy of itself once per run — and, as an ingestion behavior, once per **document**: two runs
   produce four copies, a sixty-document ingestion compounds sixty times.
5. **`LeidenOptions` was unreachable from the public API** (`c34d270e`) while `docs/guide/graphrag.md`
   told readers to tune Leiden `Resolution` through it. The defaults were the only values that had
   ever run. Same shape as #108's dead settings: stored, documented, ignored at the point of use.
6. **The community report prompt was unbounded and global search could not reach its own reports**
   (`49da36ae`, `2abc17e4`). One prompt measured **1,806,352 characters** — some 450,000 tokens
   against gpt-4o-mini's 128,000-token context — because its size was a property of the corpus
   rather than of the code. And global search maps over chunks tagged `graph_type =
   community_report`, of which a dense top-500 over this slice contained **none**, so the map phase
   never ran and the behavior returned its input untouched, looking to every caller as though it had
   worked: **zero map/reduce calls.** Bounded at 50,000 characters and filled in PageRank order, the
   largest prompt this run builds is **49,933 characters**, the best report moved from rank 1,098 to
   209, and the map/reduce runs — **2 calls, unfiltered**.

**Three tests had been written around broken behaviour rather than through it, and that is the
transferable finding.** `Detect_ThreeCliquesWithBridges_FindsThreeCommunities` asserted only
`Count >= 1`, with a comment conceding "Leiden may merge communities" — it now asserts **3**, and
passes. Two assertions in this phase's own new guard were weakened on first writing to accommodate
degenerate output, and restored once the pipeline was sound. **Individually each was defensible.
Together they meant a green suite over a pipeline where 90% of the graph sat in one bucket and
global search never executed.** A test that documents the defect it tolerates is still a test that
stops anyone finding it.

**Recorded, not fixed:**

- **65% of communities are singletons** (396 of 607). 273 of them are entities appearing in no
  relationship at all; the other **123 have edges and still end up alone because every one of those
  edges is dropped before Leiden sees it** — `BuildAdjacency` keeps an edge only when both endpoints
  resolve to an extracted entity, and 853 of the slice's 16,403 relationships (5.20%) fail that: 837
  name something the entity pass never extracted, 16 name the same entity twice. 273 + 123 = 396
  exactly. (An earlier version of this note, and of the comment in `GraphRagFunctionsTests`, said
  their neighbours had been drawn into other communities. That was wrong: they are not in the
  clustering's input at all. Over the full 609-article corpus the same drop is 5,492 of 147,021,
  3.74%.) Printed on every run and deliberately not asserted: a pass/fail bound would hide movement
  inside its band, and movement is the whole of what is interesting.
- **Relationship weights were unbounded until #209, and the slice could never have shown it.** The
  extraction prompt asks for `"weight": 1.0`; over the full corpus the model returned acquisition
  prices, `Microsoft -> Mojang` at 2.5e9 and `Microsoft -> Rare` at 3.75e8, and those two edges of
  147,021 carried 99.99% of the graph's weight. Leiden returned **57,484 of 62,392 entities (92.13%)
  in one community** at modularity 0.0001. `GraphRagOptions.MaxRelationshipWeight` (default 10)
  bounds it: 20 of 147,021 weights altered, largest community 5,629 (**9.02%**), modularity 0.7496.
  Over these sixty articles the heaviest weight is 6.0, so the bound alters nothing and the slice
  guard's 7.3% was never evidence about the corpus.
- **The guard never saw a real community report. Fixed 2026-08-13 (#172).** `PromptEchoChatClient`
  returned a bounded head of the prompt, so a "report" was its community's own entity descriptions.
  Reports are now generated once against `openai/gpt-4o-mini` at temperature 0 by the generation
  tool's `--stage reports` and replayed refuse-on-miss out of the `graph-reports` directory of
  `GraphExtractionCache` — the same treatment extraction has, made possible by bounding the report
  prompt. **The two rank figures (1,098 → 209) were measured against the stub and remain properties
  of it**; the run prints the rank it actually measures. Global search's map-reduce deliberately
  stays on the stub: its prompts are built from whichever reports retrieval returned, and ONNX
  kernel dispatch can reorder near-ties across machines, so caching them would make the guard
  machine-specific for a reason unrelated to GraphRAG.
- **`Leiden.RefineSingleNode` indexes `assignment[comm]` with a community id where a node index is
  wanted.** It is correct today only because refined community ids are always seed-node indices.
  Fragile, worth a follow-up, and not touched in this phase because it was found while reading rather
  than while failing. **Fixed 2026-08-12 (#170):** the refinement now takes its starting labelling as
  a parameter and resolves a sub-community's community through an explicit map, and
  `RefinementNumberingTests` re-runs it under two other labellings. Reading it was right about
  the defect and understated it — under a constant offset the old bounds check dropped the
  same-community constraint entirely and merged two communities into one, and under a dense
  renumbering it both merged across communities and blocked legitimate merges inside them.
- **The clusterer was Louvain-with-refinement, not textbook Leiden** — no guaranteed-connected
  communities, no refinement-partition aggregation. It clustered, and it was not the algorithm the
  name promised. **Documented 2026-08-12 (#171), not renamed and not reimplemented:** the type said
  on itself what it was, what it was not, and that the γ-connectedness guarantee was not provided.
  Checking the paper rather than the issue changed the recommendation. The seeding step the issue
  names is one of **three** departures, and the guarantee is a property of the other two — the
  paper moves only nodes alone in their refined community and merges one "only if both are
  sufficiently well connected to their community in P", and picks the target randomly by θ-weighted
  quality increase rather than by best gain. **Implementing the seeding step alone would leave the
  name just as false and look like it had been fixed.** **Renamed 2026-08-12 (#181):** the type
  became `LouvainWithRefinement`, its settings `LouvainWithRefinementOptions`, and the ingestion
  property `options.CommunityDetection`, each old name kept as an `[Obsolete]` forwarder. **Fixed
  2026-08-12 (#180)** — all three constraints and the seeding step, below. **Renamed back
  2026-08-12, same day:** once the guarantee held, the descriptive name understated the code and
  nobody searching for "Leiden" would find it, so `Leiden`, `LeidenOptions` and `options.Leiden` are
  the names again. The argument for keeping the descriptive name was that reversing would cost
  callers a second migration — that argument was wrong on its premise. nuget.org has only 0.1.0,
  published before the rename, and `git tag --contains` on the rename commit is empty, so no
  consumer ever saw the new names and the net cost to anyone downstream is one rename rather than
  two. The forwarders are **deleted** rather than stacked in the other direction, for the same
  reason: they protected nobody and a second layer pointing back would only confuse.
- **The missing guarantee fired, and is now supplied (#180).** Written as a disclaimer, then tested,
  because this repository has twice learned that "it is implemented" and "it works" are different
  claims. A ~30,000-detection sweep returned disconnected communities on sparse *weighted* graphs —
  48 of 2,220 random weighted trees, 14 of 520 tree-plus-chords, **0 of 2,220 unweighted trees**,
  and 0 on anything dense. The weights opened the gap, not the sparsity. **Fixed 2026-08-12** by
  implementing the paper's refinement phase in full: moves restricted to nodes alone in their
  refined sub-community; a merge permitted only when both the node and the target sub-community are
  γ-connected to their community in the unrefined partition P (in modularity's units,
  `E(C, S−C) ≥ γ·K_C·(K_S − K_C) / 2m`); the target drawn at random with probability proportional
  to `exp(ΔQ / θ)` over candidates with `ΔQ ≥ 0`, staying put included at `ΔQ = 0`; and the
  aggregate graph built from P_refined while the next level is seeded from P rather than reset to
  singletons. θ is the new `LeidenOptions.Randomness`, defaulting to 0.01 — the
  paper's own experimental value — and validated in its setter.
  **Re-swept with the same harness against both implementations: 132 disconnected of 3,351,175
  communities before, 0 of 3,359,331 after**, over 40,000 random weighted trees × five resolutions ×
  four seeds, which is roughly seven times the exposure of the run that first measured the base
  rate (53 of 490,358). `CommunityConnectivityTests` no longer pins the ten-node counterexample as a
  still-false record; it keeps the graph and asserts the communities are connected, across four
  resolution/seed pairs, alongside a 300-graph sample of the weighted-tree family.
  **What it cost on the corpus:** nothing measurable. The MultiHop-RAG slice returns the same 607
  communities and the same 396 singletons; only the largest community moved, 796 → 661 entities
  (8.8% → 7.3%), because the refinement splits the one community that had been holding two halves
  joined by too little. **What is still not guaranteed:** a run capped by `MaxLevels` returns the
  refined partition rather than the working one — connected, but finer than the uncapped run — and
  the partition is a randomised search's local optimum, so a different `RandomSeed` is a different
  partition. Determinism under a fixed seed is asserted over five repeats.
- **Retrieval-mode routing (#104) does not exist at all** — no `Mode` property, no
  `GraphRagRetrievalMode` enum, nothing in either behavior consulting one. **That issue's framing is
  itself wrong**: it reads as "a setting is not honoured" when the setting was deleted at 0.1.0
  (#125) and what is open is a feature. A test asserting `Mode = Local` routes to local search would
  not compile.

`features.md`'s GraphRAG row is corrected accordingly: it still says `✅ Done`, which was never false
about the code shipping, and it now says what is exercised and what is not.

### Phase 5.2.1: The GraphRAG Deficit Read Back [status: complete 2026-08-15 — added and closed the same day, all three items landed: #232 measured, depth costs nothing; #226 done, reports parallel and measured 4.1x at 4; #174 done, idle-cold Real leg 343.5 s and 388.5 s, FitsTheNightly stays false, decided; #239 ablations measured the same day, the whole behaviour cost is the PageRank blend and the walk adds no recall]

**Goal:** act on what 5.2's comparative run measured instead of filing it, the way 5.1.1 acted on
5.1's cost figure. Three things came out of #173's close and none had a home: a deficit the run
measured but did not decompose (#232), a wall clock the run was told to re-take cold and has not
(#174), and a library defect the run had to pay for to notice (#226). **Added the day the milestone's
last phase closed, so the milestone finishes whole** — the operator's call, 2026-08-15: close
Milestone 5 with its own follow-on inside it rather than carry three open issues from an evaluation
milestone into a hardening one and let "what is left" become unrecoverable from the phase table
again. That is the same gap 5.2's phase row was rewritten to close, and the reason this entry names
its issues in its header.

**Why these three and not more.** Each is bounded, each is cheap, and each is *owed by the run* —
it left a question the run itself created. Not here: the report-in-top-10 tension (artefact or
real), which is a design question and not a measurement; retrieval-mode routing (#104), which 5.2
recorded as a feature and not a defect; and the singleton-community rate (#176), which is an
extraction-quality question with no cheap experiment attached. Those stay where 5.2 left them.

**1. Decompose the deficit — [#232](https://github.com/MarcelRoozekrans/Rag.NET/issues/232).**
5.2 measured three points and separated one gap: the **−0.02761** between local search and the
candidate-set control is the graph behaviour alone, and that finding stands. **The −0.04309 between
the control (0.59658) and the Real leg (0.63967) is not separated.** Same corpus, chunking asserted
identical, and two things differ at once: the store holds 321,151 units against 17,648 — 299,916
entity/relationship chunks and 3,587 community reports competing for rank against judged documents
— and the depth is 500 against 2,010. One run separates them: **dense retrieval at top-500 over the
17,648-chunk article-only store**. Against the Real leg it prices depth; against the control it
prices pollution. No LLM call, no new cache; it is the control leg `BeirGraphRagCorpusTests` already
runs, pointed at the smaller store — 1,226 s for the control pass on 2026-08-15, so about twenty
minutes. **The two answers lead to opposite work**, which is why the run is worth twenty minutes:
mostly pollution means a ranking or filtering policy for graph-derived chunks, plausibly worth more
than tuning the behaviour and applicable to RAPTOR too, which also indexes synthetic chunks beside
real ones; mostly depth means a fairness caveat on the protocol and no defect at all. Publish the
fourth row beside the three in 5.2's table, with the two differences it prices stated in the same
sentence, and pin it in `BeirReproduction` — the figure is deterministic and needs no quiet machine.

**Measured 2026-08-15 — three runs at 120.0 s, 8 s and 7.0 s, identical figures every time, the spread being the OS page cache over 19,903 vector files — and the answer is the sharper of the two.** The fourth row is
`BeirProtocol.GraphRagDepthControl` — its own protocol, because both registries key on the pair
and it has its own cost and its own figure — run through `BeirHarness.MeasureAsync` with one
argument changed, so "nothing else moved" is a property of the call:

| Leg | nDCG@10 | Recall@10 | MRR@10 | Store | Depth |
|---|---|---|---|---|---|
| Real leg (2026-08-12) | 0.63967 | 0.78684 | 0.70150 | 17,648 units | top-2,010 |
| **Depth control (this run)** | **0.63967** | **0.78684** | **0.70150** | 17,648 units | top-500 |
| Candidate-set control | 0.59658 | 0.74254 | 0.66785 | 321,151 units | top-500 |
| GraphRAG local search | 0.56897 | 0.71696 | 0.63302 | 321,151 units | top-500 |

**Identical to five decimals on all three metrics** — not close, identical: cutting the candidate
set from 2,010 to 500 changed the top-10 document ranking of none of the 2,255 queries. So the
depth prices at **0.00000** and the whole **−0.04309 is store pollution**: 303,503 graph-derived
units competing with the article chunks for rank at the same depth over the same article text. The
−0.07070 against the Real leg therefore decomposes exactly, −0.04309 pollution and −0.02761
behaviour, and 5.2's "depth-confounded" is corrected in place above and in `BeirReproduction`
rather than deleted. Two consequences. **The lever is a ranking or filtering policy for synthetic
chunks**, and it is worth more than the behaviour — 0.043 against 0.028 — and it applies to RAPTOR,
which indexes synthetic chunks beside real ones the same way; that is a design question and it is
recorded, not decided, here. **And the guard's second finding, the community report in the top 10
on 39.5% of queries, is a lower bound on this pollution and was already the mechanism.** What the
figure does not say is that top-500 is a safe depth in general: the ten best documents' best chunks
survived the cut on this corpus, where the deepest article runs to 201 chunks; a corpus whose top
documents each stack more chunks above the tenth's best could lose it. The measurement is pinned on
its own authority for drift, as the Real and GraphRag figures are. **Its clock is the phase's own
small lesson re-learned**: all three runs were warm by the embedding cache's count (19,903 hits, 0
misses — it embeds nothing the Real leg has not) and still spread 17x, because the first run read
those 19,903 files off disk and the next two out of the page cache. Both ends are in the budget
cell, and 7–8 s is the compute.

**2. Re-take the Real wall clock cold, twice — [#174](https://github.com/MarcelRoozekrans/Rag.NET/issues/174).**
`BeirRunBudget`'s `multihop-rag` / `Real` cell records **600.2 s**, taken on 2026-08-12 at ~45% CPU
with other applications running, and labels itself an upper bound; `FitsTheNightly` is deliberately
`false` on it because flipping a gate on a knowingly inflated figure is what the cell exists to
prevent. Over-estimates fail safe for a budget — they keep a case *out* — which is why the figure was
publishable at all and why nobody should schedule against it. **Take it cold and take it twice**:
the same run read 392.6 s partly warm against 600.2 s cold during #168, a 1.6x page-cache artefact
with every metric identical to five decimals, and 5.1.1 recorded a 23x artefact of the same kind
producing three false findings in one day. Kill strays by assembly name first, per the overnight
plan; do not poll the run. Only the timing moves — the nDCG is pinned separately and stays. Then
reconsider `FitsTheNightly` on the clean figure, and record whichever way it goes.

**Done 2026-08-15.** Cold by the nightly's own definition — a fresh cache root holding the
converted dataset, the model and the vocabulary and nothing else, the `embeddings` directory absent
and deleted again between the runs — on this machine at 2–4% load with nothing else of the
repository's running, and not polled:

| Run | Real leg | Parity control | Case | Embedding cache (real leg) | nDCG@10 |
|---|---|---|---|---|---|
| 2026-08-12, cold, under load | 600.2 s | 78.9 s | 11 m 19 s | cold | 0.63967 |
| 2026-08-15, cold, idle #1 | **343.5 s** | 34.1 s | 6 m 18 s | 2,314 hits, 17,589 misses | 0.63967 |
| 2026-08-15, cold, idle #2 | **388.5 s** | 58.1 s | 7 m 27 s | 2,314 hits, 17,589 misses | 0.63967 |

Recall@10 and MRR@10 identical to five decimals across all three, the parity control 0.55724 every
time. **The bound held and was loose by 1.5–1.75x**, which is what "over-estimating fails safe"
predicted, and the two idle figures are quoted rather than averaged: 13% apart is the honest width
of an idle measurement here, and the parity leg's 34–58 s is a small leg's warm-up noise, not a
finding. **`FitsTheNightly` is decided, and it stays `false` — for a different reason.** The old
reason, an inflated figure, is gone. The reason now is that this figure is about this machine and
the nightly runs on another: a 4-vCPU hosted runner against 20 logical processors here, and CPU
ONNX embedding of 20,453 texts scales with cores, so the runner's cold cost is plausibly 3–5x this
— 20–35 minutes for the case — and unmeasured. Flipping a 120-minute job's gate on a local figure
the runner may not reproduce is the casual decision the old cell refused, one machine over; every
Real leg in the table is opt-in by policy, and #175 already records the nightly's MultiHop-RAG
footprint as unbudgeted. **What would flip it** is one opted-in, timed dispatch of the case on
`ubuntu-latest`, and then a decision about the nightly's total rather than about this cell alone.
The cell says all of this in place, with the command that runs it.

**3. Community reports in parallel — [#226](https://github.com/MarcelRoozekrans/Rag.NET/issues/226).**
`CommunityDetectionBehavior` awaits one report at a time. There is no concurrency option and
nothing reports the cost: 607 round trips on the slice, **3,587 on the corpus**, while entity
extraction next door runs `Concurrency = 12`. Ingestion is the path a user runs — `UseGraphRag()` on
a 60k-entity corpus is a multi-hour loop with no progress signal, and it was noticed only because
a benchmark had to pay for it. That makes it the seventh library defect 5.2's run surfaced, and the
precedent for the other six is that they were fixed in-phase rather than routed onward. Bound the
concurrency with an option in the shape `GraphRagOptions` already has for extraction, and **keep the
per-community determinism** — the report prompt is filled in PageRank order with an ordinal
tie-break so two runs agree, and a parallel fill must preserve that per report rather than merely
overall. **Measure it against the provider rather than assume it**: the rate limit is the real
ceiling and a 429 storm trades one wait for another. The replayed cache means the benchmark cannot
show the gain; a bounded probe can, and its cost is stated derived until #200 exists.

**Done 2026-08-15.** `GraphRagOptions.CommunityReportConcurrency`, default 4, validated `> 0` at
registration. One correction to the paragraph above first: **"the shape `GraphRagOptions` already
has for extraction" was wrong** — the library has no extraction concurrency; the `Concurrency = 12`
lives in the generation tool, which runs *articles* in parallel with a throwaway store each. So the
option is the library's first, and it takes the shape `MapReduceOptions.MapConcurrency` and
`EvaluationCallOptions.MaxConcurrentCalls` already use for calls of this kind. **The determinism is
in the shape of the fix**: every prompt is built first, sequentially, in Leiden's community order
and PageRank order inside each — the ordering the report cache is keyed on — and only the calls run
in parallel, through `Parallel.ForEachAsync` over the indices, each response written to `reports[i]`
for `prompts[i]`. Three tests: a probe client that refuses to answer until the configured number of
calls are waiting together, so a sequential loop deadlocks and fails on a 10 s timeout rather than
passing slowly (checked by mutation — bound forced to 1, test failed with the intended message); the
same probe at 1 asserting a peak of exactly 1; and the same graph run at 1 and at 4 with a stub that
echoes each prompt's member names in scrambled completion order, asserting every report names its
own community's members and none of any other's, and that the two community lists are equal
position for position. **The guard still replays 607 of 607 slice reports refuse-on-miss under the
new default**, which is the check that parallelism changed no key.

**Measured against the provider, as the entry demanded, and it pays.** Three disjoint sets of
uncached reports through the generation tool's new `--report-concurrency` — one flag, no prompt
touched, so the entries it wrote are keys the guard would compute — against OpenRouter's
`openai/gpt-4o-mini` at temperature 0:

| in flight | reports | wall clock | s / report | retries |
|---|---|---|---|---|
| 1 | 45 | 208.1 s | **4.62** | 0 |
| 4 | 70 | 79.0 s | **1.13** | 0 |
| 8 | 49 | 30.8 s | **0.63** | 0 |

Near-linear to 8 with no rate-limit pressure at all on that provider that day; the sequential set
had the *shortest* prompts (longest 29,917 characters against ~49,900 for the other two) and was
still the slowest per report, so the effect is if anything understated. Extrapolated to the corpus's
3,587 reports: about 4.6 h at 1, 68 min at 4, 38 min at 8. **The default stays at 4**, because
one provider on one day is not a rate-limit policy and 8 is a measured, documented option rather
than a default. Cost of the probe: 164 generated reports, **derived** at roughly $0.60 from the
report run's own rate — the tool still records no `Usage` (#200) — and it left 164 extra entries in
the shared report cache under keys nothing else computes, which is harmless. `CachedGraphRagClient`
gained a `Retries` counter so the tool can print rate-limit pressure beside the rate, and the
report plan now states the bound and divides by it instead of announcing "no concurrency to widen".

**4. Two ablations the operator asked for on reading the result — [#239](https://github.com/MarcelRoozekrans/Rag.NET/issues/239), added and measured 2026-08-15.**
Reading `GraphLocalSearchBehavior` to design them found the mechanism before the run did: the blend
is `(1 − w)·cosine + w·PageRank` with PageRank normalised to sum to one over 62,392 entities (mean
1.6e-5) against cosines of 0.3–0.6, so at the default `w = 0.3` every graph-connected entity chunk
is *demoted* by ~30%; the walk collects PageRank scores and adds no candidate; and two store calls
per seed fetch relationships and communities that are then discarded. Measured over one graph build
and one query pass (53 m 54 s, everything replayed):

| Ablation | Result |
|---|---|
| **1. `PageRankWeight = 0`** | nDCG@10 **0.59658**, Recall@10 0.74254, MRR@10 0.66785 — the candidate-set control to five decimals, **identical top-10 on 2,255 of 2,255 queries**. The whole −0.02761 is the blend. |
| **2. Reach of the walk** (seeds = top-10 entity chunks, depth 1, reached entities contribute their source articles, appended below the pooled candidates) | dense top-500 pools to 75.8 documents/query and already holds **Recall@100 = 0.98566**; the walk adds 24.0 documents/query for **+0.00148**; the additions alone reach **0.00610** of relevant documents. |

**So the retrieval deficit decomposes completely, and none of it is the graph.** 0.63967 dense →
−0.04309 store pollution (#232) → 0.59658 → −0.02761 PageRank blend on the wrong scale (#239) →
0.56897; the traversal contributes neither harm nor recall, because dense already retrieves 98.6% of
the relevant documents on this corpus and the graph knows 0.6% of them that dense does not. What is
left for local search to be *for* is context assembly — entity, relationship and report chunks in
the model's context — which is exactly the question 5.2.2 asks. Both ablations are recorded here
rather than pinned; they share the GraphRag budget cell, and `GraphRagRun.ExpandDocumentsAsync` is
the harness-side walk the behaviour does not perform. Fixing #239 is a design decision (rescale or
drop the blend; use or drop the traversal) and is not made in this phase.

**Exit condition, falsifiable:** #232's fourth row is published and pinned with the decomposition
stated; #174's cell carries a cold two-run figure with `FitsTheNightly` decided on it; #226 has a
concurrency option, a determinism test, and a measured statement of what it saved. Then the three
issues close and Milestone 5's phases box is ticked again — it is opened below for exactly as long
as this phase is pending.

### Phase 5.2.2: Does GraphRAG Help Answers? [status: complete 2026-08-15 — added and closed the same day: local search does not help answers (0.210 vs dense 0.350, pollution), global search does on entity questions (0.844 vs 0.772) and not on yes/no ones; design in `docs/plans/2026-08-15-graphrag-answer-level-evaluation.md`]

**Goal:** ask the question 5.2 could not: not "does the graph path rank documents better" but
"does it produce better *answers*" — in MultiHop-RAG's own currency, against the gold answer every
judged query carries, with the paper's own accuracy rule. **Added at the operator's request** the
day 5.2.1 closed, after they read 5.2's finding and asked whether it was the dataset or an
implementation mistake. The honest answer to that (recorded in the design) is that 5.2 measured a
currency GraphRAG does not claim to earn — document nDCG — and that the currency it does claim,
answer quality, has never been scored here. This phase scores it.

**Three arms, one graph build, one answering model, one prompt, top-6 context** (the paper's):
**A** dense over the article-only store; **B** GraphRAG local search *as shipped* (`w = 0.3`,
entity/relationship/report chunks in the context as they come); **C** GraphRAG global search, the
arm 5.2 could not score at all and the one Microsoft's claim is actually about. `openai/gpt-4o-mini`
at temperature 0 answers all three; every call is cached and replayed refuse-on-miss like
extractions and reports, so the figures are reproductions and the guard calls no model. **The judge
is the authors' `qa_evaluate.py` rule** — extract `The answer to the question is "…"`, lower-case,
count a hit on any shared word — re-implemented and unit-tested, with a strict-equality rule beside
it because that rule is lenient and the entry must say so. Per query type (inference 816,
comparison 856, temporal 583) and overall; the 301 null queries reported separately as abstention.
Pinned as three new protocols with three budget cells, because three figures with three costs.

**Spend is bounded and staged.** A and B are ~2,255 calls each (~$2 together, derived); C is
map/reduce over up to 50 community reports per query, ~25,000 calls, $15–25 derived — the pilot
(100 stratified queries, all arms, ~$1) decides whether C runs at 50 reports or 20. Nothing is
tuned to win: one shipped configuration per arm, and if #239's ablation lands as predicted, a
`B′` at `w = 0` is one extra arm at generation cost only, named as an extra.

**How it reads is written down before the number exists**, in the design: A ≥ B, C confirms 5.2 in
both currencies and answers the operator's question with "no, on this dataset with this
implementation and this model" — and says which parts of "this implementation" are design (#232's
shared store, #239's blend) and which are the graph. B > A reverses 5.2's headline. C > A is
Microsoft's claim holding, per type. Any of the four is a completion.

**Exit condition, falsifiable:** all three arms run in full over the 2,255 judged queries; three
figures pinned in `BeirReproduction`, three cells in `BeirRunBudget`; the sidecar of gold answers is
part of the converted dataset with its counts pinned; the judge has a unit test per rule; and this
entry names which reading the numbers support. **The milestone's phases box re-opens for this
phase and re-ticks when it closes** — the same discipline 5.2.1 followed, for the same reason.


**Done 2026-08-15, the day it was added.** Four arms rather than three — a **control** joined,
dense top-6 over the *graph* store with no behaviour, the answer-level analogue of #229's
candidate-set control — over all 2,556 queries (2,255 judged, 301 null), 19,674 model calls of which
17,668 were generated (2,006 came from the pilot), 1 retry, 1 h 25 m, replayed since. Three
deviations from the design, each stated: the figures are pinned in `MultiHopRagAnswerReproduction`
rather than `BeirReproduction`, because that table's entries and messages are about nDCG and
"nDCG@10 = 0.35" for an accuracy would mislead; the case shares the GraphRag budget cell rather
than owning three, because it is one graph build; and generation lives in the test class behind
`RAGNET_GRAPHRAG_ANSWERS_GENERATE` rather than in the tool, because the graph build and the
embedder live in the test project — recorded as debt on the class. Two things the pilot found before
the money was spent: **the model closes its quote after a full stop** (`"Google."`), which scores
zero under the authors' `split()` rule, so the headline is that rule over punctuation-stripped
tokens with the raw rule printed beside it; and **global search never says "no"**, which an
accuracy would have read as comprehension, so every figure below carries its answer distribution.
Also found on the way: `GraphGlobalSearchBehavior`'s "deterministic" shuffle was seeded from
`string.GetHashCode`, randomised per process — **#241, fixed** — without which arm C could not be
replayed at all.

| Arm | overall | inference (816) | comparison (856) | temporal (583) | abstains on nulls (301) |
|---|---|---|---|---|---|
| **dense** (article-only store) | **0.3499** | 0.7721 | 0.1636 | 0.0326 | 48.5% |
| control (graph store, no behaviour) | 0.1384 | 0.2806 | 0.0876 | 0.0137 | 41.5% |
| local (as shipped, w = 0.3) | 0.2102 | 0.4620 | 0.1005 | 0.0189 | 40.5% |
| **global** (map/reduce over reports) | **0.5951** | **0.8444** | 0.4953 | 0.3928 | 9.3% |
| *always-yes baseline* | — | — | *0.598* | *0.463* | — |

Paper rule over the 2,255 judged queries (strict: 0.3242 / 0.1215 / 0.1898 / 0.4523). **Read per
type, because the overall figure mixes two different things.**

- **Inference — the column that cannot be guessed — is the finding: global 0.844 against dense
  0.772**, 59 more entity questions right of 816. Global commits on 99% at precision 0.85; dense
  commits on 82% at precision 0.94. That is more coverage at somewhat lower precision, and it is a
  real, honestly earned gain from the arm 5.2 could not score at all.
- **Comparison and temporal measure willingness to guess, not retrieval.** Comparison gold is 60%
  "yes", temporal 46%; always-yes scores 0.598 and 0.463, and **no arm beats it** — global's 0.495
  and 0.393 come from committing on ~70% at a precision of 0.57–0.68, i.e. the base rate (it
  answers "yes" 532 times and "no" 55 on comparison); dense abstains on 78% and 95%. The paper's
  Table 6 aggregates these types the same way, which is worth knowing when reading its 0.44 / 0.56.
- **Store pollution is five times worse for answers than for rankings: control 0.138 against
  dense 0.350**, −0.21 overall and −0.49 on inference, where #232 priced it at −0.043 for the
  ranking. Six chunks is a small window, and 303,503 graph-derived units fill it.
- **Local search as shipped does not help answers either — 0.210 against dense 0.350 —** and
  5.2's finding stands in both currencies. It sits *above* the control (+0.072) because the blend
  #239 measured as a pure cost to the ranking pushes entity chunks out of the top-6, which is a
  strange way to help and the reason the pollution, not the behaviour, is the thing to fix.
- **Global guesses on unanswerable questions**: it abstains on 9.3% of the nulls where dense
  abstains on 48.5%.

**Which of the four pre-registered readings the numbers support: two of them at once.** *A ≥ B* —
local does not help; the retrieval finding holds in the answer currency, and the mechanism is
pollution. *C > A* — global helps, on entity questions, by a margin the strict rule and the
commitment rates both survive; its lead on yes/no questions and its overall 0.595 do not survive
the base rates and are not claimed. **The owner's question, answered:** on this dataset, with this
implementation and this model, GraphRAG's *local* search is worse than dense for answers and the
reason is a design choice (one shared store) rather than the graph; GraphRAG's *global* search is
better than dense for the questions where an answer must be found rather than guessed. Neither
half was visible from document nDCG. Cost: **derived at roughly $10** for the 17,668 generated
calls, at gpt-4o-mini's rates over the observed prompt sizes; #200 still records no usage.

**Recorded, not done:** a design for the shared store (filter or rank policy for graph-derived
chunks — the lever every measurement here points at, and it applies to RAPTOR); global search's
abstention; `B′` at `w = 0`, which the control arm made redundant since #239 showed `w = 0` *is*
the control. **The phases box re-ticks with this close.**

### Phase 5.3: Deferred Datasets — NFCorpus, TREC-COVID, EnronQA [status: complete 2026-08-12 — TREC-COVID landed and measured; NFCorpus declined on its licence; EnronQA still blocked, undeclared]
**Goal:** Land the three datasets the evaluation programme still lacks, together: the small hard
one, the graded one, the private-corpus one. (Not a features.md row — **this phase is the real
destination the TREC-COVID/EnronQA debt entry has waited for since 3.12**; its
Milestone-4-as-deadline backstop is replaced by this phase, which is the reason this milestone
exists rather than a debts list.)

- **NFCorpus** (~3.6k documents, medical jargon) — **new**: never considered by any phase before
  the handover proposed it. Its stated value is punishing weak embedding models — small enough
  to run in minutes, hard enough to separate models the easy corpora cannot.
- **TREC-COVID** (~171k documents) — **explicitly declined at Milestone 3's close (2026-08-03)**
  with recorded grounds, and it remains the first graded-relevance dataset: `IrMetrics`' `2^rel
  − 1` path has a hand-computed graded fixture and has never seen a graded dataset. **The
  FiQA-qrels check comes first**, exactly as the debt entry orders: read the cached
  `qrels/test.tsv` — if any grade exceeds 1, the graded path has been exercised by three phases
  of FiQA runs and the debt's premise falls; if none does, `IrMetrics.cs:31-32`'s "FiQA and
  TREC-COVID are graded" is wrong and gets corrected. Either way one sentence changes before the
  run.
- **EnronQA** — also declined at that close. The handover's case, recorded accurately so it does
  not have to be re-argued: **103,638 cleaned emails, 528,304 QA pairs, 150 inboxes, CC BY 4.0,
  published BM25 and ColBERTv2 baselines** (paper: arXiv 2505.00263) — and the genuinely
  distinctive part, **per-inbox structure that doubles as a multi-tenant collection-isolation
  test**, which no BEIR dataset offers. Its anti-contamination argument is also worth keeping: a
  model cannot have memorised someone's inbox, unlike NaturalQuestions or TriviaQA.

  **Correction, 2026-08-09: "CC BY 4.0" is wrong, and it is wrong in an instructive way.** That
  badge is the **arXiv submission licence for the paper**, not a licence for the dataset. The
  dataset has **no declared licence anywhere**: the HuggingFace repo `MichaelR207/enron_qa_0922`
  has no licence tag and no LICENSE file, and the paper's full text states none. (An MIT tag that
  turns up in searches belongs to a *derivative*, `weaviate/hard-questions-enronqa`.) The other
  four claims — 103,638 emails, 528,304 QA pairs, 150 inboxes, the arXiv id — all **verified**
  against the paper. So the handover was right about everything checkable from the paper and wrong
  about the one thing that required looking somewhere else, which is the same shape as the
  FiQA-qrels error: a plausible claim, repeated, never traced to its source.

Each arrives under the full Milestone 3 per-dataset checklist — descriptor, `BeirRunBudget`
timing, revision-pinned published reference where one exists, licence determination from
upstream, `BeirReproduction` pin — which is precisely the list Milestone 3's close said none of
them had, and declined them over.

**Licence determinations, from primary sources, 2026-08-09.** This was the checklist item gating
the whole phase, so it was done first and separately from any implementation.

| Dataset | Licence (upstream) | Pin | BM25 reference | Verdict |
|---|---|---|---|---|
| NFCorpus | **"Free to use for academic purposes"**, verbatim from [Heidelberg](https://www.cl.uni-heidelberg.de/statnlpgroup/nfcorpus/); non-academic use of the NutritionFacts.org material requires contacting **Dr. Michael Greger directly**. Citation of Boteva et al. 2016 required. | HF `b5026a0e…`; BEIR zip MD5 `a89dba18…` | nDCG@10 **0.325** | **DECLINED** 2026-08-12 |
| TREC-COVID | Corpus: **CORD-19 agreement — "text and data mining only"** ([LICENSE](https://github.com/allenai/cord19/blob/master/LICENSE)); qrels: NIST, unstated | HF `7e16fde3…`; BEIR zip MD5 `ce62140c…` | nDCG@10 **0.656** | **clear to land** |
| EnronQA | **None declared**, re-verified 2026-08-12 against the HuggingFace API: no licence tag, no `cardData.license`, no LICENSE file on `MichaelR207/enron_qa_0922` | HF `c0b3a919…` | Recall@5 87.5 (BM25) / 59.3 (ColBERTv2) | **blocked** |

**NFCorpus was declined on 2026-08-12, and the licence is only half the reason.** Upstream does not
present a licence to interpret; it presents an instruction to ask — "free to use for academic
purposes", with non-academic use of the NutritionFacts.org material directed to contact the author
personally. Rag.NET is an open-source library that commercial users consume, so calling this
benchmarking "academic purposes" is a stretch, and proceeding would mean ignoring an explicit
request rather than reading a permissive licence. **The other half is that its value collapsed the
day before.** NFCorpus was wanted for domain diversity and graded relevance; TREC-COVID is also
biomedical and landed the graded-gain criterion on 2026-08-12, so NFCorpus would now buy overlap at
the price of an unresolved permission. If it is ever wanted, the route is to email Dr. Greger, not
to reinterpret the sentence.

> **This row previously claimed the NutritionFacts.org content is CC BY-NC 4.0. That was wrong**, or
> at least unverifiable: the upstream page says no such thing — it directs non-academic users to the
> site's Terms of Service and to the author. Corrected 2026-08-12 by reading the page rather than
> the note. **That is the fourth licence claim in this repository to come from a convenient
> secondary source and fail against the primary one**, after SciFact's HuggingFace `cc-by-sa-4.0`
> tag, ArguAna's dead BEIR homepage, and TREC-COVID's identically wrong `cc-by-sa-4.0` tag.
> Heidelberg also describes 9,964 PubMed documents against 3,244 NutritionFacts.org queries, where
> this row says ~3.6k documents — BEIR repackages a subset, so the two are not the same corpus and
> the figures should not be quoted interchangeably.

**"Other projects use it" is not a licence, and it is worth saying why.** EnronQA's underlying Enron
emails are FERC public record and widely treated as usable; EnronQA's contribution is the generated
question-answer pairs on top, and that derivative work declares nothing. Absent a declaration,
copyright's default is that the author retains all rights — so the number of repositories depending
on it says something about the community's risk appetite and nothing about the grant. The unblocking
move is to ask the author to add a licence, which would settle it for everyone rather than for us.

Three things worth carrying forward:

- **TREC-COVID's graded-relevance claim survives scrutiny, unlike FiQA's.** NIST states it
  verbatim — *"judgment is 0 for not relevant, 1 for partially relevant, and 2 for fully
  relevant"* — and the BEIR qrels preview shows 0, 1 **and 2**. So it genuinely would be the
  first dataset to exercise `IrMetrics`' `2^rel − 1` path, and the Milestone 5 DoD criterion that
  depends on a graded dataset has a real candidate rather than a mistaken one. Its TDM-only
  corpus licence permits exactly what this repo does — cache locally, benchmark, publish figures —
  and forbids redistribution, which this repo does not do.
- **Do not cite the BEIR HuggingFace cards as licences.** Both `BeIR/nfcorpus` and
  `BeIR/trec-covid` are tagged `cc-by-sa-4.0`, and both tags are contradicted by upstream —
  BEIR's own paper admits *"the authors of 4 out of the 19 datasets (NFCorpus, FiQA-2018, Quora,
  Climate-Fever) do not report the dataset license."* The tag is a blanket repo tag, not a
  determination. This is the third time in two days that a convenient secondary source has been
  wrong where the primary one was clear.
- **Nothing upstream offers a semantic version for any of the three.** HF revision hashes plus
  BEIR zip MD5s are the only pins that exist, so that is what `BeirReproduction` must pin.

NFCorpus is the one needing a call: "academic use only" plus CC BY-NC underlying content is
almost certainly satisfied by benchmarking and publishing figures, but this is an open-source
library that commercial users consume, and the restriction should be documented rather than
waved through.

### Phase 5.4: Precision@k and MAP [status: implemented 2026-08-09, #75]
**Goal:** Add `Precision@k` and `MAP` to `IrMetrics`. (Not a features.md row — two missing IR
metrics.)

Verified against the source on 2026-08-03 rather than taken from the handover: `IrMetrics`'
public surface is exactly `NormalizedDiscountedCumulativeGain`, `Recall`, `ReciprocalRank` and
`Evaluate` — no precision, no MAP. Small — two methods plus hand-computed pinned values per
`IrMetricsTests`' convention, and MAP's judged-query exclusion rule must match `Evaluate`'s,
which is where the one subtlety lives. It belongs with whichever phase first needs to compare
against a published figure stated in either metric — 5.3's EnronQA baselines or 5.2's multi-hop
suites are the plausible triggers — but it is recorded as a phase anyway so the work has an
owner, rather than becoming a slot nobody owns: this list's own history says an unowned small
task is how a debt turns into an open note.

**Closed 2026-08-09 (#75).** `Precision` and `AveragePrecision` landed with hand-computed pinned
values, and `IrEvaluation` gained `Precision` and `MeanAveragePrecision` **appended**, so the
positional reads of every existing caller still mean what they meant. The predicted subtlety was
the real one: MAP's denominator is `min(k, relevantCount)`, matching `Evaluate`'s judged-query
rule rather than dividing by `k`.

A contradiction found while pinning the values, and settled separately in **#77**: this file and
`IrMetrics`' doc comment disagreed about whether FiQA is graded. It is **binary** — verified by
reading every qrels row rather than by sampling: train 14,166, test 1,706 and dev 1,238 rows all
score 1. The doc comment was wrong; `2^rel − 1` still has no graded dataset to exercise it, so
the Milestone 5 DoD criterion that depends on one remains open and is now recorded honestly
instead of resting on a dataset that would never have exercised it.

### Phase 5.5: Tier 3 Suites [status: recorded — deliberately not scheduled]
**Candidates, with the handover's reasoning kept:** CRAG (Meta, KDD Cup 2024 — the handover's
pick for the most credible single headline number a RAG library can publish), RAGBench,
LegalBench-RAG, FinanceBench, T²-RAGBench.

**None is scheduled until 5.1–5.3 land — a milestone that lists everything schedules nothing.**
This entry exists so the candidates and the reasoning survive without being dressed up as
commitments; whichever is picked up first gets a real phase entry with a scope and a number, the
way every scheduled phase on this roadmap has, and the DoD's first box deliberately excludes
this one.

## Milestone 6: Hardening & v1.0 — Battle-Tested [status: active — opened 2026-08-15 from `docs/plans/2026-08-15-milestone-6-battle-tested-replan.md`, the day Milestone 5 was audited and archived; two of the re-plan's decisions are the operator's and open]
**Goal:** Exercise every package beyond fakes — or record, per package, exactly why that cannot
be done and what it leaves unverified — and then, and only then, tag v1.0. The terminal
milestone, created 2026-08-03 when the tag was postponed out of Milestone 4: this project's own
record says defects are found by running the real thing for the first time, not by the tests
that already pass, so the release comes after the work that runs real things. Milestone 4 keeps
its number, its phases and its gates as the shipping-readiness work; Milestone 5 is unchanged;
this milestone carries the hardening and the tag.


> **Re-planned before it opened, 2026-08-15, at the operator's request** — *"make Milestone 6
> about testing every available feature so we are battle-tested"* — from
> `docs/plans/2026-08-15-milestone-6-battle-tested-replan.md`. Milestone 5's last week is why: a
> package that was `VerifiedBy=unit`, `✅ Done`, green and published had eight defects found by
> running it once, and the dense path — calibrated against four published figures — is the
> counter-example whose every defect was found *by* that calibration. The unit that matters is the
> **feature**, defined per kind so the criterion can be false: retrieval techniques get a pinned
> figure with a control; answer engines an accuracy through the 5.2.2 harness; vector stores must
> reproduce the SciFact parity figure through themselves; parsers/chunkers a real file with shape
> assertions; live services a recording or a stated reason; pipeline plumbing a parity test.
> Two decisions the note leaves to the operator, **both now closed, and differently**.
> **The recordings question was decided 2026-08-20, against the note's recommendation**: 6.1's
> recordings **do** gate v1.0, with 6.1's *work* postponed behind 6.2.3. The note had argued for
> `<VerifiedByReason>` instead, because a criterion satisfiable only by credentials that may never
> arrive is not falsifiable; that trade-off was raised at the time and accepted, so v1.0 now waits
> on 18 cassettes whose blocker is accounts rather than effort. `<VerifiedByReason>` stays
> implemented and enforced — it is no longer the answer for the connectors. **The #247 / #239
> question was settled by events rather than by a decision**: both are on `main` as of 2026-08-18
> (#311, #312 for #247; #296, #291 for #239), so they ship unless someone reverts them. Recorded
> because a decision nobody made is worth distinguishing from one that was taken.
> **"Find all bugs before going 1.0" is the intent behind this milestone, and it is deliberately
> not its Definition of Done.** "No bugs remain" is not a claim this — or any — milestone can
> make: nothing can check it, it can only be falsified by the next defect, so a milestone
> promising it can never honestly close. Milestone 3 spent sixteen phases learning to prefer
> criteria that can be false and are checked by something, and this milestone is written in that
> style. What **is** claimable, and is claimed below: every package has been exercised beyond
> fakes, or carries a recorded reason why not — per package, machine-readable, honest about what
> stays unverified. A defect can still ship in v1.0; what cannot ship is a package nobody ran
> and nobody said so.

**Definition of Done** (in Phase 4.0's falsifiable style — every criterion below can be false,
and something checks it):
- [x] Milestones 4 and 5 complete — their own DoDs, checked at their own closes, not
      re-litigated here; this box is false while either is open *(both closed by audit: 4 on 2026-08-11, 5 on 2026-08-15)*
- [ ] All planned phases complete — 6.0 Inventory, 6.1 Recorded Responses, 6.2 Raise the Floor,
      6.2.1 Retrieval & Answer Sweep, 6.2.2 Requested Features, **6.2.3 Corpus-Level RAPTOR**
      (added 2026-08-20; gates v1.0 — see #331), 6.3 Release v1.0
- [ ] **Every `✅ Done` row in `features.md` names what exercises it** (Phase 6.0): an
      *Exercised by* column pointing at a test or benchmark that runs the real thing — a pinned
      figure, a container suite, a recording, a real-file test — and a conventions test that fails a
      ✅ row whose column is empty. On 2026-08-15: 56 rows, 0 pointers. Checked, not asserted.
- [ ] **Every retrieval technique and answer engine has a pinned figure with a control** (Phase
      6.2.1): the GraphRAG method — a real corpus, a real model, a control it is differenced against,
      a `BeirReproduction`-style pin at ±0.005 — applied to RAPTOR, HyDE, hybrid, reranking, late
      chunking and SPLADE; the three answer engines through the 5.2.2 harness against MultiHop-RAG's
      gold answers; every vector store reproducing the SciFact parity figure through itself; and a
      **pipeline-parity** test holding a real `AddRagNet` pipeline to the harness's top-k on every
      push. A feature measured and found wanting passes this box; a feature nobody ran does not.
- [ ] **Every package talking to a live service has either a scrubbed, dated recording or a
      recorded reason** (Phase 6.1): `VerifiedBy=recorded` backed by committed fixtures, or
      `VerifiedBy=unit` with a `<VerifiedByReason>` beside it in the csproj naming the service,
      why no recording exists, and what that leaves unverified. Enforced the way the ledger
      already enforces declaration — the conventions test fails a live-service package with
      neither — so the gap is visible per package instead of blocking the release on
      credentials that may never arrive. A live-service package with neither fails; that is
      what keeps this criterion falsifiable where its Milestone 4 predecessor was not.
- [ ] **No package remains at `VerifiedBy=unit` without a stated reason** (Phase 6.2): every
      package is upgraded past `unit` under the definition 6.2 settles — its ledger value says
      so — or carries a `<VerifiedByReason>` stating why it stays. A bare `unit` fails; the
      check is the same ledger test, extended.
      *(**The definition 6.2 settled, 2026-08-16, and it needed a new ledger value.** This
      criterion always said "under the definition 6.2 settles" and deliberately did not name the
      levels; the sibling criterion in `MILESTONE.md` did enumerate them, and that enumeration was
      wrong once the definition existed. `integration` — exercised against something real that is
      not an external service: a real file this repository did not generate with the library that
      reads it, a real process, a real host over its real transport, a real storage engine
      surviving a reopen. Weaker than `container` and `benchmark` by design; stronger than `unit`.
      It does not mean "well tested" — it means the thing under test was not replaced by a
      substitute of itself, which is the distinction every defect in this repository's record
      turned on. Twenty-five packages moved to it on the day it was added, taking the allowlist
      from 57 to 32.)*
- [ ] **The release commit is green on both operating systems `ci.yml` matrices** — one
      required check per OS, `build-test (ubuntu-latest)` and `build-test (windows-latest)` —
      and the Docker tier and the latest nightly are green on Linux, the one OS they run on by
      design, stated as such rather than counted as both. Milestone 3 closed minutes early on
      a suite that was red on Windows while the Linux nightly was green; this criterion exists
      so that cannot recur at the tag.
- [ ] Release tagged v1.0

### Phase 6.0: The Inventory [status: complete 2026-08-15 — added and closed the same day; the two guards ship failing-behind-a-work-list: 57 packages at bare unit and 49 Done sections without an exercise pointer, each owned by 6.1, 6.2 or 6.2.1]
**Goal:** turn "battle-tested" from a mood into a list with checkboxes before any of 6.1–6.3
starts. Three things, all mechanical and all checked: (1) the **per-kind definitions** from the
re-plan, written into `docs/reference/features.md`'s preamble so every later row is held to a
stated bar; (2) an **Exercised by** column on that table — a pointer to the test or benchmark
that runs the real thing — plus a conventions test that fails any `✅ Done` row whose column is
empty, and the ledger values `recorded` and `benchmark` beside `unit` and `container` with the
existing ledger test extended to fail a bare `unit`; (3) a **first classification** of all 56 ✅
rows and 71 packages as *exercised* (pointer already exists — the four BEIR datasets, the nine
container packages, the E2E suites), *plan* (a phase below will supply it), or *declared*
(`<VerifiedByReason>`, with the reason). The two guards ship in this phase **failing**, behind an
allowlist that 6.1–6.2.1 empty; a guard that only turns on when everything already passes is the
inert-guard shape this repository keeps deleting.

**Exit condition:** the column exists, both guards run on every push, every ✅ row and every

**Done 2026-08-15, the day it was added.** Both guards are in `Rag.NET.RepoConventions.Tests`, on
every push, and both **fail behind a work list rather than pass by default**:

- **`PackageVerificationTests.NoPackageStaysAtBareUnit`** — the ledger gained `benchmark` and
  `<VerifiedByReason>`; a `unit` package with neither a better level nor a reason must be in
  `PackagesAllowedToStayUnit`, keyed by name, valued by the phase that owes it a run; the staleness
  twin fails an entry the moment its package leaves bare `unit`. **Five packages earned
  `benchmark` from Milestone 5's measurements and left the list on day one** — `Embeddings.Onnx`
  (every BEIR figure), `Reranking.Onnx` (the +reranker cell), `Chunking` (the Real protocol on all
  five corpora), `Graph` and `GraphRag` (the corpus and answer runs) — each csproj naming its pin.
  **57 remain**: 24 under 6.1 (live services), 30 under 6.2, 3 under 6.2.1 (`AnswerEngines`,
  `QueryTechniques`, `Raptor`).
- **`FeatureExerciseTests`** — every ✅ Done section of `features.md` carries
  `**Exercised by:** kind — text naming a test or benchmark class`, kind ∈ benchmark / container /
  test / recorded / declared, the class checked to exist under `tests/` or `benchmarks/`; the
  preamble defines what each kind is allowed to mean. A section without the line must be in
  `SectionsAwaitingExercise` under its phase; the staleness twin fails an entry whose section gains
  the line. **51 Done sections: 2 exercised** (ONNX Cross-Encoder Reranking, GraphRAG), **49
  owned** — 18 under 6.2.1, 20 under 6.2, 11 under 6.1.

Two things the inventory found that the re-plan did not know: the vector-store sections say
"Delivered" rather than "✅ Done" and so sit outside both feature guards — 6.2 normalises the status
lines; and the ONNX reranker's only figure is the parity-corpus cell whose Recall@10 is frozen by
construction (3.15's own note), so its `benchmark` is honest and thin until 6.2.1 re-measures it
under the Real protocol. Nothing was exercised in this phase, as its goal said; the ledger stopped
being a feeling.

package is in exactly one of the three states, and the allowlist is the work list for the rest of
the milestone. Nothing is exercised in this phase; it is where the ledger stops being a feeling.

### Phase 6.1: Recorded Responses [status: postponed 2026-08-20, **still gating v1.0** — sequenced behind 6.2.3 at the operator's call; the gate was kept rather than handed to `<VerifiedByReason>`, so the tag waits on the remaining 18 cassettes and their accounts. Previously: in progress 2026-08-17 — the harness did not work and now does (#290): recordings were written to a directory replay never read, and every mapping matched on the ephemeral WireMock port, so nothing anyone recorded could ever replay. Both defects were silent because record mode proxies to the real service and therefore passes. First working cassette recorded the same day (GitHub, unauthenticated, 17 KB); #283 carries the corrected instructions for the remaining 18 services, where the blocker is accounts rather than work]
**Goal:** For each of the ~20 packages that talk to live services, either commit a scrubbed,
dated recording of one real exchange that the tests replay, or record per package why no
recording exists. (Moved out of Milestone 4 on 2026-08-03 with the v1.0 postponement; the phase
is `docs/plans/2026-08-02-milestone-4-replan-design.md` §3, which Milestone 4's replan note
referenced "by design section rather than number until it is scheduled" — this is the
scheduling.)

The ~20: the twelve SaaS connectors (Jira, Slack, Notion, Gmail, Confluence, Asana, Airtable,
Bitbucket, Zendesk, Teams, GitHub, GitLab), the cloud vector stores, and the hosted LLM and
reranker providers. Each is hit once by hand, its real HTTP exchange recorded, scrubbed, and
committed as a fixture the tests replay — so the tests prove the code handles what the service
actually returns rather than our belief about it, the shape that let a hand-written cassette
agree with the reranker defect. §3's three requirements carry over unchanged:

- **Scrubbing is a correctness property, not hygiene**: tokens, cookies, account ids and
  customer data removed before commit, and a test asserts no committed fixture matches a
  credential pattern. A leaked token in a fixture is worse than no fixture.
- **Recordings state when and against what version they were taken.** Staleness is not
  detectable from inside a recording, so it is metadata, reviewed at release.
- **A recording is evidence of one exchange, not of the API.** The ledger says `recorded`,
  never `live`, and the difference is meaningful.

**And a fourth, which is what let the criterion move here at all: where credentials do not
exist, the reason is recorded per package instead.** The owner does not have accounts for all
twenty services, and a criterion satisfiable only by credentials that may never arrive is not
falsifiable — it is permanently false, and would have blocked v1.0 indefinitely. So such a
package stays `VerifiedBy=unit` and gains a `<VerifiedByReason>` naming the service, why no
recording exists, and what that leaves unverified; the same conventions test that enforces
`<VerifiedBy>` fails a live-service package with neither a recording nor a reason. The gap
stays visible and machine-readable — the release ships with a stated boundary, not a silent
one.

Debts that land here, each already pointing at this phase by design section: the Azure Document
Intelligence live half (`RAGNET_DOCINTEL_ENDPOINT`/`_KEY`, never run — its `TestGateTests`
satisfiability half stays Milestone 4's, per that debt's entry), the AzureAISearch OData filter
path (no integration coverage — a simulator limit), and the Pinecone live sparse-write
verification (Milestone 2's documented coverage gap).

### Phase 6.2: Raise the Floor on Unit-Only Packages [status: substantially complete 2026-08-17 — 57 bare `unit` down to 22, the remainder being 6.1's credential-blocked connectors, `Chunking.Templates` and 6.2.1's three. Every package picked up produced a defect in something adjacent: `Parsers.Audio` was filed as needing a hosted transcription model and needs none — Whisper runs locally — while being broken on Linux without `libgomp1`; `Parsers.Vision` had no CI tier that could select it; `DataProviders.Web`'s crawler returned the seed page twice (#288). `recorded` and `live` were both used for the first time]
**Goal:** Decide what "exercised beyond fakes" means for a package with no external dependency,
and do it. Phase 4.0's ledger measured **61 of 71 packages at `VerifiedBy=unit`** — only ever
exercised against fakes. About 20 of those are 6.1's live-service packages; the other **~41 —
parsers, chunkers, stores, utilities — are not**, and for them "hardening" has no definition
yet. This phase supplies one, from evidence rather than taste.

**The evidence, stated here so the phase's design session starts from it.** What actually found
defects in this project — which is unusually well documented on exactly this question:
- **Late chunking** was inert from Phase 1.1 until Phase 3.7 provisioned a real model — found
  by *running the real thing for the first time*.
- **The default chunker emitting one chunk per word** — found because embedding-cost arithmetic
  did not add up.
- **`OnnxReranker` destroying 26% of every document as `[UNK]`** — found because a *stated
  prediction was contradicted in a specific direction*.
- **Three `BeirDatasetCache` races and a Windows rename hazard across three classes** — found
  only when a workflow ran on a cold cache, and then on a second operating system.
- **Two false `features.md` claims** — found by a mechanical check comparing documentation to
  code.

**Not one was found by adding another unit test to a package that already had some**, and the
phase must design accordingly. Candidates worth naming, none settled here: property and fuzz
testing, where inputs are generated rather than chosen; differential testing against a
reference implementation; and exercising each package once against something real — a real
model, a real file, a real filesystem, a second operating system. The design decision is the
phase's own first task; this entry states the question and the evidence, not the answer.

**Exit condition:** no package remains at bare `VerifiedBy=unit` — each is upgraded under
whatever definition this phase settles, or carries a `<VerifiedByReason>` stating why it stays.
That is Milestone 6's second DoD criterion, and this phase owns it.

### Phase 6.2.1: Retrieval & Answer Sweep — the GraphRAG method, applied to the rest [status: active 2026-08-20 — added 2026-08-15 by the re-plan; a sub-phase so 6.3 keeps the number every release note already points at. Two of its four named debts are now closed: #239 and #200 on 2026-08-17, **#247 on 2026-08-18** — fixed twice over, #311 hiding graph chunks from retrieval by default and #312 giving them their own store, pinned at 0.3494 in #280. **All four of its named debts are now closed**: #239 and #200 on 2026-08-17, #247 on 2026-08-18, and **#176 answered 2026-08-26 in #405** — not by driving the number down but by naming the dropped endpoints, which turn out to be common nouns and paraphrases rather than entities the extractor missed, so the singletons are honest and the obvious fix would trade a better number for a worse graph. The full-corpus reading stands at 2,816 of 3,573 (**78.8%**) against the 65% the issue carries, and that is now a documented property rather than an open debt. The local-search thread completed 2026-08-20 (#323, #326); the sweep itself has not started. **The RAPTOR measurement's Tasks 4-6 unblocked 2026-08-22** when #345 merged in #351 (`bb4c11c7`, verified on `main` by content): before it, `SelectClusterCount` capped every level at `SelectK(maxK: Min(count, 10))`, so over MultiHop-RAG's 17,648 chunks the largest level-1 cluster held at least 1,765 chunks (~183k tokens) against `gpt-4o-mini`'s 128k context and the corpus tree could not be built at the shipped default at all]
**Plan:** `docs/plans/2026-08-19-graphrag-local-search-completion-implementation.md` — the GraphRAG
local-search thread only (spec sub-phases 6.x.1, 6.x.6, 6.x.7), not the whole sweep. **Complete
2026-08-20.** Tasks 1–5 merged in #323; Task 6's measurement ran the same night and is pinned as the
`localspec` arm in `MultiHopRagAnswerReproduction`.

**And it revises Milestone 5.2's published finding.** 5.2 concluded "GraphRAG does not help on this
corpus" from the `local` arm's 0.2102 — a PageRank blend over dense candidates, which is not in
Microsoft's local search at all. Measured properly: **0.3459 overall and 0.8603 on inference**,
+0.1357 and +0.3983 against that blend, and the **strongest entity-question result this project has
measured** (above global's 0.8444 and dense's 0.7721). It is level with dense overall (−0.0040)
because it abstains on yes/no questions — committing on 8.8% of comparison and 4.3% of temporal —
while abstaining on only 34.6% of the unanswerable nulls against dense's 48.5%. Generation pass
50 m 21 s; the reproduction replayed 2,556 answers from cache, generated zero, and agreed row for
row. Caches cost ~$9 once: 35,112 extraction calls and 3,573 community reports over the full
609-article corpus.

Two things this run produced that are **not** this plan's business and need their own homes:
**#176 is worse than recorded** — the full corpus gives 2,816 singleton communities of 3,573,
**78.8%**, against the 65% #176 carries; and local search's **yes/no abstention** is a
characterisation nobody has explained, where global search scores 0.4953 and 0.3928 on the same
questions. Covariates (6.x.5) and every other technique in this phase's goal below still need plans
of their own.
**Goal:** every retrieval technique and every answer engine gets what GraphRAG got in Milestone 5:
one real run on a real corpus with a real model, differenced against a control, pinned, and read
honestly. **#247 was first, and is done** (2026-08-18) — the shared-store policy for graph-derived
chunks was the largest measured cost in the programme (−0.043 nDCG, −0.21 answer accuracy) and the
one fix every measurement pointed at. Option (c) was measured first (#274: the `filtered` arm
reproduced article-only accuracy to four decimals, so the whole loss was displacement), pinned at
0.3494 (#280), then shipped as behaviour — #311 hid the graph's chunks from retrieval by default,
#312 gave them their own store. The question 5.2.2 could not answer — can local search beat dense
when it is not starving the model — got its number on 2026-08-20: **0.3459 overall, 0.8603 on
inference**, level with dense overall and the strongest entity-question result measured here.
What remains, in whatever order the pilot costs suggest: **RAPTOR first** (same store shape as
GraphRAG, likely the same finding, and #247's fix is the template — the discriminator, the
over-fetch-and-drop, and the separate-store option are all now built and measured once), then
HyDE and reranking (already have parity-corpus
cells — re-measure under the Real protocol), hybrid BM25, late chunking, SPLADE; the three answer
engines (MapReduce, Refine, FLARE) through `BeirGraphRagAnswerTests`' harness as arms; every vector
store through the SciFact parity leg, reproducing 0.64593 ± 0.005 through itself; and the
**pipeline-parity test** — the same query through a real `AddRagNet` pipeline and through the
harness, top-k identical, on every push — which closes the gap 5.2.2 named. #239's blend and
traversal are decided here (rescale, drop, or use), with the ablation numbers in hand. #176 and
#200 rode along and are both closed — #200 on 2026-08-17, #176 on 2026-08-26.

**What it costs:** one BEIR run per retrieval feature (minutes to an hour, embeddings cached); one
answer arm per engine (~$3 derived each, replayed after); one container run per store; a fast-tier
test for parity. What it does not promise: that any of them are good. Measured is the bar.

**Exit condition:** every row 6.0 classified as *plan* has its pointer and its pin; ~~#247 is fixed
and re-measured~~ (met 2026-08-18); ~~the pipeline-parity test is in the fast tier~~ (met
2026-08-27 — **both legs now run and pass as of 2026-08-28**, see below); the
guards' allowlist is empty.

**Open threads, 2026-08-20** — what is actually left, now that #247 and the local-search work have
closed: ~~RAPTOR~~ (**complete 2026-08-27**, Tasks 1-6 — measured, pinned, and written down; see
below), ~~HyDE~~ (**SciFact measured 2026-09-02 in #433**, 0.71389 against the Real control's
0.67742, +0.03647; **FiQA measured the same day**, 0.34683 against 0.35569, −0.00886, so the sign is the corpus rather than the technique; ArguAna and TREC-COVID unrun and each now measurable alone — see below), reranking
(**SciFact and FiQA measured 2026-09-02 in the same PR**, +0.01266 and −0.00951; ArguAna and
TREC-COVID unrun), hybrid BM25, late chunking and SPLADE under the Real protocol; ~~the
three answer engines as arms~~ (**built 2026-08-28** on `feat/answer-engine-arms`, not yet merged —
five arms, `flare` included; no pilot run; see below); every vector store through the SciFact parity
leg; ~~the
pipeline-parity test~~ (**fast leg complete 2026-08-27**, in the fast tier and green on every push;
both legs measured 2026-08-28; see below); ~~**#176** at its re-measured 78.8%~~ (**answered
2026-08-26 in #405** — not
a defect worth fixing; see below); **local search's yes/no abstention** — it
commits on 8.8% of comparison and 4.3% of temporal questions where global search scores 0.4953 and
0.3928, which nobody has explained; and ~~the **deletion of `GraphLocalSearchBehavior` and
`PageRankWeight`**~~ (**done 2026-08-27 in #408** — see the bullet below).

**RAPTOR's thread is complete, 2026-08-27 — the first of the sweep's techniques to finish.** Task 6
of `docs/plans/2026-08-21-raptor-real-protocol-implementation.md` closed the ledger the Task 5
measurement had earned two days earlier: `docs/guide/raptor.md` gained a **Measured** section,
`Rag.NET.Raptor.csproj` went `integration` → **`benchmark`**, and `features.md`'s RAPTOR row moved
off *"Not yet `benchmark`"* onto `MultiHopRagAnswerReproduction`, where the four arms are
machine-asserted. Zero compute — the run was cached; what was missing was the writing-down.

**The measured finding, stated as the bar requires rather than as the feature would like:** over the
**2,255 judged queries**, `raptor` (per-document control) **0.3734**, `raptorcorpus` (the shipped
default) 0.3588, `raptorfiltered` (the gate) 0.3499, `raptorboost` 0.3450. The gate held — it
reproduces the dense arm's pinned 0.3499 / 0.2603 / 0.3242 to four decimals — so the corpora did not
diverge. Summaries buy `+0.0089` over leaves alone (p=0.0293). **And corpus scope, 6.2.3's breaking
change, measured −0.0146 paper (p=0.0247) and −0.0204 raw (p=0.0006) against the per-document tree
it replaced**, the whole gap sitting in inference queries (0.7831 against 0.8309) — the exact
multi-hop case #331 argued corpus-spanning summaries would help.

**The default was not reverted, and the guide says why rather than just what.** Per the 2026-08-27
decision (option 3), `Corpus` stays as a *hold*: MultiHop-RAG composes its questions from
identifiable source articles, so a per-document tree is measured on home ground, and reverting a
breaking default on the least neutral evidence available is the wrong move. The guide gives the
reader the corpus-shaped rule instead — if your questions resemble MultiHop-RAG's, set
`PerDocument`; if your documents genuinely share themes, `Corpus` is what the paper describes;
either way measure your own corpus. **A second-corpus arm is the scheduled work that settles it**,
and it needs a dataset whose questions are not constructed per document.

**What this does not do is complete the phase.** RAPTOR is one technique of a sweep that still owes
HyDE, reranking, hybrid BM25, late chunking, SPLADE, ~~the three answer engines as arms~~ (**built
2026-08-28**, not yet merged, not yet run; see below), every vector
store through the SciFact parity leg, ~~the pipeline-parity test~~ (**fast leg complete 2026-08-27**;
see below), the second-corpus RAPTOR arm, and local search's unexplained yes/no abstention.

**The pipeline-parity test's fast leg is complete, 2026-08-27 — the DoD clause 6.2.1's exit
condition names explicitly.** `OrderingEmbeddingGenerator` is a deterministic fixture embedder built
geometrically rather than hashed: text *i* of *n* sits at angle *i·δ*, δ = π/(2(n+1)), so cosine
against the query is strictly decreasing and no two documents can tie — its own guard test asserts
determinism, injectivity and strictly-decreasing pairwise-distinct scores, because 6.2.3's
`new Random(123)`-inside-the-callback mock is exactly the failure shape a degenerate embedder would
repeat here, invisibly, and this project has paid for that shape before. `PipelineParity` builds a
fresh default `AddRagNet` container over a caller-supplied store and embedder, runs
`IRagPipeline.RetrieveAsync`, and compares chunk-level against the harness's own dense row with
**exact** score equality — no tolerance, because both sides call the same `SearchAsync` on the same
store instance, shared by identity so indexing never becomes a second variable — and reports the
first differing rank with both ids and both scores. `PipelineParityTests` has two legs: a fast leg
over a synthetic six-document corpus that runs on every push and **passes**, and a real leg that
runs SciFact through `AblationRow.Dense` over the same shared store, at `BeirHarness.Cutoff` (10),
gated on provisioning only rather than on `RAGNET_BEIR_LONG_RUNS`.

**The fast leg runs six queries, not one — and the reason is the mutation check below.** A final
review caught it running only the query at angle 0, which is *identically* document 0's vector, so a
whole class of reordering cancels out there; a leg pinned to that one angle can be green while the
pipeline has already drifted for every other query. The fixture now also carries one query per
adjacent document pair, at angles *j·δ + δ/3* — each a distinct multiple of *δ/3* from every
document, so still tie-free, and sitting on no document. The guard test asserts exactly that: only
`QueryText` coincides with a document vector, and it does so deliberately, because its hand-checked
ranking `doc-0#0 … doc-3#0` is the leg's pin against two identically-wrong rankings agreeing.

**`UseMmr` was re-run as a mutation against the six-query leg, and it now fails** — `Rank 1 differs
for 'the parity query, a third past document 0' — pipeline doc-5#0 (0,5000000596046448) vs harness
doc-1#0 (0,9888308644294739)` — while the `QueryText` query alone still passes under it. That is the
finding stated as evidence rather than as argument: the single-query leg was green against a
behaviour that stops no-opping the moment the query moves off document 0. (The leg fails at the
first mismatch, so what is measured is the second query, not all six.)

**The mutation check was run, and it failed on purpose, with this verbatim message:** `Rank 1
differs for 'the parity query' - pipeline doc-2#0 (0,9009689092636108) vs harness doc-1#0
(0,9749279618263245). Either a default retrieval behaviour stopped being a no-op, or the harness's
dense path changed. If the behaviour change was deliberate, every pinned figure now describes
something the shipped pipeline no longer does, and the figures - not this test - are what need
attention.` **The plan's suggested mutation, `UseMmr`, did not work, and why matters**:
`OrderingEmbeddingGenerator`'s query vector is placed identically to doc-0's vector by construction,
so MMR's relevance and diversity terms cancel exactly and MMR reproduces the harness's order — a
*mathematical* no-op on this fixture, not a defect in `MmrBehavior`.
`UseRedundancyFilter = true` was used instead: the fixture's six documents sit on a unit circle at
equally spaced angles, so adjacent documents carry cosine similarity ≈0.975 — above
`RedundancyFilterBehavior`'s default 0.95 threshold — and enabling the filter genuinely changes both
the count and the order the pipeline returns against the harness's untouched ranking.

~~**The real leg has never run.**~~ **IT RAN, 2026-08-28, AND IT PASSED** — 20 SciFact queries, the
real ONNX embedder, `AblationRow.Dense` against a real `AddRagNet` pipeline over one shared store,
chunk ids and exact scores identical at every rank. Both parity legs green, **zero skipped**, 90.5 s.

**The claim it had never run was true but the reason given for it was wrong, and the correction
matters more than the result.** The entry above said the machine had "no ONNX model, no BEIR dataset
cache provisioned". The cache was there the whole time, at `~/.cache/ragnet-beir`, with
`model.onnx`, `vocab.txt`, the SciFact corpus and 256 embedding shards. **The tests skip because no
environment variable points at it**, and the cache ships an `env.sh` that sets them. Three sessions
read "SKIPPED" as "this machine cannot measure" and wrote that conclusion into the planning record;
the harness was reporting the absence of a *variable*, and the sessions heard the absence of a
*corpus*. **Read a skip's reason, not its colour.**

**So the central claim of this thread is now measured rather than argued:** the sixteen retrieval
behaviours that run before `VectorStoreBehavior` really are no-ops at shipped defaults, on a real
corpus with a real embedder. Every pinned figure in this project is produced by the harness; this is
the first evidence that a user's `AddRagNet` pipeline reproduces it.

**A review caught the real leg passing vacuously, before merge.** With zero hits on both sides,
`AssertSame` would agree on all 20 queries and pass regardless of whether retrieval worked at all —
the exact failure mode the fast leg already guards against with its hardcoded expected ranking.
Fixed before merge with one assertion that touches the store: that the harness side actually
retrieved a full page of hits (`BeirHarness.Cutoff`) at every query. A second assertion, on the unit
count, was added at the same time and **removed again in the final review** — it was a tautology
(`BeirHarness.LoadAsync` already asserts the document count, and `OneChunkPerDocument` is one unit
per document) and it never touched the store, so it could say nothing about indexing.

**The fast leg satisfies 6.2.1's exit-condition clause** — *"the pipeline-parity test is in the fast
tier"* — and runs, green, on every push. **This does not complete the phase.** It still owes HyDE,
reranking, hybrid BM25, late chunking, SPLADE, ~~the three answer engines as arms~~ (**built
2026-08-28**, not yet merged, not yet run; see below), every vector store
through the SciFact parity leg, the second-corpus RAPTOR arm, and local search's unexplained yes/no
abstention.

**#176 is answered, 2026-08-26 — and the answer is that it is not a defect worth fixing (#405).**
It was answered by looking at what the dropped relationship endpoints are *called*, which nobody had
done; the counts were already understood. 853 of 16,403 relationships (5.20%) are dropped because an
endpoint resolves to no extracted entity, stranding 123 entities that do have edges, and
273 + 123 = **exactly** the 396 singletons on the pinned slice. Those 853 drops name **565 distinct**
endpoints, and they are not entities the extractor missed: `content policies` (10), `tasks` (10),
`smart plug` (9), `handy tool` (8), `film` (7), `ceremony` (6), `death` (5), `decisions` (5) — common
nouns — mixed with descriptive paraphrases of things that *are* extracted, `Falun Gong practitioners`
beside the entity `Falun Gong`, `Rachel's husband`, `Lars Mapstead's parents`.

**That rules out the obvious fix.** Promoting an unresolved endpoint into an entity drives the
singleton share down while adding 565 junk nodes named after common nouns — a better-looking number
over a worse graph, and **the singleton count is precisely the metric easiest to move without
helping anything**. The singletons are honest. What produces them is the model writing relationship
endpoints as prose descriptions rather than the canonical names it extracted, so any fix belongs in
the extraction prompt and has to be measured against *retrieval*, not against this count. **Nothing
was changed on the strength of it.** The diagnostic stays — it prints the counts and a ten-name
sample, because the names are what made the answer decidable and a future extraction change should be
readable in them. Measured by replaying the extraction cache refuse-on-miss: zero model calls.

**This closes the last of 6.2.1's four named debts** (#239, #200, #247, #176). What remains in the
phase is the sweep itself, which was never about those four.

- ~~**`GraphLocalSearchBehavior` and `PageRankWeight` are deprecated in `<remarks>`, not
  `[Obsolete]`, and not deleted**~~ — **deleted 2026-08-27 in #408** (`c3e4aa94`), verified on
  `main` by content: `GraphRagGlobalSearchOptions.cs` is present, `PageRankWeight` is gone from
  `src/`, and the frozen `LegacyPageRankLocalSearch` fixture lives in the measurement harness.
  **The three pinned figures survived, which was the whole design of the change** — they were
  re-measured *through the frozen copy before the original was deleted*, the only moment that
  comparison is possible: **0.56897/0.56897, 0.2102/0.2102, 2,255-of-2,255**, zero skips, zero model
  calls. All three are now machine-asserted; the ablation's was previously only *printed*, so
  nothing in the suite would have failed had it regressed. The change cost 21 files across five
  projects — **the estimate was wrong three times** (17 here, 19 in the design doc) because each low
  count came from grepping the two *named* members rather than the union of all seven targets, which
  is what reaches `GraphGlobalSearchBehavior` and its tests. One item is unverified and gates
  nothing: the `Rag.NET.E2ETests` GraphRag tests never ran (no Docker daemon), so their rewritten
  local-search assertion is verified by reading only. The reasoning that held the deletion back
  until 2026-08-20, retained because it is why the frozen fixture exists:
  `[Obsolete]` was rejected, not merely skipped: `Directory.Build.props` sets
  `TreatWarningsAsErrors=true`, so CS0618 would be a build error across the 17 files in four
  projects that deliberately still reference these members, and `PageRankWeight`'s
  `[Must(nameof(PageRankWeightIsFinite))]` validator has source-generated code that references the
  property and cannot be `#pragma`'d around by hand. Deleting them would make three pinned figures
  unreproducible — the `local` answer arm at 0.2102, `BeirReproduction`'s GraphRag 0.56897, and
  the blend ablation. They are unregistered from the default pipeline, where at the default
  `PageRankWeight = 0` they were a no-op. → **delete once 6.x.7 publishes the replacement
  figure**, in the same phase.

**The answer-engine arms are built and merged 2026-08-28 in #416, and a 10-query pilot has now RUN
— all three gates passed on the first attempt.** Five new arms share the existing `AnswerArm.Dense` retrieval case verbatim, so
retrieval is held fixed by construction, and vary only generation: `chatengine`, `mapreduce`,
`refine`, `flarefixed`, `flare`. `chatengine` is the control — single-shot through
`ChatAnswerEngine` over the same routing as the others — so `chatengine − dense` is the prompt
effect alone and `<engine> − chatengine` is the mechanism alone, the exact confound that cost
Milestone 5.2 three weeks and a revised published finding. **`flare` shipped**, not excluded
pending #414: #414 merged mid-implementation as `641e27f0`, verified on `main` by content
(`PipelineParity.cs` present); this branch rebased onto it, and `flare`'s lookahead now resolves a
real `IRetriever` from a real `AddRagNet` container, built the way `PipelineParity`'s tested adapter
builds it, over the harness's own shared `articles` store by identity — so the lookahead retrieves
from exactly the corpus it is measured on, not a stub. The engines build their own prompts over the
harness's shared `CachedGraphRagClient`, so their cache entries are new keys; **`PromptTemplate` and
the non-engine generation path were never touched**, verified byte-identical across three separate
reviews (Task 4's initial review, its post-fix re-review, and Task 5's independent re-check).

**Nothing has been run.** No pilot, no sweep — no arm has ever answered a real query. The machine
that built this has no ONNX model, no BEIR cache and no API key, the same gap this phase has
recorded honestly before (RAPTOR's E2E GraphRag tests and the pipeline-parity real leg, both above).
Three pilot gates live inside `Accuracy_AgainstTheGoldAnswers_ThreeArms`: context identity against
`dense` (chunk-identity comparison, fails at the first differing rank); per-arm call shape (an
`EngineCallCountingChatClient` decorator, one instance per (arm, query), so `Parallel.ForEachAsync`'s
concurrency cannot leak into the count); and lookahead observed firing in `flare` (a shared
`CountingRetriever` wrapper, read once after the parallel loop, so a scorer that fails open and never
triggers a lookahead cannot pass silently as `flare`). The gate **logic** is unit-tested with no
model and no corpus, but the gates themselves have never executed and are verified by reading only.

**Measured call shapes, from a fake client, no money spent:** `chatengine` exactly 1, `refine`
exactly 6, `mapreduce` exactly 7 — all three matched the design's reading of the engines on the first
run, nothing adjusted. FLARE's upper bound is **33** per query (`MaxSentences = 15` × 2 calls for
generation and scoring, plus up to `MaxRetrievals = 3` regenerations), corrected during review from
an initial 30 that missed the regeneration calls.

~~**Cost is derived, not measured**~~ — **the 10-query pilot measured it, 2026-08-28, and FLARE's
sentence count is the thing that collapsed.** 323 new answer-cache entries: 126 are the known
non-FLARE arms (`chatengine` 9, `refine` 54, `mapreduce` 63 — the fake client's call shapes
reproducing exactly against a real model), leaving **197 for the two FLARE arms over 9 queries, about
11 per query per arm against a ceiling of 33**. FLARE stops at roughly a third of `MaxSentences`,
which is what MultiHop-RAG's few-word gold answers predicted.

**So the full 2,556-query sweep is ~36 calls per query across all six arms — roughly 92,000 calls,
on the order of $5–10**, against a derived range of ~$4 to ~$21. The middle, not the ceiling.

**Two caveats on that number.** Cache entries are a **lower bound** on calls — identical prompts
collapse to one entry — so 11 is "about a third of the ceiling" rather than a precise count; the
per-arm counters printed by `DescribeEngineArmCosts` remain the authority and were not captured here
because xunit does not print a passing test's output. And nine queries is a thin basis for a
per-query rate: longer questions may generate more sentences.

**The pilot cost pennies.**

**THE PILOT FOUND A DEFECT IN THE DESIGN, AND IT IS WORSE THAN THE CONFOUND THAT WAS PREDICTED.**
The design warned that the scoring rule "may punish format rather than reasoning, and that would look
exactly like a finding", and required the pilot to read answers rather than scores. It was right to,
but the mechanism is sharper than *style*: **the inline `PromptTemplate` carries an extraction
contract the judge depends on, and the engines were never given it.**

`MultiHopRagAnswerJudge.AnswerInstruction` asks the model to *"End your reply with exactly this
sentence, filling in the answer: The answer to the question is \"...\""*, and the judge reads the
answer out of that sentence — **falling back to the whole reply trimmed when it is absent**. Measured
over the 9-query pilot:

| arm | met the extraction contract |
| --- | --- |
| `dense` | **9 of 9** |
| `chatengine`, `mapreduce`, `refine`, `flarefixed`, `flare` | **0 of 9, every one** |

So on the inference query whose gold is `Donald Trump`, `dense` answers **"Trump"** and scores
correct while every engine answers *"The individual implicated in both inflating the value of a
Manhattan apartment…"* and scores wrong. **The engine arms were not measuring engines.** They were
measuring whether a gold word happened to appear anywhere in a discursive paragraph, and their
accuracy figures from that run are uninterpretable.

**The instruction belongs to the measurement apparatus, not the product.** A real
`MapReduceAnswerEngine` user has no reason to end with that sentence; this harness's judge has to be
able to find the answer, and the `dense` arm has carried the same instruction inside `PromptTemplate`
all along. **Fixed 2026-08-28** by passing it as `RagOptions.SystemPrompt` to every engine arm — all
four engines honour it, so it reaches every call each makes, including MapReduce's per-chunk maps and
Refine's rewrites. It changes every engine prompt and therefore every engine cache key, orphaning the
323 entries the first pilot wrote; that is the right trade, because they answer a question nobody
asked.

**The design missed this because it treated the inline prompt as untouchable and never asked what was
in it.** "Do not edit `PromptTemplate`" was correct — it is a cache key — but it was read as "the
engines need nothing from it", when half its content is the contract the judge runs on. The rule that
generalises: **when one arm is exempted from a shared apparatus, check what the apparatus was doing
for it.**

**No accuracy headline is published from this pilot, deliberately.** Nine judged queries: `dense`
3/9, `chatengine` 3/9, `mapreduce` 5/9, `refine` 5/9, `flarefixed` 5/9, `flare` 4/9 — **underpowered,
not a result**, recorded only so the next reader knows what was seen. RAPTOR's 50-query pilot put its
headline at +0.0000 where the full sweep found −0.0146 at p=0.0247. The one line worth keeping is
that the control landed exactly on the incumbent, which is what a sound routing should do.

**Gate 3 earned its place on the first real run.** `flare`'s lookahead was observed firing, so the
fail-open scorer did **not** silently degrade it into `flarefixed`. That guarantee was wrong in four
successive versions — a throwing stub FLARE swallows, a flag made unreadable by the stub not being
installed in the run that matters, a fresh stub allocated per call and dropped, and finally a shared
holder asserted after the run — and it is the one that has now proven itself against real data.

**A caveat on what the eventual figures will mean, ~~carried forward rather than fixed~~ superseded
2026-08-29 by #419 (`50221812`):** ~~`CachedGraphRagClient.GetResponseAsync`
ignores its `options` argument, so FLARE's `MaxOutputTokens = 150` and the engines' temperatures are
discarded before a call is made. The pilot will therefore price *the harness's* configuration — which
is what the sweep would run — but not what a shipped engine would send with its own options honoured.
Deliberately not fixed: `options` are not part of the cache key, so honouring them now would silently
change what every already-cached entry means.~~ **Options are now forwarded and keyed as of
2026-08-29** — `CachedGraphRagClient` merges and forwards the caller's `ChatOptions`, and
`GraphExtractionCache`'s optional third key field is omitted rather than emptied when the caller
passes none, so the cache-key worry above proved unfounded: the key change orphaned nothing, and all
86,510 pre-existing entries keep their keys (see the 2026-08-29 fix below). **Still true:** `Merge`
(`CachedGraphRagClient.cs:319`) overwrites the caller's temperature with the baseline, so the engines'
*temperatures* remain deliberately discarded — the model identity carries the baseline temperature
into every cache key.

**One finding worth carrying as a lesson.** The guarantee that `flarefixed` holds retrieval fixed —
the whole point of pairing it against `flare` — was wrong three times in three different places
before it held: first a throwing stub, defeated because `FlareAnswerEngine`'s lookahead call catches
and swallows the throw; then a recorded `WasCalled` flag, unreadable because the stub was never
installed in the one run where `flare` and `flarefixed` are co-selected; then the `EngineRetrievers`
holder that finally closed the gap. The answers were never affected by any of the three — what was
repeatedly broken was the *proof* that they weren't.

**This does not complete the phase, and Phase 6.2.1 is not marked complete.** It still owes HyDE,
reranking, hybrid BM25, late chunking, SPLADE, every vector store through the SciFact parity leg, the
second-corpus RAPTOR arm, and local search's unexplained yes/no abstention. The DoD's answer-engine
clause — *"the three answer engines through the 5.2.2 harness against MultiHop-RAG's gold
answers"* — is **not** met by building the arms; it needs the run, and the run has not happened.

**#418's fix broke FLARE, and this is the third time in this phase a fix has caused the next
defect.** 6.2.12 had #390's fix deadlock Blazor (#396), whose fix hung on host singletons (#400); now
#418 — itself the fix that finally gave every engine arm the judge's extraction contract — caused
this one. The 2026-08-29 pilot re-run, meant only to confirm #418, failed twice in a row with four
`TaskCanceledException`s each, every one inside `FlareAnswerEngine.GenerateSentenceAsync`, deterministic
rather than flaky. Neither run reached judging, so #418's own question — do the arms actually meet the
contract — stayed unanswered for a full extra day.

**The mechanism, confirmed by cached evidence rather than inferred.** One cached response from the
first failed run is **86,091 bytes**, the contract sentence *"End your reply with exactly this
sentence…"* repeated **256 times**, against a **3,747-byte** maximum across the 47,151 answer-cache
entries written before that day — 23× the historical worst case. Three things compose: #418 gives
every arm a *terminal* system prompt; `FlareAnswerEngine` replaces its own system prompt with the
caller's on every per-sentence call (`FlareAnswerEngine.cs:302`), so a terminal instruction fights the
"continue with exactly one sentence, or reply `<DONE>`" protocol the model never resolves toward
`<DONE>`; and `CachedGraphRagClient` discarded the caller's `ChatOptions` entirely, so FLARE's own
`MaxOutputTokens = 150` — written, by its own comment, to bound exactly this kind of rambling — never
reached the model. Disarming that guard is why a bad prompt became an 86 KB runaway and a timeout
instead of a 150-token oddity.

**Three fixes, merged to `main` 2026-08-29 as `50221812` in #419** — verified on `main` by content
(`FragmentProtocol` in `FlareAnswerEngine.cs`, `ThrowIfUnkeyable` in `CachedGraphRagClient.cs`)
rather than by the PR's MERGED label. **(1)** `FlareAnswerEngine` now composes a caller `SystemPrompt`
with its own fragment protocol instead of being replaced by it — a defect in a **shipped package**,
reachable by any user whose `SystemPrompt` carries a terminal instruction, the same shape as #333. (2)
`GraphExtractionCache` gained an optional third key field for caller options, **omitted rather than
emptied** when the caller passes none — appending an empty field would still have changed every
existing hash, so all 86,510 pre-existing entries keep their keys with zero regeneration. (3)
`CachedGraphRagClient` merges and forwards the caller's `ChatOptions` instead of discarding them, and
the harness applies the extraction contract once to FLARE's assembled answer after its loop rather
than to each fragment, with the call-shape gate's bounds widened to carry the extra call.

**Fix (1) is not exercised by this pilot.** The harness hands every FLARE arm `FlareLoopOptions =
new()` — no system prompt at all — so the run below validates the harness's *avoidance* of the
terminal-instruction conflict (by never handing FLARE one), not FLARE's own *composition* fix. Fix
(1) stands on its unit test, `ACallerSystemPrompt_DoesNotDisplaceTheFragmentProtocol`, alone.

**The re-run pilot, 2026-08-29: 15 tests, 0 failed, 0 skipped, 172.968 s, 469 new answer-cache
entries — generated, not replayed.** Every engine arm moved from 0 of 9 to 8 or 9 of 9 on the judge's
extraction contract:

| arm | this run | 2026-08-28 |
| --- | --- | --- |
| `dense` | 9 of 9 | 9 of 9 |
| `chatengine` | 8 of 9 | 0 of 9 |
| `mapreduce` | 9 of 9 | 0 of 9 |
| `refine` | 9 of 9 | 0 of 9 |
| `flarefixed` | 8 of 9 | 0 of 9 |
| `flare` | 8 of 9 | 0 of 9 |

**Three arms sit at 8 of 9, not 9 — stated as measured, not rounded up to "all arms now comply."** The
contract is met on the large majority of queries, not universally, and nothing in this run explains
the three misses. The longest prediction across all 54 records is **890 characters**, against the
86,091-byte response that killed the two prior runs. No accuracy headline is published from nine
judged queries (`dense` 3/9, `chatengine` 7/9, `mapreduce` 3/9, `refine` 4/9, `flarefixed` 4/9, `flare`
4/9, paper rule) — RAPTOR's 50-query pilot put its own headline at +0.0000 where the full sweep found
−0.0146 at p=0.0247, and `chatengine` at 7/9 against `dense`'s 3/9 here is exactly the kind of gap a
pilot invents.

**One finding worth recording in its own right: a passing test does not prove the contract.**
`MultiHopRagAnswerJudge.UsedTheAnswerSentence` is recorded and reported, never asserted
(`BeirGraphRagAnswerTests.cs:2896`), and xunit prints nothing for a passing test — so the per-arm
figures above were recovered from the run's own results file
(`graph-answers-results/pilot-20260829T171523Z.jsonl`), not read off the console. Full account in
`docs/plans/2026-08-29-flare-contract-pilot-notes.md`.

**This does not complete the phase, and the DoD's answer-engine clause is still not met.** The clause
needs the full 2,556-query sweep, and this pilot is not it. The phase still owes HyDE, reranking,
hybrid BM25, late chunking, SPLADE, every vector store through the SciFact parity leg, the
second-corpus RAPTOR arm, and local search's unexplained yes/no abstention.

**The full 2,556-query sweep RAN, 2026-08-30 — and its accuracy figures are not an engine
comparison.** 15,336 records across six arms, 6.5 hours, 15 tests, 0 failed, 0 skipped. Protocol in
`docs/plans/2026-08-29-answer-engine-sweep-protocol.md`; full account in
`docs/plans/2026-08-30-answer-engine-sweep-findings.md`.

**Gate 0 held exactly:** `dense` reproduced its pinned **0.3499 / 0.2603 / 0.3242** to four decimals
over the 2,255 judged queries, so the corpora did not diverge and the run is sound as a measurement.
Gates 1-3 held across roughly 15,000 assertions, and #419's fixes held at scale — no runaway.

**Then the control moved, which is the tell.** `chatengine` shares `dense`'s retrieval verbatim and
varies only the generation path, so `chatengine − dense` should be a small prompt effect. It is
**+0.4204 paper and −0.0541 raw** — enormous, and signed opposite ways by rule. Reading the answers
rather than the scores explains it: **`PromptTemplate` carries three instructions — grounding
("using only the context"), abstention ("answer exactly: Insufficient information"), and the
extraction contract — and `EngineAnswerOptions` passes only the third.** #418 found one of three and
restored it.

**The measurement is not subtle.** On the 2,255 judged queries, with context asserted identical
chunk-for-chunk, `dense` abstains **1,394 times (61.8%)** against 1-17 for the engines. On the 301
null queries, `dense` correctly abstains **146 (48.5%** — the figure already in the record,
reproduced independently**)** while **every engine arm abstains 0 of 301, five times over.** So the
+0.42 measures one sentence of prompt, not any engine mechanism. The raw-rule inversion is likewise
mundane: the engines end ~2,150 of 2,255 answers with a period where `dense` ends 513, and the raw
rule counts punctuation.

**Nothing is pinned.** `MultiHopRagAnswerReproduction` keeps the five engine arms at empty figure
arrays; pinning these would enshrine an invalid comparison as the published result, which is the
Milestone 5.2 failure exactly. **The clause remains unmet — the sweep produced a defect, not a
comparison.**

**Fourth fix-leaves-an-adjacent-gap in this phase** (#390 → #396 → #400, and now #418 → this), and
the lesson is sharper than #418's. Its rule — *"when one arm is exempted from a shared apparatus,
check what the apparatus was doing for it"* — **was followed, and came back incomplete**, because the
symptom pointed at the extraction contract and the check stopped there. The rule that generalises:
**when the shared apparatus is an inline blob of prose, enumerate every instruction in it** before
deciding what the exempted arm needs. `PromptTemplate` is one `const string` holding three separable
contracts, and being one string is what made it look like one decision.

**The re-run is deferred by the operator, 2026-08-30.** Giving the engines the full contract changes
every engine prompt and cache key, orphaning this run: ~$5-10 and ~6.5 hours again. A guard test
asserting the arms share one instruction set belongs to that re-run — it would fail today, which is
the point. Also recorded: the protocol's 3-4 hour estimate, extrapolated from the nine-query pilot's
rate, was wrong by ~2× — the second time here that a pilot rate has failed to survive extrapolation,
after RAPTOR's factor of eight.

**The contract fix was built and validated on a 400-query subset, 2026-08-30 — it fixed two arms,
broke a third and missed a fourth.** `AnswerContract` now names all three instructions and
`EngineAnswerOptions` passes the whole of it; `PromptTemplate` composes to the same bytes, so
`dense`'s cache and its pin survive. Full account in
`docs/plans/2026-08-30-engine-contract-subset-findings.md`.

**A 400-query subset ran instead of the full sweep, and paid for itself immediately.** The check was
categorical — do abstentions appear where there were zero — and a categorical check does not need
2,556 queries. ~$3 against ~$20, and it found three problems, two of them new.

**What worked:** for `chatengine`, `mapreduce` and `refine`, abstentions appeared where there had
been none — **0 of 301 before, now 13-26 of 47** — and the control anomaly collapsed:
`chatengine − dense` moved from **+0.4204 to −0.1104**. Contract compliance is 393-400 of 400 across
every arm.

**What broke: `mapreduce` collapsed to 0.0142**, answering the literal string `"not found"` while
meeting the extraction contract 400 of 400. #418's own note records that the system prompt reaches
*"MapReduce's per-chunk maps"* — so the abstention rule is applied **per chunk**, and a single chunk
legitimately lacks the answer even when the six together contain it, so every map abstains and the
reduce finds nothing. `refine` at 0.2521 is the same failure in weaker form across its rewrites.

**What was missed: the FLARE arms never received the contract at all** — 3 of 353 judged and **0 of
47** nulls, unchanged. A gap in #419's own Task 4 design, which hands FLARE `FlareLoopOptions =
new()` for the loop (deliberately — a terminal instruction per fragment is what caused the runaway)
and only `AnswerInstruction` for the post-loop call, so grounding and abstention reach it nowhere.

**The finding, named as a class for the first time: there is no single instruction string that means
the same thing to a single-shot engine and to an engine that decomposes its context.** The apparatus
assumed one shared prompt makes arms comparable; that holds for `dense` and `chatengine`, and fails
for `mapreduce`, `refine` and `flare`, because a rule written about *the answer* is applied to *a
part*. **Fifth occurrence of this shape in the phase** — #418's terminal instruction in FLARE's
fragment loop was the same error with a different instruction. The rule to carry: **before sharing an
instruction across arms, ask at what granularity each arm will apply it.**

**The contract was then split three ways by granularity, and a second 400-query subset settled what
that buys, 2026-08-30.** Grounding goes to every arm (true of a sentence as of an answer), abstention
to `dense` alone (a statement about *the answer*, false of a part), and the terminal extraction rule
to every arm but reaching FLARE only *after* assembly. Full account in
`docs/plans/2026-08-30-engine-granularity-findings.md`.

**`PromptTemplate`'s byte-identity is now proven rather than argued:** `dense` returned
0.3484 / 0.2635 / 0.3201 with 222/353 and 21/47 abstentions — **identical to the previous subset,
digit for digit**, replayed wholly from cache. Its 2,556 answers, its pinned 0.3499 and Gate 0 are
intact.

**Four arms became comparable.** Against a properly-instructed `chatengine` control, with every
instruction fixed and only the mechanism varying: `flare` +0.1417, `flarefixed` +0.1332, `refine`
−0.0680, `mapreduce` −0.4249. FLARE moved for the first time (0.6572 → 0.7479) now that grounding
reaches it at all. Not pinned and not published — a 400-query subset is a validation run, and this
thread has three times shown a small sample not surviving scale.

**`mapreduce` is not measurable under this apparatus, and the reason is structural.** It recovered
from 0.0142 to 0.1898 with the worst contract compliance of any arm (368/400), answering *"The
TechCrunch article … does not provide any information"* and *"not found"*. **Grounding is also not
portable to per-chunk maps**: applied to one chunk of six it correctly elicits "this chunk does not
answer it", and the reduce aggregates those into nothing. Removing abstention was necessary and
nowhere near sufficient. **MapReduce's per-chunk calls are not answering the question — they are
extracting facts**, so no instruction phrased *"answer the question"* is right for them, and the map
step and the final step are simply doing different jobs.

**Third instance of the granularity class, and the one proving it is not about a particular rule** —
terminal/extraction per sentence gave FLARE an 86,091-byte runaway; abstention per chunk gave
MapReduce 0.0142; grounding per chunk gives it 0.1898. FLARE escaped only via bespoke handling, which
works because its sentence calls genuinely *are* answering. MapReduce and Refine would need the same
per-engine synthesis seam — **product work in `Rag.NET.AnswerEngines`**, not a harness change.

**A residual asymmetry, stated rather than left implicit:** FLARE now gets a dedicated post-loop
formatting call no other arm receives, and part of its +0.14 may be that second pass rather than the
mechanism. The apparatus cannot separate them.

**Consequence for the DoD clause: a clean full sweep on this basis does NOT close it.** The clause
names *"the three answer engines"* — MapReduce, Refine and FLARE — and MapReduce cannot be measured
here without the synthesis seam. The sweep can deliver a valid comparison for `refine`, `flare` and
`flarefixed` against the control, plus MapReduce documented at scale as not-measurable with the
evidence. That is partial completion, worth having, and **the clause must not be marked met on the
strength of it**.

**The full sweep ran on the corrected apparatus, 2026-08-30, and produced two clean findings.**
15,336 records, 5.5 hours, 18 tests / 0 failed / 0 skipped. **Gate 0 held on all three rules** —
`dense` reproduced 0.3499 / 0.2603 / 0.3242 exactly, so the corpora did not diverge and
`PromptTemplate` is byte-identical for the third run running. Full account in
`docs/plans/2026-08-30-answer-engine-sweep-results.md`.

**Finding 1 — sequential refinement is significantly worse than answering once.**
`refine − chatengine` = **−0.1055**, McNemar `p<0.0001`, losing 370 queries and winning 132. The
comparison is uncontaminated: identical system prompt, identical single call path, no extra passes,
so the difference is the mechanism. Read as a *completion* — a feature measured and found wanting is
this milestone's stated bar. One caveat travels with it: `refine` rewrites per chunk, a weaker
instance of the granularity problem, and some of the deficit may be that rather than the mechanism.

**Finding 2 — FLARE's lookahead helps, by under one percentage point.**
`flare − flarefixed` = **+0.0075**, p=0.0135, 31 wins to 14. The cleanest measurement in the set and
the only direct one of FLARE's mechanism: identical engine, differing only in whether the
mid-generation lookahead may fire. Significant, and small — both belong in any statement of it.

**Labelled as not clean:** the FLARE arms' ~+0.11 over `chatengine` is confounded by a post-loop
formatting call no other arm receives (`flare − flarefixed` is unaffected, since both get it);
`chatengine − dense` = +0.2843 is one sentence of prompt, not an engine result; and `mapreduce`'s
−0.4333 is the apparatus failure confirmed at scale, with the worst contract compliance of any arm
at 2,333/2,556.

**The 400-query subset predicted every sign correctly** and no magnitude — moves of 0.008 to 0.038.
Fourth pilot-versus-scale data point in this phase and **the first where the direction survived**.
A ~$3 subset predicted a ~$20 sweep's direction on all four arms; it did not predict the sizes, and
should not be trusted to.

**Pinned in `MultiHopRagAnswerReproduction`:** `chatengine` 0.6341, `refine` 0.5286, `flarefixed`
0.7428, `flare` 0.7503, each carrying its caveats in the entry. All four rejoined the default replay
set automatically — the data-driven selection working —
and `SelectArms_DefaultSelection_ContainsOnlyArmsWithARecordedFigure` failed on the pin and named the
list to update, so the movement was deliberate rather than silent. **`mapreduce` is not pinned**: it
ran, and its figure measures a known-broken setup.

**Two of the three named engines now have a pinned figure with a control. The DoD clause is still
not met** — MapReduce cannot be measured until `MapReduceAnswerEngine` applies caller instructions to
its reduce step rather than to every map, which is product work in `Rag.NET.AnswerEngines`.

**That reading of MapReduce was wrong, and it was one defect rather than a structural limit —
corrected 2026-08-31.** Full account in
`docs/plans/2026-08-31-mapreduce-refusal-filter-findings.md`.

MapReduce drops `not found` partials by an **exact** string match before the reduce. A caller system
prompt that changes the shape of a reply defeats that match: under the extraction contract, refusals
came back as `Not found. The answer to the question is "not found".` — not equal to `not found` — so
they survived into the reduce, which treated them as contradicting the one correct partial and
**discarded the correct answer**. A logged transcript shows a map returning
`The answer to the question is "Microsoft".` and the reduce throwing it away in favour of three
unfiltered refusals.

**Fixed in `MapReduceAnswerEngine` by appending a map protocol after the caller's prompt on map calls
only**, so the sentinel survives any caller formatting instruction; the reduce keeps the caller's
prompt untouched, and a caller supplying no prompt gets no change at all. Two fast-tier regression
tests, mutation-checked. **This is a shipped-package defect**: any caller who writes "always end with
a summary line" hits it, and it is the same class as the FLARE protocol defect fixed in #419.

**Validated on 400 queries, 2026-08-31:** `mapreduce` **0.1898 → 0.6487** paper, contract compliance
from the worst of any arm to **400/400**, and answers containing "not found" from the majority to
**1 of 353**. `dense` and `chatengine` returned figures identical to the previous subset, replaying
wholly from cache. **`mapreduce − chatengine` = +0.0340** — it is now slightly ahead of the
single-shot control rather than 0.43 behind it.

**So three claims in the record above are retired:** that the number was an apparatus failure rather
than a property of the engine; that MapReduce cannot be measured by an apparatus sharing one
instruction; and that its per-chunk calls extract facts rather than answer, making any
"answer the question" instruction false of a chunk. All three were elaborate, fitted the evidence
available, and were wrong. **No evidence remains that MapReduce is bad at multi-hop questions.**

**How the wrong reading survived two runs:** the full sweep and both subsets reported the same low
figure, which read as corroboration. It was the same defect reproducing. **A number that reproduces
is not a number that is right** — consistency measures reproducibility, not correctness. What broke
it open was reading a transcript of what the model actually sent and received, about 20 calls, after
aggregate scores had been examined at three different sample sizes.

**`mapreduce` measured at full scale and pinned at 0.6483, 2026-08-31 — and it is a null result.**
Both controls held on the run (`dense` 0.3499 / 0.2603 / 0.3242; `chatengine` exactly its pinned
0.6341, both replaying from cache), so nothing drifted underneath it. Contract compliance
**2,553 of 2,556**, up from 2,333 before the fix.

**`mapreduce − chatengine` = +0.0142, McNemar p=0.2955 on 462 wins against 430 across 892 discordant
pairs — not significant.** The map/reduce mechanism buys nothing measurable over a single call on
this corpus. That is the finding: not a defect, not a win, and a feature measured and found
unremarkable is a completion, exactly as 5.2 was.

**How close it came to being published as a win, recorded because the lesson is sharper than the
earlier ones.** The 400-query validation subset put the same difference at **+0.0340**, which reads
as a real gain; at full scale it is +0.0142 at p=0.2955. Previous pilot-to-scale misses in this phase
moved a *magnitude* — this one moves the *conclusion*. **A subset can carry a direction and cannot
carry a significance**, and the subset had just been vindicated on direction, which is exactly what
would have made trusting it feel safe. Third timing miss alongside it: 30 minutes against a projected
6.4 hours.

**The DoD's answer-engine clause now closes.** All three named engines have a pinned figure against a
properly-instructed control: MapReduce +0.0142 (p=0.2955, no measurable difference), Refine −0.1055
(`p<0.0001`, significantly worse), FLARE's lookahead +0.0075 (p=0.0135, helps slightly).
`UnmeasuredEngineArms` is **empty** — every arm in `AnswerArm.All` carries a figure, reached through
three separate failures of `SelectArms_DefaultSelection_ContainsOnlyArmsWithARecordedFigure`, each
naming the arm and the list to update so the default replay set moved deliberately rather than
drifting.

**And `refine` needs re-examination.** It scored −0.1055 and was pinned with a caveat that some
deficit *may* be structural rather than mechanism. It rewrites sequentially over chunks — a per-chunk
shape — and MapReduce has just shown a per-chunk shape can hide a defect worth 0.46. Read that caveat
as a live question rather than a hedge.

~~**The full sweep must not be funded until the arms are under equivalent treatment**~~ — ~$20 would buy
three differently broken arms measured at higher precision. Nothing is pinned. The DoD's
answer-engine clause remains unmet.

**HyDE's thread completed 2026-09-02 — the sweep's second technique after RAPTOR, and its first
under the Real protocol.** #433 (`e4923341`, verified on `main` by content: `0.71389` in
`BeirReproduction.cs`, `UnderCachedHyde_` in `BeirRunBudget.cs`) wired and measured HyDE and
reranking over Rag.NET's own chunking rather than parity's one-chunk-per-document units, which is
the corpus no user runs.

**SciFact RealHyde 0.71389 against its control, the Real cell's 0.67742 — +0.03647**, reproduced by
a second run agreeing to five decimals and now asserting rather than reporting. Recall@10 0.85856,
MRR@10 0.67295, 20,155 units over 5,183 of 5,183 documents, 21,355 embedding-cache hits and 0
misses, no model calls, no spend. HyDE reordered 300 of 300 queries.

~~**The finding is the shrinkage, not the sign.**~~ **Corrected 2026-09-02 by FiQA's cell, and the
correction is the more useful finding.** This paragraph originally read the SciFact pair — +0.055 at
parity over a 0.645 dense anchor against +0.036 over 0.67742 — as "HyDE buys roughly two-thirds as
much on the corpus this library actually produces as it does on whole documents". That is a ratio
from **one dataset** presented as the shape of the result, and the second dataset does not merely
fail to confirm it: it moves the other way.

| dataset | parity dense | parity HyDE | Δ | real dense | real HyDE | Δ |
| --- | --- | --- | --- | --- | --- | --- |
| SciFact | 0.64593 | 0.70001 | **+0.05408** | 0.67742 | 0.71389 | **+0.03647** |
| FiQA | 0.37086 | 0.36543 | **−0.00543** | 0.35569 | 0.34683 | **−0.00886** |

**HyDE's sign is a property of the corpus, not of the protocol.** Positive stays positive and
negative stays negative across both protocols. What the chunked corpus changes is the *magnitude*,
and **not in a consistent direction** — SciFact's advantage shrinks toward zero while FiQA's deficit
grows away from it. There is no single ratio to quote, and quoting one was the error.

**Note that HyDE was already slightly negative on FiQA at parity.** The library's own ablation table
has carried 0.36543 against a 0.37086 anchor since Phase 3.15, so "HyDE helps" was never this
project's finding — it was SciFact's, and nobody had said so.

**Reranking behaves the same way and was measured in the same PR**: SciFact +0.01266 (0.69008 against
0.67742) where its parity cell is +0.039, and FiQA −0.00951 (0.34618 against 0.35569, 648 of 648
queries reordered). **Two of two techniques measured on two datasets are corpus-dependent in sign.**
That is the load-bearing result for the rest of the sweep: a technique cell pinned on SciFact alone
says almost nothing about whether a user should switch the technique on, so the remaining cells
(hybrid BM25, late chunking, SPLADE) want at least two corpora before anything is concluded from
them. Costed rather than assumed: FiQA's HyDE cell was 29 m 47 s and paid no model calls.

**What survives from the original claim** is the weaker and still-useful half: a parity-corpus
ablation figure is not a statement about the corpus this library produces, in either direction. It
overstated HyDE's help on SciFact and understated its harm on FiQA.

**Cost is recorded as two numbers per cell, because cold and warm differ by 16–18x** — SciFact
199.5 s then 12.5 s, FiQA 1,786.9 s then 99.3 s, each pair on the same machine and binary with
nothing changed between them. **Two cells agreeing on the ratio makes it a property of these runs
rather than one odd measurement.** The candidate cause is the OS page cache over the embedding
cache's shard files, recorded as a **candidate and not a diagnosis**; in-process chunking is redone
on every run, so it is not that. `BeirRunBudget` carries both figures per cell and says to budget
against the cold one. This is the same shape as the 23x artefact that produced three false findings
in one day, caught this time because each pin was confirmed by a second run rather than trusted from
one — and both nDCG figures reproduced to five decimals while their timings did not.

**#433 was red on CI for a day and nobody had read why**, which is the part worth carrying forward.
Adding `RealHyde` and `RealReranked` to `BeirProtocol` left three exhaustive constructs behind —
`BeirRunBudget.Describe`, `BeirRunBudget.Filter` and `BeirReproduction`'s required-pair set — and
each threw rather than skipped. **None of it was reachable on the machine that wrote the cells**:
`Explain` runs only when a case is *gated off*, the measurement ran with the opt-in set, so the gate
returned before `Explain` was ever built. The defect was invisible in exactly the configuration that
produced the figures and immediate in the one CI uses. A green local run over a gated-off suite is
not evidence about the gated-off path.

**And the filter arms could not simply be added.** `Filter`'s discriminators are substring matches
and the new methods are named as extensions of the old ones, so `UnderCachedHyde` became a prefix of
`NdcgAt10_UnderCachedHydeOverRealChunking` and the *parity* cell's printed command silently selected
the *Real* cell too — handing a reader an expensive measurement they did not ask for, under an exit
code saying everything ran as priced. `EveryCellsPrintedFilterCanSelectATest` could not see it: it
asks whether a filter selects anything, not whether it selects only what it names.
`NoCellsDiscriminatorIsContainedInAnothers` asks the second question.

**The gate that made the measurement safe landed with it, as #439** (`f66b1677`, verified by content:
`IsOptedInFor` in `BeirRunBudget.cs`). `RAGNET_BEIR_LONG_RUNS` was binary, and xunit filters on
classes and methods but never on theory arguments, so measuring one cell ran its theory across every
dataset. The datasets are not interchangeable: **TREC-COVID's Real leg has never been embedded and
its corpus is 33x SciFact's**, so a run aimed at SciFact would chunk and embed that corpus from cold
on the way past and then fail on refuse-on-miss, because its hypotheticals were never generated —
hours of compute spent reaching a guaranteed failure. That is not hypothetical: **FiQA's
`RealReranked` cell ran 6 h 18 m on 2026-09-01 for exactly this reason**, unscheduled, and its own
commit message says so.

`=1` and `=true` still mean every dataset; `=scifact` or `=scifact,fiqa` mean those alone; anything
else **throws** rather than widening, because the old rule was "anything else present is on" and that
turns a typo into the most expensive run available. The skip messages print the scoped form. **Every
remaining technique cell in this phase is now measurable one dataset at a time**, which is most of
why it was worth doing before the measurement rather than after.

**What this leaves open in the sweep:** hybrid BM25, late chunking and SPLADE under the Real
protocol; HyDE and reranking on the three datasets each still lacks (FiQA's RealHyde is the cheapest
of them — its chunked corpus is already embedded and its hypothetical cache exists); every vector
store through the SciFact parity leg; local search's yes/no abstention; and `refine`'s caveat above.

### Phase 6.2.2: Requested Features [status: complete 2026-08-16 — #252 built and exercised; the phase stays open in spirit for any further request filed before the tag]
**Goal:** the feature requests reported against the shipped packages, built and exercised to this
milestone's bar rather than deferred past the tag. This phase exists because Milestone 6 is the
terminal milestone: a request filed against a published package has nowhere else to land, and the
alternative — "Beyond v1.0" — means shipping v1.0 with a known, small, reported gap. It stays a
*sub*-phase of 6.2 and not a milestone of its own so that the scope line above holds: this is not
"make every feature good", it is "the specific things people asked for, each one small enough to
name".

**The bar is 6.0's, not a lower one.** Anything built here is a new `✅ Done` row, so it needs its
*Exercised by* pointer like every other row, and it must not push its package back onto
`PackagesAllowedToStayUnit`. `Rag.NET.DataProviders.Web` is a 6.2 package (bare `unit` today), so
#252's real-file test is also the run that takes it off that list — the phases compose rather than
collide.

**Contents:**
- **#252 — `SitemapDataProvider`: skip URLs by prefix or regex. DONE 2026-08-16.** Both design
  questions below were settled and are recorded in `SitemapOptions`' own XML docs: the filter
  applies to nested `<sitemapindex>` links as well as page URLs (`ExcludeNestedSitemaps`, default
  true, because pruning an index avoids fetching it at all), and prefix and regex are two
  mechanisms rather than one predicate (a `Func<string,bool>` cannot come from an
  `appsettings.json`, and this provider is routinely configured from one). Patterns carry a
  one-second match timeout — they come from a human, and a sitemap can carry tens of thousands of
  URLs to apply them to.
- *(original entry)* **#252 — `SitemapDataProvider`: skip URLs by prefix or regex.** Reported 2026-08-15 against the
  shipped provider, with a working reference implementation in the issue. Today
  `SitemapDataProvider` has no filtering at all (`src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs`,
  verified 2026-08-16): every `<loc>` in the sitemap and every nested `<sitemapindex>` link is
  yielded. Two design questions this phase owns rather than assumes, because the issue's sample
  answers one of them by accident: whether the filter applies to nested sitemap-index links as well
  as page URLs (the sample applies it to both, which is a real decision — it prunes whole subtrees,
  not just pages), and whether prefix and regex are one mechanism or two. Additive and
  non-breaking — an optional constructor parameter, defaulting to no filtering, so no existing
  caller changes.

**Exit condition:** every issue listed above is closed by a merged PR, each with an *Exercised by*
pointer in `features.md`, and no new bare-`unit` entry is created.

### Phase 6.2.3: Corpus-Level RAPTOR — implement the paper's central mechanism [status: complete 2026-08-21 — added 2026-08-20, built and merged in #340. Three defects fixed (#331 tree scope, #332 `ChunkIndex` collision across levels, #333 `SelectK` returning k=n so a level never reduced and the loop never terminated); three more found, filed and documented rather than fixed (#336, #337, #338)]
**Completed:** 2026-08-21
**Surface:** Backend
**HelpWanted:** no
**Issue:** #331

*This phase never carried `status: active`. It was added and executed in the same session, so the
`pending → active → complete` transition collapsed. Recorded rather than back-filled, because a
status the roadmap never actually held would be a fiction.*

**Goal:** Make `Rag.NET.Raptor` cluster across the corpus rather than within each document, keeping
the per-document scope selectable so the shipped behaviour stays measurable rather than deleted.

**Why this is a phase and not a note.** `RaptorIngestionBehavior` is an `IIngestionBehavior`
clustering `ctx.EmbeddedChunks`, and `IngestionContext` carries one `Stream` and one
`DocumentMetadata` — one document's chunks. Its own telemetry says so
(`activity?.SetTag("document.id", …)`). The RAPTOR paper clusters across the collection; that is
the technique's point. A per-document tree cannot produce a node spanning two documents, so on any
multi-document question the summaries can only displace. **This is #300's shape** — a whole-corpus
operation running once per ingested document.

`docs/guide/raptor.md` is not wrong about it; it describes the per-document behaviour accurately.
The gap is between the package's name and its mechanism.

**Why it costs what it costs.** #302's debounce-plus-rebuilder pattern transfers in shape, but not
in substance: `CommunityDetectionBehavior` can do whole-graph work because GraphRAG owns an
`IGraphStore` that enumerates itself. RAPTOR has no equivalent and the vector store cannot stand
in — `IVectorStore` is `StoreAsync`/`SearchAsync`/`DeleteByDocumentIdAsync` with no enumeration;
`IChunkLookup` is by-key and returns `TextChunk`, so it cannot return the **vectors** RAPTOR
clusters on; and it is implemented by `InMemoryVectorStore` and `FederatedVectorStore` only, which
is what #318 is about. So this needs RAPTOR to own persistent state the way GraphRAG owns its graph
store: leaf embeddings written at ingestion, corpus-wide clustering over them, debounced on growth,
an on-demand rebuilder, and a migration story. Roughly **#302 plus #312 combined**.

**Both scopes stay selectable, deliberately.** The per-document path is kept as an option rather
than deleted, following #323's precedent — `GraphLocalSearchBehavior` and `PageRankWeight` were
kept unregistered but present because deleting them would have made three pinned figures
unreproducible. Keeping both here means the RAPTOR measurement in 6.2.1 prices the old and new
shapes in **one** run rather than two, and nothing ships unpinned.

**Ordering, and the reversal behind it.** The design at
`docs/plans/2026-08-20-raptor-real-protocol-design.md` originally deferred this fix on
a measured trigger, per #247's measure-then-fix order. **Reversed by the operator on 2026-08-20** —
*"fix it first before spending again"* — on the grounds that the defect is structural rather than
empirical: it was established by reading `IngestionContext`, not inferred from a figure, so a paid
sweep would buy evidence for something already known. What that gives up is a pinned figure for the
shipped state; keeping both scopes selectable is what gives it back.

**Exit condition:** RAPTOR clusters corpus-wide by default; the per-document scope is reachable by
configuration; the debounce and the rebuilder exist and are tested; existing RAPTOR users have a
stated migration path; #331 closes. The measurement itself belongs to 6.2.1 and runs after.

#### Measured 2026-08-25 (Task 5): the change did not buy accuracy

The full sweep — 2,556 queries, 4 arms, the validation gate holding to four decimals — puts
**`raptorcorpus − raptor` at −0.0146 paper, −0.0204 raw, −0.0027 strict**. Corpus-level clustering
is *worse* than the per-document tree it replaced, significantly so on two of three rules (McNemar
paper p=0.0247, raw p=0.0006, strict p=0.7372).

**The gap is inference queries** — 0.7831 against 0.8309 — which is the multi-hop case this phase's
rationale said a per-document tree could not serve. It is the one place the change was expected to
pay and the one place it costs.

The 50-query pilot had this at +0.0000 and was underpowered. See `STATE.md`'s OPEN DECISION: the
default has **not** been changed, because one corpus that rewards per-document locality is thin
ground for reversing a shipped design, and the strict rule is a wash.

### Phase 6.2.4: RAPTOR Retrieval Over-Fetch — make `Boost` and `Filter` do what they document [status: complete 2026-08-21 — added and merged the same day in #344. Small, as expected: `Boost` and `Filter` now fetch `CandidateMultiplier` × `TopK` candidates and reduce afterwards, so `Boost` can promote a summary into the result set and `Filter` returns what the caller asked for. `Blend` is byte-identical. 1× reproduces the pre-fix behaviour exactly, so the control survives by configuration rather than as a shipped defect]
**Completed:** 2026-08-21
**Surface:** Backend
**HelpWanted:** no

**Goal:** `RaptorRetrievalBehavior` over-fetches candidates before applying its mode, so `Boost` can
promote a summary *into* the result set and `Filter` returns the number of results the caller asked
for. Keep today's behaviour reachable by configuration.

**The defect.** `HandleAsync` calls `next(ctx, ct)` with the context **unmodified**, and
`VectorStoreBehavior` fetches exactly `ctx.Options.TopK`. So the behaviour only ever sees the
already-truncated top-k:

- **`Boost` cannot do what it documents.** `ApplyBoost` multiplies summary scores *within* the k
  already returned, so a summary ranked 7th that would beat 6th at 1.2× can never appear. It
  promotes within the set, never into it — the opposite of the mode's stated purpose. #239's shape:
  an option whose default behaviour undoes what it says, invisible because every test passes.
- **`Filter` under-fills.** Ask for 6, get 6, drop 3 summaries, return 3. A contract violation, not
  a tuning question.

**The fix is small and its pattern is already in the solution.** `MmrBehavior` does exactly this:
`var candidateCount = ctx.Options.MmrCandidateCount ?? ctx.Options.TopK * 3;` then
`next(ctx with { Options = ctx.Options with { TopK = candidateCount } })`, reducing after. RAPTOR
gains the same shape and the same `?? TopK * 3` default, so the multiplier is precedent rather than
invention.

**`Blend` must stay byte-identical.** It is the default and figures are pinned against it; it
returns exactly `TopK` and must not start over-fetching.

**Why this is a phase rather than a line in 6.2.1.** Because 6.2.3 also looked like a small fix and
turned out to need a store, a debounce, a rebuilder and a migration. Naming it means the roadmap
sees its cost if it grows again. It is genuinely expected to be small.

**Why it lands before the measurement, reversing this design's original order.** The measurement
spec at `docs/plans/2026-08-20-raptor-real-protocol-design.md` originally measured `Boost` as
shipped and fixed it after, following #247. That was wrong: #247's defect was *empirical* — nobody
knew what the shared store cost until it was measured — while this one is **structural and provable
from two lines**. Paying for an answer arm to confirm arithmetic buys evidence for something already
known, which is the objection that reversed #331's ordering on 2026-08-20. The question reading
cannot answer is whether a *working* `Boost` helps, and that needs the fix first.

**The control survives by configuration, not by leaving the defect in place** — 6.2.3's own answer,
where `PerDocument` stayed selectable. Setting the candidate count to `TopK` (1×) reproduces today's
behaviour exactly, so anyone wanting the broken state pinned can still reach it.

**Exit condition:** `Boost` can promote a summary into the result set that ranked below the cut
without it; `Filter` returns `TopK` results when enough non-summary candidates exist; `Blend` is
unchanged; 1× reproduces the old behaviour; all three have tests that fail against today's code.

### Phase 6.2.5: Contract Defects — shipped behaviour that contradicts its own documentation [status: complete 2026-08-25 — added and shipped the same day across three PRs: #350 in #372 (breaking: `IBm25Index.Search` takes the metadata filter, so the BM25 arm of client-side hybrid stops ignoring it), #360 in #373 (a benchmark run whose query selection is empty now fails instead of passing on zero queries), #328 in #374 (`KNearestNeighborsCount` no longer set below Azure's own default)]
**Surface:** Backend
**HelpWanted:** no

**Goal:** three places where a shipped API does something other than what it documents. All three
were reported or found since 2026-08-22, and all three are cheap. They are grouped because they
share a shape, not a subsystem: **the code is wrong against its own contract, and every test
passes.**

- **#350 — the BM25 arm of client-side hybrid ignores `MetadataFilter`.** `RetrievalOptions.MetadataFilter`
  documents itself as restricting retrieval to chunks matching every pair. `EnsembleBehavior` passes
  the filter to the dense and sparse arms, then calls `Bm25Index.Search(query, topK)` — and
  `IBm25Index.Search` has no filter parameter at all, so there is nothing to pass. Those hits are
  merged by `RrfMerger` and nothing downstream re-applies the filter. **A filtered query can return
  chunks the filter excludes.** Worse than first reported: `CanDispatchNatively` also takes the
  client-side path when `EnsembleOptions` is supplied, when `MinScore` is non-zero, or when a sparse
  arm runs — so Azure AI Search with `MinScore = 0.2` leaks too, despite its native hybrid filtering
  correctly server-side. **This is the one that matters most**, because callers reasonably read a
  metadata filter as a boundary, and #342's requester was doing exactly that.
- **#328 — `KNearestNeighborsCount` is hard-coded to `TopK`.** Wrong even without semantic ranking:
  under RRF the vector arm should over-fetch so fusion has something to fuse, and more wrong when a
  reranker follows. Same shape as 6.2.4's `Boost` defect. Azure AI Search semantic ranking is the
  other half of the issue.
- **#360 — the answer test passes on an empty selection.** `RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES=1`
  selects **zero** queries: `SelectQueries` stratifies proportionally and every quota rounds to 0 at
  `bound = 1`. The run scores nothing, prints `NaN` for every metric, writes a 0-byte sidecar, and
  **passes with exit 0.** This is 6.2.1's own measurement tooling, and it is the fourth instance of
  this milestone's recurring failure — a green run that is not checking. It does **not** block
  Task 5, which omits `MAX_QUERIES` deliberately.

**Exit condition:** each defect has a test that fails against today's code; `MetadataFilter` is
honoured on every path that claims to honour it or the contract is narrowed to say otherwise; an
empty query selection fails rather than passes.

### Phase 6.2.6: Package Boundaries — stop `Rag.NET.Security` dragging a native SQLite binary [status: complete 2026-08-25 — added and shipped the same day in #376. `SqliteAuditLog` moved to a new `Rag.NET.Security.Audit.Sqlite` package and `UseAuditLog()` was removed rather than kept as a runtime check, so "auditing configured, nothing recorded" cannot be expressed — forgetting the migration is a compile error. Package count 71 → 72]
**Surface:** Refactor
**HelpWanted:** no

**Goal:** `Audit/SqliteAuditLog.cs` is the only file in `Rag.NET.Security` that touches SQLite, and
it is why `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3` are on the package. Everyone
using `UseChunkSanitiser`, `UseRbac` or `UsePiiDetection` pays for a native binary they never load.

**Sequenced before v1.0 deliberately.** Moving a type between packages is a breaking change, and it
is far cheaper before the tag than after. The precedent exists: 0.1.0 already extracted
`Rag.NET.Storage.Sqlite` for the same reason.

### Phase 6.2.7: Named Pipelines — isolate `IRagPipeline` and its services per configuration [status: complete 2026-08-25 — added and shipped the same day in #381. `AddRagNet(name, configure)` gives each name its own child container; the unnamed form is unchanged, which was the hard constraint. Three Criticals found in review, and four tests in the plan itself could not have failed for what they claimed]
**Surface:** Refactor
**HelpWanted:** no

**Goal:** today everything `AddRagNet` registers is an **unkeyed singleton**, so one container is one
pipeline is one index. `UseAzureAISearch` binds one store to one index and registers that instance
as `IVectorStore`/`IHybridSearchable`/`ICollectionManageable`. A caller who wants per-tenant
isolation has no way to express it.

**The requester proposed the answer and it is the right one:** keyed/named registrations, in the
shape `IHttpClientFactory` already established in .NET. The maintainer's analysis agreed, and the
requester accepted the cheaper equivalent.

**Why a metadata filter is not the same thing**, and why this phase exists rather than a docs note:
a filter is *caller-supplied*, so isolation depends on every caller remembering it — and #350 proves
the conditions under which it silently stops being a boundary are not something a caller can be
expected to know. Isolation belongs in the registration, not in each query.

**This is the largest phase added here**, and it is pre-tag on the operator's decision of
2026-08-25. That is consistent with 6.2.2's charter: this is the terminal milestone, so a request
filed against a published package has nowhere later to land. **Depends on 6.2.5**, because #350's
fix determines how much of the isolation story the filter can honestly carry.

### Phase 6.2.8: Requested DX and Chunking Quality [status: complete 2026-08-25 — added and shipped the same day in #378, three of its four items: #366 (heading-only chunks skipped after the breadcrumb is recorded, so the hierarchy survives), #365 (`ChatAnswerEngine` sources move to a `Tool` message), #355 (`Failed` distinguished from `Skipped`). #353 was split out into 6.2.10 during design]
**Surface:** Backend
**HelpWanted:** no

**Goal:** four reported papercuts against shipped packages, all from the same reporter, none
architectural.

- **#366 — header-aware chunking emits chunks that are only a heading.** A real indexed example is
  the single string `"Section 2"`. Those chunks carry no information, occupy retrieval slots, and
  score well on heading-shaped queries. The header-aware metadata feature is documented in the
  README, so this is a quality defect in a promoted feature.
- **#365 — `ChatAnswerEngine` puts sources in a `User` message.** Wrapping them in a `Tool` message
  lets a caller run PII detection on the user's text only and treat retrieved sources as already
  trusted. Small, and it makes a real deployment pattern possible.
- **#353 — `InitializeAsync` must be called by hand.** Create the index on first use when it is
  absent.
- **#355 — `IngestFromProviderAsync` reports failures as `Skipped`.** A missing index yields a pile
  of "skipped" and the caller must read `Errors` to discover why. Distinguish `Failed` from
  `Skipped`, and consider failing fast on the first error.

### Phase 6.2.9: `Umap.Fit` at Corpus Scale [status: complete 2026-08-25 — added and built the same day. Measured first, then changed: the kNN graph turned out to be 92% of `Umap.Fit`'s time and 98% of its allocation, so the issue named the right target. Bounded k-selection replaced the full sort, and the row loop parallelises above 512 rows]
**Surface:** Backend
**HelpWanted:** no

**Goal:** `Umap.BuildKnnGraph` is brute force — for MultiHop-RAG's 17,648 rows over 384 dimensions
that is ~311M distance evaluations and 17,648 sorts of 17,648 elements, single-threaded, via
`Array.Sort` with a comparison **delegate** rather than a value comparer.

**Memory is the part that bites first.** Each row allocates `new (float, int)[n]` = 141,184 bytes at
corpus scale, over the 85,000-byte large-object-heap threshold, 17,648 times — roughly **2.5 GB of
LOH traffic per corpus build**.

**Split out of #345, and #345's fix does not help it**: `Umap.Fit` runs *before* clustering, so
raising `k` changes nothing here.

**Any figure from this phase needs two runs on a quiet machine.** Three timing runs on 2026-08-17
disagreed by 6× on identical inputs, and 2026-08-24 lost an hour of measurement to an orphaned
runner starving its replacement.

#### What was measured

`UmapKnnBenchmarks`, 17,648 rows × 384 dimensions, `RunStrategy.Monitoring`, two runs per state on
an otherwise idle machine (i9-12900HK, 14 physical cores). Both runs are quoted rather than averaged
— the spread is the point.

| `Knn_BuildGraph` @ 17,648 | Time (run 1 / run 2) | Allocated | Gen0/1/2 per 1k ops |
| --- | --- | --- | --- |
| Baseline (sort every distance) | 74.8 s / 78.4 s | 2,379.86 MB | 111k / 111k / 111k |
| Bounded k-selection | 60.2 s / 57.4 s | 3,309.05 KB | 0 / 0 / 0 |
| ...and parallel rows | 14.7 s / 14.3 s | 3,343.81 KB | 0 / 0 / 0 |

**5.2× faster and ~729× less allocated**, with the large-object-heap traffic gone rather than
pooled: the scratch buffer no longer exists. `Umap_FitFull` moved 81.6/83.6 s → 21.0/21.3 s. The
2.5 GB LOH estimate was confirmed almost exactly — 2,379.86 MiB is 2,495 MB against an analytic
2,491 MB — and the flat Gen0=Gen1=Gen2 profile is the LOH signature.

**Two claims in the original framing were wrong, and the measurement is why we know.**

- *"This is real time rather than a micro-optimisation"* rested on the ~1,368 s corpus tree build.
  `Umap.Fit`'s level-1 reduction is ~82 s of that, roughly **6%** — the tree build is dominated by
  LLM summarisation. The case for the phase is the 2.4 GB of LOH churn, not the share of wall-clock.
- Replacing an O(n log n) sort with an O(n log k) selection sounds like the headline, but on its own
  it bought **21%**. The dominant cost is the distance computation — n² pairs × 384 dimensions —
  which no amount of selection touches. Most of the 21% came from the delegate comparison and the GC
  pressure, not from the complexity class. Parallelising that distance loop is what bought the 4×.

**Parallelism gave 4.0×, not 14×**, because every row streams the whole 27 MB dataset: the loop is
memory-bandwidth bound well before it is core bound.

**Deliberately not done:** approximate nearest neighbours and sampling, both listed as options on
#348. Each changes *which* neighbours are found, hence the tree, hence the RAPTOR comparison that
6.2.1 has in flight. Everything above is exact — the differential test asserts bit-identical indices
and distances against the pre-change implementation.

### Phase 6.2.10: Vector-Store Initialisation — create the collection without being asked twice [status: complete 2026-08-25 — added and built the same day. Neither of the two costs the design section priced had to be paid: its premise was wrong, and the pattern already existed in the codebase]
**Surface:** Refactor
**HelpWanted:** no

**Goal:** a caller should not have to call `InitializeAsync` by hand before the first ingest.
Reported as #353 — *"Is there a way to do this automatically when the code sees that the index does
not exist?"* — and it is the same incident as #355: the reporter's index did not exist, every entry
threw, and the run reported them as `Skipped`.

**It was pulled out of 6.2.8 because it is not a papercut, and finding that out is the useful part.**
The other three items in that phase were each one method or one field. This one is not:

- **`InitializeAsync` is not on any interface.** It is a public method on each concrete store —
  `AzureAISearchVectorStore.InitializeAsync` creates the bound index with its vector field, its HNSW
  profile and its `metadata_entries` complex collection. There is no `IVectorStore.InitializeAsync`
  to call.
- **`ICollectionManageable` is not a substitute.** It carries `CreateCollectionAsync`,
  `DeleteCollectionAsync` and `CollectionExistsAsync`, but those create a *named* collection, not
  the bound index with the schema the store actually needs. Calling them instead would create
  something the store cannot then read.

**So the design question is which of two costs to pay**, and it wants deciding before any code:

1. **Add `InitializeAsync` to `IVectorStore`.** The natural home — one hook, every store, and it
   enables a real lazy first-use initialisation rather than a registration-time side effect.
   Breaking for external implementers, and touches all eight in-repo stores.
2. **A per-store opt-in that calls the concrete method at registration.** Small, and precedented:
   `Rag.NET.Raptor`'s builder already does `store.InitializeAsync().GetAwaiter().GetResult()`. But
   that blocks a thread during DI setup, and it has to be repeated for every store.

**The precedent cuts both ways and should not settle it by itself.** That Raptor already blocks at
registration makes (2) consistent, not correct; sync-over-async during container build is a thing
this project does once, not a pattern it has endorsed.

**Sequenced after 6.2.7** deliberately: named pipelines change how stores are registered, and an
initialisation hook designed against the current single-unkeyed-singleton registration would have to
be revisited immediately.

**Exit condition:** a caller who has not called `InitializeAsync` gets a working first ingest, or a
failure that names what to call — never the current behaviour, where a missing index surfaces as N
per-entry errors with no statement of the cause.

#### What was built, and why the design section above is wrong

**Its premise — "`InitializeAsync` is not on any interface" — is false.** It is declared on six:
`IBm25Index`, `ICostLedger`, `IEmbeddingVersionStore`, `IParentChunkStore`, `IRagDataManager` and
`IRaptorLeafStore`, all with the identical signature and the identical idempotence contract.
`IVectorStore` was the only storage abstraction in the library *without* it. Option 1 was therefore
never introducing a concept — it was removing an inconsistency.

**Neither priced cost had to be paid.**

- *"Breaking for external implementers"* — avoided with a **default no-op body** on the interface
  member. An existing external `IVectorStore` keeps compiling and behaving identically.
- *"Touches all eight in-repo stores"* — five of them (Azure AI Search, PgVector, Qdrant, Redis,
  Weaviate) already declared `Task InitializeAsync(CancellationToken cancellationToken = default)`
  byte-for-byte, so they satisfied the new member with no code change at all.

**And the pattern was already in the repo, twice.** `ChromaVectorStore` had resolve-or-create on
first use before #353 was filed — double-checked read, semaphore, cached after, invalidated when the
collection is dropped — reached from read and write paths alike. `WeaviateVectorStore` had its own
`_initLock`/`_initialized` gate, but wired **only into `StoreAsync`**, so it created on write and
not on search. So this phase generalised an existing shape rather than inventing one, and Weaviate
kept its own bespoke logic with the missing call sites added.

**What shipped:** `VectorStoreInitialisationGate` in `src/Shared`, linked into each store exactly as
`RagTelemetrySource` is — one definition, no new dependency, nothing on any package's surface. It
caches **success only**; a `Lazy<Task>` would hold a faulted task forever, so one transient error at
startup would break the store for the process's life. `DeleteCollectionAsync` resets it when the
name matches the bound collection, so delete-then-use re-creates rather than writing into a hole.

**The sync-over-async in `Rag.NET.Raptor`'s builder is retired.** `SqliteRaptorLeafStore` now creates
its schema in its constructor — as `SqliteCostLedger` and `SqliteEmbeddingVersionStore` already did —
so the builder's `InitializeAsync().GetAwaiter().GetResult()` is gone.

**The audit that decided the scope.** Every component with an `InitializeAsync` was checked:
the three in-memory ones are `Task.CompletedTask`; `SqliteBm25Index`, `SqliteDocumentStore` and
`SqliteParentChunkStore` self-initialise on every call; `SqliteCostLedger` and
`SqliteEmbeddingVersionStore` initialise in their constructors. **The vector stores were the only
real gap** — which is worth recording, because the obvious guess was that this was a general defect
needing a general framework.

**The trade-off, documented in `docs/guide/vector-stores.md`:** creating on first use means a
mistyped collection name silently creates a new empty one instead of failing. That is inherent to
what #353 asks for; callers who want the error provision out of band.

### Phase 6.2.11: HTML Structure and a Guid Seam [status: complete 2026-08-25 — added and shipped the same day across two PRs: #375 and #371 in #385 (the sibling walk replaced by a document-order traversal over text nodes, plus `HtmlHrefHandling`), #380 in #386 (`IGuidProvider`, an interface rather than the abstract class the issue proposed, returning `Guid` rather than `string`). Verified on `main` by content — `HtmlHrefHandling.cs` and `IGuidProvider.cs` are both present — rather than by either PR's MERGED label]
**Surface:** Backend
**HelpWanted:** no

**Goal:** three issues filed 2026-08-25. Two are the same parser and the same walk; the third is a
testability seam. Ordered by damage.

- **#375 — `HtmlDocumentParser` loses content rather than misplacing it.** Reproduced against the
  parser directly: the reporter's markup yields **one section whose text is only the heading**, with
  both paragraphs dropped and emitted nowhere. `BuildHeadingSection` collects content by walking
  `heading.NextElementSibling` (`HtmlDocumentParser.cs:99`); when the `<h1>` is wrapped in layout
  divs it is its parent's only child, `NextElementSibling` is `null`, and the walk ends before it
  starts. `ParseAsync` emits one section per heading, so anything outside a heading's own sibling
  chain is never visited by any path. **A page shaped this way indexes as its headings and nothing
  else** — a very common shape in component-framework markup.

  **Note the interaction with #366, which shipped the same day.** For such a section
  `Text.Trim() == Heading.Trim()`, so `IsNothingButItsHeading` now skips it and the page produces
  **zero** chunks. #366 did not cause the loss — the paragraphs were already gone — but it removes
  the last visible trace, turning "a useless heading-only chunk" into "this page silently ingested
  nothing". Whichever fix lands must be checked against both, or one will mask the other.

- **#371 — href handling needs an option.** `Keep` (today's behaviour), `Remove`, `MakeAbsolute`.
  Same parser, same walk as #375, so they share a phase. `MakeAbsolute` needs a base URI, which the
  parser does not currently receive — that is the design question, not the resolution itself.

- **#380 — `Guid.NewGuid()` behind a seam.** **Scoped to 9 of the 23 call sites in `src/`**: only
  those whose value reaches observable output — `ChatAnswerEngine`'s call id, both audit request
  ids, `RagMcpTools`, `AzureAISearchVectorStore`, `RagGrpcService`, `IngestCommand`,
  `EndpointRouteBuilderExtensions`, `QdrantVectorStore`. Temp-file names, prompt delimiters and
  internal DI keys are left alone: injecting those adds ceremony and buys no test.

  **Decided 2026-08-25: local, not in `ZeroAlloc.ValueObjects`.** That library is already referenced
  by `Rag.NET.Abstractions` for `DocumentId`/`EntryId`, but those are *values*; a Guid factory is an
  ambient service, the same family as a clock. A public contract in a shared package is also a
  permanent versioning obligation for ten lines of code.

  **Corrected while building it: an `IGuidProvider` interface, not a `TimeProvider`-style abstract
  class.** The first decision argued from "the repo already uses `TimeProvider`" — but the repo uses
  `TimeProvider` because Microsoft ships it in-box, which is a reason to *use* it, not a house
  pattern to copy. Every abstraction this library authors is an interface (`IVectorStore`,
  `IBm25Index`, `ICostLedger`, `IAuditLog`), it is what #380 asked for, and it substitutes with the
  mocking library the tests already use. `SystemGuidProvider.Instance` keeps the property that
  mattered about the abstract-class shape: nothing has to be registered for production behaviour,
  because every component takes an optional `IGuidProvider` and falls back to it.

  **Returns `Guid`, not the issue's `string`**: formatting is the caller's business, and `string`
  bakes `"N"` into every call site.

### Phase 6.2.12: Dogfooding Defects — what the first external user found [status: complete 2026-08-26 — added and shipped the same day. Seven PRs from one reporter (@StefH) integrating the library into a real project, which is the dogfooding this project had not done. Two of the defects were introduced by fixes earlier in this same phase, and one was silent data loss]
**Surface:** Backend
**HelpWanted:** no

**Goal:** close the defects found by the first person to use the library against a real corpus.
Kept as its own phase because the pattern matters more than the individual fixes: **every one of
these was reachable at shipped defaults, and none had a test.**

- **#390 — a named pipeline could not see the host's `IEmbeddingGenerator`.** The canonical
  `Microsoft.Extensions.AI` pattern registers on the root collection; a named pipeline built a
  child container and saw only what `AddRagNetShared` declared, so the named form saw *less* than
  the unnamed one for identical registrations. Fixed in #391 by forwarding the host's non-keyed
  root singletons. **The first diagnosis was wrong and was posted publicly** — the reporter's
  follow-up showed the registration was present, and reproducing it locally showed unnamed worked
  while named failed. The correction is on the issue.

- **#396 — that fix deadlocked Blazor.** Forwarding ran inside `BuildFactory`, which *is* the
  factory delegate for `IRagPipelineFactory`, so resolving root singletons re-entered the container
  for a service still under construction; a host singleton depending on `IRagPipelineFactory` hung
  outright. Fixed in #397 by deferring forwarding to the first `Get(name)`.

- **#400 — and that fix hung on unrelated host singletons.** Deferring made forwarding happen at
  `Get(name)`, but it still resolved *every* eligible root registration to forward it as an
  instance — the only descriptor shape a child does not dispose. A host singleton that blocks on
  construction therefore stalled `Get` for a pipeline that never wanted it. Fixed in #403 by
  forwarding through a factory descriptor per root registration, so nothing is constructed until
  the pipeline asks. **The trade was measured, not assumed:** an instance descriptor is disposed 0
  times by the child and a factory descriptor once, so laziness costs a second `Dispose` at
  shutdown for services the pipeline actually used. A hang at startup is worse.

- **#394 — `CleanupMode.Full` without a hash store did nothing, quietly.** Fixed in #398 by
  reporting it. **Investigating #400 then found the same hazard through a door that guard did not
  cover, and this one was live data loss:** a provider listing failure was collected into `Errors`
  and dropped, so the entries behind it were never seen and cleanup deleted them as disappeared.
  One 500 on one sitemap page removed every document behind it, and the run reported success.
  Fixed in #402; the `stoppedEarly` bool became a `CleanupBlocked` reason so that every way a run
  can fail to see an entry must be named rather than defaulting to "safe to delete".

- **#395 — `ProviderIngestionResult` carried four counts.** Fixed in #399, but not as proposed:
  the issue asked for `IReadOnlyList<FileEntry>`, and `FileEntry` carries an `OpenContentAsync`
  delegate that a finished run has no business handing back — and cannot produce at all for a
  deleted document, which by definition the provider no longer lists. The lists hold a
  `ProviderEntryOutcome` instead. A `NotAttempted` list was added because counts could not express
  the `StopOnFirstError` case at all: the entries after the failure appeared in no count, so a run
  that stopped after 1 of 5,000 URLs read exactly like a run that only had one to do.

- **#375, #371, #380** shipped in 6.2.11 above.

**The unifying finding, and the reason this is a phase rather than a footnote.** The stall the
reporter opened #400 for was an unconditional `await Task.Delay(1s)` at the end of
`AzureAISearchVectorStore.StoreAsync` — once per document, so a 500-page sitemap spent over eight
minutes asleep. It bought nothing: Azure gives no read-after-write guarantee at any fixed delay.
Removed in #401, along with a sweep of every unconditional sleep in the solution; the ones that
poll a real condition against a bounded timeout stayed, and nine `Task.Delay(2s)` calls in the
Azure tests became polls that fail naming the condition that never came true.

**What none of it had was a test.** The data loss, the two hangs and the sleep were all reachable
at shipped defaults, and the suite was green throughout — the same shape as 6.2.3's RAPTOR finding,
where no test had ever built a tree deeper than one level. Every fix here landed with a test that
fails against the previous code, and each was mutation-checked with the mutation verified to
compile first.

### Phase 6.3: Release v1.0 [status: pending — but its first work is DONE and was done before this milestone opened: 71 packages are live on nuget.org at 0.1.0 since 2026-08-11, so the account, the key and every package ID are settled. What remains is the v1.0 tag itself. ~~Now gated on 6.2.3~~ — **that gate cleared 2026-08-21** when #340 merged. What still gates the tag is 6.1's recordings, kept as a gate by the operator's 2026-08-20 decision, and 6.2.1's sweep]
**Goal:** Tag v1.0, plus whatever release mechanics Phase 4.1's packaging pass leaves to
release time — the release-please run, release notes, the published packages' final metadata.
The tag is the last and smallest phase in the milestone, which is the point of the 2026-08-03
restructure: by the time this phase runs, every criterion above it is already true and checked
by something.

> **CORRECTED 2026-08-16, and the correction is larger than the note.** Everything in the note
> below was true on 2026-08-03 and stopped being true on **2026-08-11**, when 71 packages were
> published to nuget.org at `0.1.0` — five days before this milestone opened, and nothing in the
> planning record noticed. Verified against nuget.org's API on 2026-08-16: 71 IDs live and
> `listed: true`, published 15:10:03Z, licence MIT; `.snupkg` symbols served by the symbol CDN;
> `Rag.NET.Benchmarks.Quality` correctly absent (404), so `IsPackable=false` held through a real
> publish. **So `NUGET_API_KEY` exists and is correctly scoped, every package ID was available and
> is now owned, and nuget.org's validation passed on all 71** — the whole of "this phase's first
> work" in the note below, already done.
>
> **What is left of 6.3 is the v1.0 tag**, plus the one residual the publish did not touch: the
> real `409`-and-skip of `--skip-duplicate`, which only fires on a *second* push of the same
> version. `docs/reference/ci.md` § "What the rehearsal could not prove" carries the itemised
> before/after.
>
> The original note follows, unedited, because being wrong about this in a specific direction —
> assuming an exposure that had already been closed — is the kind of thing this milestone exists
> to find, and deleting it would hide that it happened.
>
> **Recorded at Phase 4.1's close (2026-08-03), so this phase starts from it rather than
> discovering it on release day: the owner stated they do not yet have credentials for all the
> packages created.** Concretely: no nuget.org API key exists (`NUGET_API_KEY` is unset — the
> `publish-nuget` gate fails loudly on that rather than 401ing), and none of the **70** package
> IDs is reserved on nuget.org, so ownership and ID availability are unconfirmed until the
> first push — an exposure 4.1's design accepted and recorded rather than fixed. Acquiring the
> account, minting the scoped key (`gh secret set NUGET_API_KEY`, fenced in
> `docs/reference/ci.md`) and confirming the IDs is this phase's first work, before either
> dispatch. What that first push exercises for the only time — authentication, key scoping, ID
> availability, nuget.org's own validation, the real 409-and-skip, `.snupkg` delivery — is
> listed in `docs/reference/ci.md` § "What the rehearsal cannot prove — the 6.3 residual",
> alongside the other first-executions this phase owns: both `release-please` dispatches, which
> 4.1 could not rehearse because their only observable effects *are* the release.

**Checklist** (the phase's work beyond the tag itself):
- [x] **Add `pack-validate` and `commitlint` to the `Main` ruleset's required checks** (routed
      here 2026-08-03 at Phase 4.1's close — until now recorded in that phase's residual (6),
      in `ci.yml`'s BRANCH PROTECTION comments and in `docs/reference/ci.md`, but owned by no
      phase, which violates this repository's record-then-schedule rule). Both checks run on
      every pull request and fail loudly, but the ruleset requires only the two `build-test`
      legs — so `pack-validate`, the only guard on the whole packaging surface, can go red
      without blocking a merge: the exact non-gating-check failure this repository has already
      documented. Do it before either release dispatch, and verify it the way the `build-test`
      checks were verified on 2026-08-03 — by reading the ruleset back through the GitHub API,
      not by trusting the settings page.
      *(Done 2026-08-11. The `Main` ruleset now requires `build-test (ubuntu-latest)`,
      `build-test (windows-latest)`, `pack-validate` and `commitlint`, read back through
      `GET /repos/{owner}/{repo}/rulesets/{id}` rather than off the settings page. Everything
      else is unchanged and was re-read in the same response: enforcement `active`, one
      approving review, strict up-to-date policy on, and the admin bypass at `always`.)*

## Beyond v1.0: Recorded, Not Scheduled [status: a list, not a plan]

**What this section is for.** Ideas that are worth keeping and are not worth doing yet. This
repository's rule is that deferred work gets recorded with its origin and then placed somewhere,
because an idea living only in an issue queue is indistinguishable from one nobody weighed. Phase
5.5 does the same for dataset candidates: **a milestone that lists everything schedules nothing**,
so nothing here is a commitment, and anything picked up gets a real phase entry with a scope and a
number first.

Milestone 6 is the terminal milestone. Entries below are explicitly after it.

### TOON instead of JSON in prompts — [#153](https://github.com/MarcelRoozekrans/Rag.NET/issues/153)

[TOON](https://github.com/toon-format/toon) encodes the JSON data model compactly for LLM prompts,
claiming 42.6% fewer tokens at 72.2% accuracy against JSON's 71.4%.

**One place in this repository fits it.** TOON's strength is uniform arrays of records going *into*
a prompt, and almost nothing here is that shape: `ChatAnswerEngine` sends `[Source N]` blocks of
unstructured prose, GraphRAG's global search joins community summaries as text, and GraphRAG's
extraction asks the model to *return* JSON, which is the opposite direction. The exception is
`GraphEntityExtractionBehavior.PerformGleaningAsync`, which re-serialises the entities and
relationships found so far into the prompt on every gleaning pass — a uniform array of flat
records, sent repeatedly.

**Not scheduled, for reasons worth keeping.** No official .NET implementation surfaced, so this is
writing and maintaining an encoder rather than adding a package — a poor trade for one call site.
The accuracy figures are a wash rather than a win, so the whole benefit is token cost. And that
cost lands on the smallest prompt path this library has: retrieved chunk text dominates prompt size
by a wide margin, which is why `RetrievalOptions.MaxContextTokens` (Phase 5.1's sibling work,
issue #85) is the lever that acts on the part that is actually large.

**What would settle it** is a measurement, not a claim: gleaning-prompt tokens per chunk, JSON
against TOON, counted with the same `cl100k_base` encoding `ContextBudgetBehavior` uses, with
entity and relationship counts asserted not to move — and compared against the honest baseline of
simply running fewer gleaning passes, which costs no new format at all. If the saving is a small
fraction of total prompt tokens, the answer is no and the measurement says so.
