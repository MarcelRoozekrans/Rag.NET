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
- **No supported way to replace a built-in parser** (found while fixing the Phase 3.11 review's first
  finding): `AddRagNETServices()` registers `TextDocumentParser` and `MarkdownDocumentParser` before
  `configure?.Invoke(builder)` runs, so a user's own `text/plain` parser is always behind them in the
  first-match dispatch and never wins. 3.11 made that **loud** — the conflict guard now declares
  claims for both built-ins, so a user who declares `text/plain` gets a startup error. It did not
  make it **resolvable**: the error names `AddRagNet()` as the other claimant, and that is not a call
  anyone can remove.
  The perverse incentive is the part worth recording. Declaring your claim honestly gets you
  rejected; declaring nothing gets you the old silent failure, since `AddParser<T>()` alone still
  registers and still loses. No capability was lost — undeclared, a user lands exactly where they
  were before 3.11 — but the guard now points at a problem it offers no way out of.
  The missing feature is parser *replacement*, not another opt-out: something like
  `AddParser<T>(replaces: typeof(TextDocumentParser))`, or removing the built-in's `ServiceDescriptor`
  and its claim together. Deliberately not designed here — 3.11 was a bug-fix phase and this is API
  surface.
  → **Phase 4.2** (re-pointed 2026-08-02 by the Milestone 4 replan, design §5, from "with 4.1":
  parser *replacement* is API design on the registration path, and §5 groups it with the rest of
  the connector-and-ingestion API work under 4.2, leaving 4.1 the packaging mechanics this entry
  originally leaned on).
- **`HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize`** (found by Phase 3.16's audit
  of the other chunking strategies, design §7 — and it is the **inverse** of the defect that phase
  fixed). Its chunks are one heading subtree each, unbounded above, and `BookChunkingStrategy`,
  `LegalChunkingStrategy` and `AcademicPaperChunkingStrategy` all delegate to it — so **a user
  setting `MaxChunkSize` on any of those three templates gets no effect from it**. Plausibly
  deliberate: a heading subtree is a semantic unit and truncating it would defeat the strategy's
  purpose. But nothing documents that, and an option a user can set that silently does nothing is a
  contract defect whether or not the behaviour behind it is right. Deliberately not fixed in 3.16 —
  that phase's entry put the other strategies out of scope "unless the same shape is found in them —
  in which case say so rather than widening quietly", and this entry is the saying-so. The decision
  to make is *document the limit or honour the option*, not a presumed fix.
  → **Milestone 4, with 4.1**, which is when the public API — including every option a user can
  set — gets scrutinised for packaging.
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
  **Both sightings lost the test's name** — the second because the run used summary-only logging —
  which vindicates the standing instruction rather than replacing it. That instruction is now the
  whole entry: **the failing test's name is the one piece of evidence needed, it has been lost
  twice, and capturing the next occurrence with `--logger trx` *before* any re-run is the only
  thing that gets it.** A summary-only run that hits this failure is evidence destroyed.
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
  removed; note the `MessageChild` backstop referenced below has since become Phase 4.3] — the
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
  satisfiable or gone.
- **Three pieces of house furniture this repository lacks** (recorded in the Phase 3.5 design as out
  of scope, scheduled here so they do not stay open notes). All three exist in
  `MarcelRoozekrans/AdoNet.Async` and none exists here:
  - **`docs.yml`** — a Docusaurus site is already in the tree (`sidebars.ts`, `src/css/custom.css`,
    the whole `docs/` directory) and **nothing publishes it**. Written docs that nobody can read are
    the same shape of gap as tests that never run, which is what 3.5 was about; it was kept out of
    3.5 because a test-coverage phase is the wrong place to acquire a publishing pipeline, not
    because it is small.
  - **`.commitlintrc.yml`** — this repository already writes conventional commits by convention;
    nothing enforces it, and release-please in 4.1 will read those messages.
  - **`renovate.json`** — no automated dependency updates at all.
  → **owners assigned 2026-08-02 by the Milestone 4 replan: `docs.yml` → Phase 4.5** (design §5 —
  with the sidebar sweep, when the docs get read end to end), **`.commitlintrc.yml` and
  `renovate.json` → Phase 4.1** (the commitlint pairing this entry already argued, and dependency
  automation is the same release-plumbing pass; §5 is silent on renovate, so that half is the
  replan's assignment rather than the design's).
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
  above; only the optional re-measure remains against this deadline.]
- **`docs/reference/ci.md` does not list the nine ablation cells** (found in Phase 3.15, while
  writing up the table). That page counts "eleven cases" in what the nightly measures, and the
  nine ablation cells are now real gated cases in `BeirRunBudget` — so the page's count is stale
  and its inventory of what `RAGNET_BEIR_LONG_RUNS` unlocks is incomplete. A documentation
  defect, not a gating one: the budget table itself throws on an untimed dataset, so the code's
  own inventory cannot drift the way the page's did.
  → **Milestone 4, with 4.1**, which adds the `pack-push` job to `ci.yml` and is when
  `docs/reference/ci.md` has to be reread and rewritten against the workflows anyway.
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
  → **stays in Milestone 3's scope** (re-pointed 2026-08-02 by the Milestone 4 replan, design §5,
  which refuses to smuggle it into 4: run or explicitly declined before Milestone 3 closes, and
  declined gets written here, not implied). **And a correction to the design's own §5:** it routed
  the FiQA-qrels check to Phase 4.0 — "one read of the qrels settles it" — but 4.0's plan scoped
  that phase to its three guards and the read was not performed; the check stays exactly where
  this entry put it, first thing for whoever runs TREC-COVID.
- **`docs/reference/features.md` documents an observability package that does not exist — a live
  Definition-of-Done failure** (found by the 2026-08-02 milestone audit; the first failing DoD
  criterion this milestone has recorded, now annotated on the DoD in both planning files). The
  detail section at `features.md:666-676` reads "OpenTelemetry Tracing & Metrics — **Status:**
  ✅ Done, **Package:** `Rag.NET.Telemetry`", and describes `.UseTelemetry()` registration, GenAI
  semantic conventions (`gen_ai.system`, `gen_ai.request.model`, …) and metrics named
  `ragnet.retrieve.latency`, `ragnet.answer.tokens` and `ragnet.embed.batch_size`. **None of it
  exists.** There is no `Rag.NET.Telemetry` package, no `UseTelemetry` anywhere in `src/`, and no
  `gen_ai.*` attribute; the real instruments are `internal` in
  `src/Rag.NET/Telemetry/RagTelemetry.cs` under different names (`ragnet.retrieve.duration`,
  `ragnet.llm.tokens`). The summary-matrix row at `features.md:1135` is **unchecked** — so the
  detail section contradicts its own matrix, the exact shape the 2026-07-27 scope correction
  opened this milestone on, and once again the matrix row is the honest one. This fails Milestone
  3's DoD criterion that detail sections, matrix and code agree, and nobody had recorded it.
  → **Phase 4.4, or any documentation pass before it** — 4.4 owns the OTel wiring, so the doc
  correction belongs with it or earlier; 4.4 must not start from a detail section that describes
  the package it has yet to build as already done. **Machine-guarded since Phase 4.0
  (2026-08-02):** `FeatureClaimTests` carries this claim in its `KnownFalseClaims` allow-list with
  the evidence above, and a staleness test fails the moment the entry is fixed *or* the claim
  leaves the docs — so 4.4 can neither forget it nor fix it without the allow-list noticing.
- **`BuildMetadata` drops `baseMetadata.CreatedAt`, so provider-ingested documents score as brand
  new** (found in Phase 2.2; recorded until now only in
  `docs/plans/2026-07-26-connector-metadata-design.md:237-240`, and surfaced into this list by
  the 2026-08-02 audit, because a closed phase's design doc is not a destination under this
  list's own rule — the doc even says "recorded so it is not rediscovered as new", and it was
  rediscovered as new anyway, by an audit, which is what recording a debt in a file nothing
  re-reads buys). `src/Rag.NET/DataProviders/RagPipelineExtensions.cs:322-328` builds a
  `DocumentMetadata` without copying `CreatedAt`, so
  `src/Rag.NET.Abstractions/Models/DocumentMetadata.cs:22` defaults it to `DateTime.UtcNow` —
  and the field is read: it is serialised into chunk metadata as `created_at` by
  `MetadataBehavior` for `TimeWeightedRetriever`, per its own doc comment. A real behaviour
  defect, not a documentation one: every document ingested through a data provider loses its
  real creation time and time-weighted retrieval ranks it as ingested-now.
  → **the next phase that touches the data-provider ingestion path, and failing that Phase 4.2**
  (owner assigned 2026-08-02 by the Milestone 4 replan's §5, with the options work on that path,
  replacing the bare milestone-as-deadline) — the same backstop shape as the `MessageChild` debt,
  because the fix is one copied property plus a test: a slot, not a phase.
- **The current `nightly.yml` has never executed, and its reranker download feeds nothing**
  (found by the 2026-08-02 audit, reading the workflow against the run history). No nightly has
  run since PR #7 merged; the last run, 2026-08-01, executed the pre-3.15 workflow. So the
  reranker provisioning Phase 3.15 added — the ~87 MB `ms-marco-MiniLM-L6-v2` download, its
  pinned `RERANKER_REVISION` and both SHA-256 verifications — is **unexecuted**: the same
  "workflow that has never run" state Phase 3.5 closed on ("the first pull-request run is the
  real verification"), recurring in the nightly two phases later. Worse, when it does run, the
  download feeds no test: every test reading `RAGNET_ONNX_RERANK_MODEL`/`_VOCAB` sits behind
  `RAGNET_BEIR_LONG_RUNS`, which that job deliberately never sets — so the nightly pays the
  download, the cache and the checksum verification for nothing it runs. **The decision is
  genuinely open and this entry does not pre-take it:** either the nightly should run something
  that uses the reranker (a budget question, with `BeirRunBudget` holding the numbers), or it
  should stop provisioning what nothing consumes.
  → **Milestone 4, with 4.1** — the phase that reworks `ci.yml` and must reread
  `docs/reference/ci.md` against the workflows anyway is where the decision lands; the first
  genuine run of the current nightly is the verification either way, and is worth having before
  that phase starts. **Phase 4.0 (2026-08-02) put the gate's status on the record:**
  `TestGateTests` found `RAGNET_BEIR_LONG_RUNS` satisfiable **only locally** — the fenced command
  in `docs/reference/ci.md` is the one place anything sets it — so "the download feeds nothing the
  nightly runs" is now asserted by a test rather than only recorded here; the 4.1 decision stands.
- **Two near-duplicate RAGAS test suites** (found by the 2026-08-02 audit):
  `tests/Rag.NET.Tests/Evaluation/` (~650 lines) and `tests/Rag.NET.Evaluation.Tests/Ragas/`
  (~1,570 lines) carry near-duplicate test names over the same metrics. Phase 3.1 removed
  exactly this hazard from `src/` — the malformed-reply defect lived in two duplicated copies of
  the scoring plumbing — and the two-copies shape survives in the tests that certify the fix: an
  assertion updated in one suite and not the other is how a metric gets one green certificate
  and one stale one, and the smaller suite is the very folder the 2026-07-27 scope correction
  records a narrowly-scoped search missing entirely. Nobody has recorded which suite is
  authoritative.
  → **Milestone 4, with 4.1** — merge them, or name one authoritative and delete the other, when
  the test surface is next scrutinised whole for packaging.
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
  service" is the question that phase exists to answer, and the new DoD's recording criterion
  holds it. **On the record twice since Phase 4.0 (2026-08-02):** `TestGateTests` lists this skip
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
  criterion holds it.
- **`features.md` claims `Rag.NET.Parsers.CSharp`, a package that does not exist under that
  name** (found by Phase 4.0's `FeatureClaimTests`, 2026-08-02 — the second of exactly two false
  claims in the 54 `✅ Done` sections, and the benign twin of the OTel ghost above): the feature
  is real and lives at `src/Rag.NET.Chunking.CSharp`; only the claimed package identity is wrong.
  Held in `KnownFalseClaims` with a staleness test, like the OTel entry, so it can neither be
  fixed silently nor forgotten loudly.
  → **Phase 4.1** — the packaging pass reads every package identity anyway, and a wrong published
  name is exactly what it exists to catch. (The replan's assignment, not design §5's, which
  predates 4.0 finding this claim.)
- **Two packages have never been exercised by any test at all** (declared honestly by Phase 4.0's
  `<VerifiedBy>` ledger, 2026-08-02): `Rag.NET.Mcp.Tool` (a host scaffold no test references) and
  `Rag.NET.Security.AspNetCore` (two types, zero test references). Both declare `VerifiedBy=none`,
  which the ledger's release gate — "no package declares `none`" — turns into a v1.0 blocker
  without failing today's build, because punishing an honest `none` is how a ledger becomes
  fiction.
  → **`Rag.NET.Mcp.Tool` → Phase 4.6** (the tooling phase — the same first-tests-for-an-executable
  shape as the CLI) and **`Rag.NET.Security.AspNetCore` → Phase 4.5** (the samples are the first
  thing that will host ASP.NET middleware end to end, and its first tests belong with that work).
  Assignments made at 4.0's close, not in design §5, which predates the ledger's findings; the
  release gate holds either way.
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
  footnoting it.
- **Two ghost directories from the PgVector rename are on disk and in no solution** (forced onto
  the record by Phase 4.0's ledger, 2026-08-02, which had to decide whether `src/Rag.NET.PgVector`
  was a 72nd package: it is not — untracked `bin`/`obj` only, no csproj, an empty leftover of the
  rename to `VectorStores.PgVector`, with a matching ghost at `tests/Rag.NET.PgVector.Tests`).
  Every figure that assumed 72 packages was counting it; the count is **71**. Not harmless while
  they sit there: one of the pair broke a `dotnet run` in Phase 3.16 by making a project name
  ambiguous — the same two-matches shape that killed the benchmarks re-measure via a leftover
  agent worktree.
  → **Phase 4.1** — delete both with the packaging pass, which is when the package inventory is
  settled for good.

### Closed

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
  labelling with only its optional re-measure outstanding.

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
**Completed:** 2026-07-27 (also codified the tag convention, enforced reserved keys, and added `provider_id`; five connectors' narrowed API field selections remain recorded as debt)

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

## Milestone 3: Quality Hardening & Evaluation [status: active]
**Goal:** Close the evaluation-tooling gap and harden quality: RAGAS metrics, dataset tooling, A/B testing, pipeline debugging, and CI coverage for the Docker-dependent suites.
**Started:** 2026-07-27
**Definition of Done:**
- [ ] All planned phases complete (14 of 16 as of 2026-08-02; 3.8 and 3.14 pending)
- [ ] No feature marked done in features.md lacks tests and docs — detail sections, summary matrix, and code agree — **failing as of the 2026-08-02 audit**: the OTel detail section (`features.md:666-676`) claims a package that does not exist; see the follow-up-debts list. [Phase 4.0's `FeatureClaimTests` swept all 54 Done claims on 2026-08-02: exactly **two** fail — the OTel ghost and the `Rag.NET.Parsers.CSharp` wrong name — both allow-listed with staleness guards, the other 52 naming code that exists]
- [ ] Integration/vector-store suites run in CI (Dockerized)
- [ ] All tests passing

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

### Phase 3.8: A/B Shadow Mode [status: pending]
**Goal:** The production half of the A/B framework — wrap a live pipeline, return the primary answer to the caller, run the secondary out-of-band and score it. (Not a features.md row of its own; it is the deferred half of the `A/B Testing Framework` row delivered in 3.3.)

Scoped out of Phase 3.3 deliberately, because it is a production-path concern with failure modes the offline harness does not have, and bolting it on would have given it none of the design attention they need:

- **No ground truth.** Production traffic has no reference answer, so Context Precision and Context Recall — which *throw* on an empty `ReferenceAnswer` — cannot run at all. Only the reference-free metrics apply, and the docs must say so rather than implying all four.
- **Doubled spend on every request**, invisible unless each variant gets its own ledger.
- **Fire-and-forget loss.** Secondary work running out-of-band is lost on host shutdown, and a naive implementation drops it silently.
- **The secondary must never break the primary.** `IRagPipeline.AskAsync` throws rather than returning a `Result`, so an unhandled secondary failure would surface on a request the caller had already been served.

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

### Phase 3.14: Library Comparison at Defaults [status: pending]
**Goal:** Compare Rag.NET's retrieval quality against other RAG libraries on the same corpus and the same embedding model, **each at its own defaults**. (Not a features.md row — scoped out of 3.7 and framed in the 3.12 design.)

Created by the 3.12 design, which decided the framing that 3.7 left open. 3.7 declined comparative tables because they are "only credible with genuinely equivalent configuration"; the 3.12 design went further and rejected *matched* configuration as the wrong target:

- **A matched-configuration table measures how carefully each library was configured**, not the libraries. Match the model, the chunk size and the top-k across four libraries and they converge on near-identical numbers, because at that point they are all calling the same embedding model through different syntax. The differences that survive are rounding.
- **The credible comparison is each library's defaults** — same corpus, same model, every configuration published in full. That measures the decisions a library makes on your behalf when you do not make them yourself, which is a real difference and the one a reader is choosing between.
- It is also the harder table to write honestly, because "our defaults win" is exactly what every such table concludes. Whatever ships must publish the configuration of every entrant, and a default that loses is a finding rather than a bug to be tuned away.

**Depends on** the 3.12 harness: the parity protocol, the descriptors and `EmbeddingCache` are what make running one corpus through several libraries affordable.

**Not in scope:** changing any Rag.NET default in response to the table within the same phase. Measure first; a defaults change is its own decision with its own phase.

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

**Completed:** 2026-08-02 (**the table, all nine cells measured** — parity protocol, judged queries only, each cell against its dataset's dense anchor: SciFact 0.64593 → +BM25 hybrid **0.69913** (+0.0532) → +HyDE **0.70001** (+0.0541) → +reranker **0.68442** (+0.0385); FiQA 0.37086 → **0.35665** (−0.0142) → **0.36543** (−0.0054) → **0.38458** (+0.0137); ArguAna 0.50432 → **0.51173** (+0.0074) → **0.50293** (−0.0014) → **0.47917** (−0.0252). **Every technique helps somewhere and hurts somewhere.** No row is free lift, which is what makes the table credible rather than promotional — the design's demand that it be able to go down is met on every row, not only the one built for it. **The design committed to per-dataset HyDE predictions before anything was built, and two of the three failed — recorded as failed rather than reframed.** FiQA, the positive control ("clear lift"), was flat: −0.0054. ArguAna, the negative control ("no lift, plausibly negative"), held: −0.0014. SciFact ("modest lift, smaller than FiQA's") gained the most of the three: +0.0541. The design named "FiQA shows no lift" as the outcome that would make the table uninterpretable, because a weak model and an unhelpful method are indistinguishable in a run that is flat everywhere — **that escape hatch did not apply**: SciFact gained +0.0541 from the same model, the same prompt and the same cache, so FiQA's flat cell is a measurement, not an artefact. The explanation that survives — HyDE helps when the hypothetical sits closer to the corpus register than the query does — is recorded **as post-hoc**, because it is one. ArguAna's negative control has an **observed mechanism**, recorded during generation independently of the measurement: its hypotheticals are compressed restatements of the input argument, recycling its own statistics, and ArguAna asks for the best *counter*argument — so HyDE moves the search vector toward the query's own position and away from the target. **Two library defects were found and fixed, and neither is what the phase set out to measure.** First: `OnnxReranker.TokenizePair` was not a WordPiece tokenizer (`a912187`). It whitespace-split and looked up whole lowercased words, mapping every miss to `[UNK]` — measured over both corpora in full, **26.59% of SciFact's 1,112,417 words and 17.62% of FiQA's 7,660,017 reached the model as `[UNK]`**; through WordPiece, 0.01% and 0.10%. The first reranker measurement showed harm everywhere — SciFact 0.56693, FiQA 0.34085, ArguAna 0.41806 — and after the fix the row **gains 0.117 on SciFact, 0.061 on ArguAna and 0.044 on FiQA from tokenization alone**. It was found because the row hurt on FiQA too, the MS MARCO-like corpus where the design predicted a cross-encoder helps, and uniform harm across in-domain and out-of-domain corpora is more consistent with a defect than with a technique. **No guard could have caught it**: `AssertRerankerReordered` proves the cross-encoder *moved* the ranking, and garbage-but-varying scores reorder every query. The new guard is an offline tokenizer round-trip test that fails on the old algorithm. The fix also corrected hardcoded `[UNK]`/`[CLS]`/`[SEP]` ids, a truncation rule that starved long queries, and a `MaxLength ≤ 3` case that exceeded its own ceiling; the shared plumbing lives in `src/Shared/BertWordPieceTokenization.cs`, linked into both ONNX packages. Second: **the harness retrieved unjudged queries** (`339f3d6`). `MeasureAsync` retrieved for every query while `IrMetrics` scores only judged ones — SciFact retrieved 1,109 to score 300, FiQA 6,648 to score 648 — waste everywhere, and it **broke the HyDE row**, whose refuse-on-miss cache failed on the first unjudged query. ArguAna concealed it: all 1,406 of its queries are judged. Metrics unchanged by construction and verified — parity reproduced 0.64593 and 0.50432 exactly — and every recorded query counter was restated across nine files. **FiQA's real leg, deferred out of 3.12 and re-based by 3.16, is measured at last: nDCG@10 0.35569 against parity 0.37086, delta −0.01517** — 121,236 units over **57,600 of 57,638** documents, the 38 empty entries (one judged relevant) contributing nothing, stated here because 3.12's debt required it stated with the number; all 648 judged queries pooled; **1 h 4 m against the derived ~1.5–2 h — the estimate overshot, and that is recorded rather than quietly replaced.** The three real deltas now exist — SciFact **+0.03148**, ArguAna **−0.02873**, FiQA **−0.01517** — and they support the explanation 3.12 proposed and 3.16 tested, that the sign tracks whether relevance is passage-level or document-level: recorded as **consistent with three corpora, not as newly proven**. **The HyDE row is reproducible by construction:** 7,062 hypotheticals for the 2,354 judged queries at `HypothesisCount = 3`, `openai/gpt-4o-mini` at `HydeOptions.HypothesisTemperature` (0.8), **$0.66**, zero failures. The cache identity is `openai/gpt-4o-mini@t0.8` — the temperature is in the key, added after a review found that sampling settings outside the key would silently serve text drawn from another distribution. The table run never calls an LLM; a cache miss fails naming the key. **The cache is never committed** — it derives from BEIR queries, and this project's standing position is that nothing is redistributed. All nine ablation figures and FiQA's real leg are pinned in `BeirReproduction` at ±0.005 (`899f4b2`), with a fast-tier theory so a mutated figure fails on every push rather than only under an opted-in run. **The BM25 comparability debt is closed by labelling**: the `+BM25 hybrid` row is published as a Rag.NET-internal comparison with no published reference, and 3.7 §2's rejection of a benchmark-only analyzer stands — moved to the Closed list, with the FiQA empty-corpus debt the real number now states. **Three debts recorded in the follow-up list, each with its origin:** the reranker row permutes only the ten documents it is evaluated on — `TopK` equals the cutoff, so Recall@10 is frozen by construction, visible in SciFact's reranker Recall@10 of 0.78667, identical to dense; **a design flaw in this phase's own plan, not a defect in the code**, and the row understates what a cross-encoder can do → the next re-measure of the table, backstopped by Milestone 4; `docs/reference/ci.md` still counts "eleven cases" and does not list the nine ablation cells now gated in `BeirRunBudget` → Milestone 4, with 4.1; and TREC-COVID and EnronQA, deferred again unchanged from 3.12 — the `2^rel − 1` path has still never seen a graded *dataset* → Milestone 4, with the release-readiness work. [**Re-pointed 2026-08-02 by the Milestone 4 replan, design §5: TREC-COVID and EnronQA stay in Milestone 3's scope** — run or explicitly declined before this milestone closes, not smuggled into 4; the FiQA-qrels check recorded on that debt still comes first. See the follow-up-debts list.])

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

## Milestone 4: Release Readiness (v1.0) [status: active]
**Goal:** Make Rag.NET shippable — CI, NuGet publishing, first-class configuration, logging, telemetry, and runnable samples — and prove that what ships works, which the first half of this sentence cannot do on its own: a green build has now been watched to coexist with four live defects.
**Started:** 2026-08-02

> **Replanned 2026-08-02** (`docs/plans/2026-08-02-milestone-4-replan-design.md`, motivated by the
> Milestone 3 audit of the same date). Verification is this milestone's dominant cost, not a
> footnote to it — Phase 4.0 measured **61 of 71 packages at `VerifiedBy=unit`**, exercised only
> against fakes — and the phase list below will grow a **recorded-responses phase** (design §3)
> covering the ~20 packages that talk to live services; that phase is referenced by design section
> rather than number until it is scheduled. v1.0 covers all 71 packages and all 54 Done claims:
> no preview tier.

**Definition of Done** (rewritten 2026-08-02 by the replan's §6. The previous DoD — all phases
complete, 0 warnings from a clean restore, non-Docker unit tests passing, CI produces packages,
tag v1.0 — was **already fully satisfied while four defects were live**: late chunking inert since
Phase 1.1, the default chunker emitting one chunk per word, `OnnxReranker` sending 26% of every
document to the model as `[UNK]`, and `features.md` advertising a package that does not exist. Not
one was found by a test. Every criterion below can be false, and something checks it):
- [ ] All planned phases complete
- [ ] Full solution builds 0 warnings / 0 errors from a clean restore
- [ ] All test projects passing — **and no test is gated behind a condition nothing satisfies** (`TestGateTests`, Phase 4.0; **failing today, knowingly**: four gates are satisfiable nowhere — see the follow-up-debts list)
- [ ] **Every one of the 54 `features.md` Done claims names code that exists** (`FeatureClaimTests`, Phase 4.0; **failing today, knowingly**: two claims sit in `KnownFalseClaims` with owners → 4.4 and 4.1)
- [ ] **No package declares `VerifiedBy=none`** (the ledger's release gate, Phase 4.0; **failing today, honestly**: `Rag.NET.Mcp.Tool` → 4.6, `Rag.NET.Security.AspNetCore` → 4.5)
- [ ] **Every package talking to a live service has a scrubbed, dated recording** (the recorded-responses phase, design §3; `recorded` and `live` both stand at 0 of ~20 today)
- [ ] CI pipeline builds, tests, and produces NuGet packages
- [ ] Release tagged v1.0

**What these guards do not fix** (design §7, stated so the milestone does not claim more than it
does): a recording proves one exchange happened once, not that the API still behaves that way; the
ledger proves a package was exercised, not exercised *well* — `VerifiedBy=unit` on a package with
one trivial test satisfies its letter; the agreement test checks that named code exists, not that
it does what the row says; and **none of them would have caught the reranker tokenizer** — that was
found by a prediction stated in advance and reported honestly when it failed, which is Milestone
3's transferable practice and is not automatable.

### Phase 4.0: Verification Ledger and Claim Agreement [status: complete]
**Goal:** Open the milestone with a measurement: three mechanical guards that make the new Definition of Done falsifiable — every `features.md` Done claim must name code that exists, no test may be gated behind a condition nothing satisfies, and every package must declare how it has been verified. Builds no features, ships nothing. (Not a features.md row — the replan's opening phase.)
**Plan:** `docs/plans/2026-08-02-milestone-4-replan-design.md` + `2026-08-02-phase-4-0-verification-ledger-implementation.md`
**Completed:** 2026-08-02 (**three guards, all cheap, and the numbers they produced are the phase's output.** **(a) `FeatureClaimTests` (`c235a9b`, `d77036f`) parses `docs/reference/features.md` and checks all 54 sections marked `✅ Done` — 54 of 54, not the ~51 the plan predicted — resolving 73 package claims at a measured false-positive rate of 0 of 73.** The residue the plan expected to need risky identifier-extraction turned out to be structured SaaS-connector tables rather than prose, so none was written. **Two claims are false, and both now live in a `KnownFalseClaims` allow-list with evidence and an owning phase, each held by a staleness test that fails the moment the entry is fixed *or* the claim leaves the docs** — an allow-list nothing re-checks is how a known defect becomes furniture. `Rag.NET.Telemetry` is **genuinely false** — the audit's finding (A), now machine-guarded rather than only recorded: no such package, no `.UseTelemetry()`, no `gen_ai.*` attribute, metric names (`ragnet.retrieve.latency`, `ragnet.answer.tokens`, `ragnet.embed.batch_size`) matching nothing in `src/Rag.NET/Telemetry/RagTelemetry.cs`, where the real instruments are `internal` under different names, and its own matrix row unchecked → Phase 4.4 owns the fix. `Rag.NET.Parsers.CSharp` is a **wrong name, not a ghost**: the feature is real and lives at `src/Rag.NET.Chunking.CSharp` → 4.1, with the packaging pass that reads every package identity anyway. **(b) `TestGateTests` (`c613fe1`) enumerates every gating site — 28, not the plan's 29; the 29th was `Assert.SkipWhen` inside a doc comment — and asserts each gate is satisfiable somewhere, reading raw source and never compiled output**, because a compiled-output check is blind to the worst case: an `#if` block that is not compiled reports nothing at all. Prose does not satisfy a gate — only a fenced, runnable command counts — and a `secrets.*` workflow mapping is not accepted as evidence either, because the repository cannot show a secret exists. The distribution: **0 gates satisfiable in `ci.yml`** (by design), **5 only in the nightly**, **1 only locally** (`RAGNET_BEIR_LONG_RUNS`, via the fenced command in `docs/reference/ci.md`), and **4 satisfiable nowhere**: `RAGNET_DOCINTEL_ENDPOINT`/`RAGNET_DOCINTEL_KEY` (secrets never configured anywhere — the Document Intelligence live suite has never run, as its debt entry records), `RAGNET_TESSDATA` (its only reader sits inside an uncompiled block), and `ENABLE_OCR` — which is worse than a test gap: nothing sets `EnableOcr`, and the flag **also compiles the production Tesseract engine out**, so the shipped PDF parser has no real OCR in any default build. Two **permanent** `[Fact(Skip)]`s are now visible with their reasons rather than latent: `PineconeVectorStoreTests` (Pinecone Local rejects sparse-on-dense) and `AzureAISearchVectorStoreTests` (the simulator has no OData filters — the skip the audit found in no planning record). **(c) Every package under `src/` now declares `<VerifiedBy>` (`46b6bd8`, `1b206e4`)** — `unit`, `container`, `recorded`, `live` or `none` — extending the `<RequiresDocker>`/`<RequiresSecrets>` convention `ci.yml` already selects on rather than inventing a parallel one. Two gates, deliberately split: "every package declares a value" hard-fails today; "no package declares `none`" is the **release** gate and does not fail the build, because punishing an honest `none` is how a ledger becomes fiction. **The distribution across 71 packages: `unit` 61, `container` 8, `recorded` 0, `live` 0, `none` 2.** The two `none` are `Rag.NET.Mcp.Tool` (host scaffold, no test references it) and `Rag.NET.Security.AspNetCore` (two types, zero test references). The eight `container` are `Rag.NET` itself, `Rag.NET.Security`, `Rag.NET.Ingestion.AzureServiceBus`, and the PgVector, Qdrant, Chroma, Weaviate and Pinecone stores. Two judgments went against the mechanical answer and are recorded as judgments: **AzureAISearch is `unit`, not `container`, despite having Docker-tier tests** — its container is a community simulator without OData filters and of unconfirmed fidelity — and **`Parsers.Pdf.AzureDocumentIntelligence` is `unit`, not `recorded`**, because its WireMock cassettes were hand-written, never recorded from the live service, and a hand-written cassette verifies the code against *our belief* about the API, the exact shape the reranker defect punished. **The number that should shape the rest of this milestone: 61 of 71 packages have only ever been exercised against fakes** — the state late chunking was in for five phases, now visible in every csproj rather than latent. **The ledger also forced a count correction: there are 71 packages, not 72.** `src/Rag.NET.PgVector` is an empty leftover of the rename to `VectorStores.PgVector` — untracked `bin`/`obj`, no csproj — with a matching ghost at `tests/Rag.NET.PgVector.Tests`; recorded as a debt in the follow-up list, since one of the pair already broke a `dotnet run` in Phase 3.16 by making a project name ambiguous. **One §5 routing did not happen here and is said rather than absorbed:** the design sent the FiQA-qrels check ("one read settles it") to this phase, but the implementation plan scoped 4.0 to the three guards and the read was not performed — it stays with the TREC-COVID debt, first thing for whoever runs that dataset.)

### Phase 4.1: NuGet Packaging & Publishing [status: pending]
**Goal:** NuGet packaging, versioning and publishing on top of a pipeline that already builds and tests.
**Backlog items:** NuGet Publishing Pipeline

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

### Phase 4.2: Options Alignment & Validation [status: pending]
**Goal:** Align pipeline options on IOptions and validate them with ZeroAlloc.Validation.
**Backlog items:** IOptions Alignment + ZeroAlloc Validation for pipeline options

### Phase 4.3: Structured Logging Enrichment [status: pending]
**Goal:** Consistent scoped/structured logging across ingestion, retrieval, and answer generation.
**Backlog items:** Structured Logging Enrichment

### Phase 4.4: OpenTelemetry Tracing & Metrics [status: pending]
**Goal:** First-class OTel wiring (exporter guidance, resource attributes, sample dashboards) on top of the existing RagTelemetry ActivitySource/Meter.
**Backlog items:** OpenTelemetry Tracing & Metrics

### Phase 4.5: Sample Applications [status: pending]
**Goal:** End-to-end runnable samples covering the main library scenarios.
**Backlog items:** Sample Applications

### Phase 4.6: Rag.NET CLI Tool [status: pending]
**Goal:** `dotnet tool` for ingest/query/evaluate against a configured pipeline.
**Backlog items:** Rag.NET CLI Tool
