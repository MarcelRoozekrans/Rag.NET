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
  → **Phase 4.4** (OTel wiring, which has to reason about span context across async iterators
  regardless)
- **`MessageChild<TMessage>` is a union by convention** (**created by Phase 3.9**, not pre-existing):
  `EmbeddedMessage != null` means "descend", and otherwise `OpenAsync` and `MimeType` must *both* be
  non-null. Nothing enforces that — the only check is a bare `yield break` in
  `EmbeddedTraversal.DispatchAsync`. Both shipped adapters construct it correctly, so this is latent
  rather than live, but a future adapter that sets `MimeType` and forgets `OpenAsync` drops every
  attachment with no log line at all. The recursion this replaced made that state unrepresentable,
  so 3.9 traded a compile-time guarantee for a runtime convention and did not say so.
  → **Phase 3.10** (Archive Parser — the next phase to touch this area, and the one that adds a
  third container shape to the same type)
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
  → **Milestone 4**, with 4.1, which is when the public API gets scrutinised for packaging anyway.
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
  → **Milestone 4**, alongside the rest of the release-readiness work. `.commitlintrc.yml` pairs
  naturally with 4.1, since release-please depends on the commit format holding.

### Closed

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
- [ ] All planned phases complete
- [ ] No feature marked done in features.md lacks tests and docs — detail sections, summary matrix, and code agree
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

### Phase 3.10: Archive Parser (ZIP) [status: pending]
**Goal:** Parse `.zip` archives by dispatching each entry to the registered parser for its content type, closing a gap where zipped email attachments are silently dropped. (features.md row: **Archive Parser (ZIP)**.)

Raised while designing 3.9. Today a `.zip` attachment reaches `EmailAttachmentDispatcher`, matches no parser, logs a warning and yields nothing — the archive's contents never reach the index. Every attachment type with no registered parser behaves this way; the warning is the only signal that content was dropped. That default is deliberate and stays, but zip is common enough in real mail that it should not be one of the misses.

Runs **after 3.9**, which is what makes it cheap: the shared traversal driver and the injected descent policy are the machinery a nested-container parser needs, and building them once for two containers beats building them twice.

**Scope:**
- Dispatch each entry by content type through the existing parser registry, matching how the email parsers already dispatch attachments.
- **Cap decompression ratio and entry count.** A zip bomb expands without bound from a small file, and an archive's own headers cannot be trusted to declare it. This is the first parser to accept an untrusted structure that *expands*, so the limits are part of the feature, not a hardening pass afterwards.
- **Sanitize entry names.** `../` traversal and absolute paths are the classic archive defect; `FileNameSanitizer` in `Rag.NET.Abstractions` already exists and is the fourth-copy lesson from 2.1 — use it rather than writing another.
- **Share one budget across nested containers.** `zip → .eml → zip` is the same unbounded-recursion shape the email parsers bound. `EmbeddedMessageContext` carries depth and budget through `DocumentMetadata.Tags` precisely so the accounting survives a hop through `IDocumentParser`; the archive parser rides that channel rather than inventing a second one.
- **Make `MessageChild<TMessage>` a real union** (the 3.9-created debt above). This phase adds a third container shape to that type, which is the moment its "descend, or open — never neither" rule stops being enforced by two adapters that happen to be written correctly.

**Not in scope:** other archive formats (7z, tar, rar), encrypted archives, and any change to the warn-and-skip default for unregistered content types.

### Phase 3.7: Retrieval Quality Benchmark Harness [status: pending]
**Goal:** Measure retrieval quality against public benchmarks with published reference numbers, so correctness is *demonstrable* rather than asserted. (Not a features.md row — quality-hardening scope.)

Distinct from `EvaluationDatasetBuilder` (Phase 3.2), which synthesises QA pairs from *your* corpus: useful for iterating on your own data, but it can only show that a change moved a number, never that the number is right. Also distinct from the existing `Rag.NET.Benchmarks` project and `docs/reference/benchmarks.md`, which measure **speed**; this measures **quality**. Keep the names apart.

**First cut: SciFact only, to prove parity.** ~5k documents, runs in seconds, and its abstracts are short enough that chunk-to-document aggregation is easy to validate. One number matching the published reference is worth more than five unvalidated ones — a harness defect is inherited by every dataset added after it.

**The methodological trap, recorded up front.** BEIR is evaluated at **document** level: qrels map `query_id → doc_id`, and nDCG@10 ranks documents. Rag.NET chunks. Ranking *chunks* computes a different quantity that merely resembles nDCG@10. The harness must map chunk → parent document, max-pool to one score per document, dedupe, and only then take the top k. This bites unevenly, which is what makes it dangerous: SciFact abstracts and ArguAna arguments are mostly single-chunk so those numbers look plausible, while FiQA and TREC-COVID have long documents where the discrepancy is real — a table that is right in the cheap places and wrong in the expensive ones. Also pin BEIR's `title + text` concatenation and cosine over normalised embeddings; both shift the numbers.

**Scope:**
- `Rag.NET.Benchmarks.Quality` — BEIR qrels/corpus/queries loaders, nDCG@k, Recall@k, MRR implemented natively (no `pytrec_eval` dependency), and the chunk-to-document aggregation above.
- Datasets downloaded on demand and cached; **never vendored into the repo**. Record each dataset's licence — they differ across BEIR.
- Env-gated like the `RAGNET_*` precedents. Corpus scale is an *embedding cost* problem rather than a disk one, so anything past SciFact needs a cached-embeddings artifact and stays out of default CI.

**Later, once parity holds:** FiQA (long documents, where HyDE should show lift), ArguAna as a **negative control** (HyDE should *not* help; a harness that shows lift everywhere is broken), then EnronQA for the private-corpus and multi-tenant story. Ablation table — baseline dense → +BM25 hybrid → +HyDE → +reranker — using the behaviors that already exist.

**Not in scope here:** comparative tables against other libraries. Legitimate and worth doing, but only credible with genuinely equivalent configuration (same embedding model, chunk size, top-k), which is a separate piece of work and the part such tables are usually attacked on.

### Phase 3.8: A/B Shadow Mode [status: pending]
**Goal:** The production half of the A/B framework — wrap a live pipeline, return the primary answer to the caller, run the secondary out-of-band and score it. (Not a features.md row of its own; it is the deferred half of the `A/B Testing Framework` row delivered in 3.3.)

Scoped out of Phase 3.3 deliberately, because it is a production-path concern with failure modes the offline harness does not have, and bolting it on would have given it none of the design attention they need:

- **No ground truth.** Production traffic has no reference answer, so Context Precision and Context Recall — which *throw* on an empty `ReferenceAnswer` — cannot run at all. Only the reference-free metrics apply, and the docs must say so rather than implying all four.
- **Doubled spend on every request**, invisible unless each variant gets its own ledger.
- **Fire-and-forget loss.** Secondary work running out-of-band is lost on host shutdown, and a naive implementation drops it silently.
- **The secondary must never break the primary.** `IRagPipeline.AskAsync` throws rather than returning a `Result`, so an unhandled secondary failure would surface on a request the caller had already been served.

## Milestone 4: Release Readiness (v1.0) [status: pending]
**Goal:** Make Rag.NET shippable — CI, NuGet publishing, first-class configuration, logging, telemetry, and runnable samples.
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Full solution builds 0 warnings / 0 errors from a clean restore
- [ ] All non-Docker unit test projects passing
- [ ] CI pipeline builds, tests, and produces NuGet packages
- [ ] Release tagged v1.0

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
