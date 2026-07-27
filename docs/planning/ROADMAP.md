# Project Roadmap

Backlog source: the unchecked items in `docs/reference/features.md` (31 items as of 2026-07-24).
Every backlog item is assigned to exactly one phase below. When a phase completes, tick the
corresponding rows in features.md.

## Recorded follow-up debts (cross-phase, from review cycles)

Anything added here follows one rule: record it with its origin, then schedule it into a
phase or re-justify it. Closed items move to the list below rather than vanishing, so a
future reader can tell the difference between "never existed" and "dealt with".

- **Fourth filename sanitizer** (Phase 2.1, Part C): `EmbeddedMessageMetadata.Sanitize` in
  `Rag.NET.Parsers.Email` duplicates `FileNameSanitizer` because that type lives in
  `Rag.NET.DataProviders`, which parsers do not reference. Behaviourally consistent bar three
  divergences: the all-replacement fallback (`"///"` → `"___"` vs `"embedded-message"`), the
  length cap (`FileNameSanitizer` defaults to 128, `EmbeddedMessageMetadata.MaxNameLength` is
  64), and post-truncation trimming (`FileNameSanitizer.TrimEdges` re-trims to a fixed point
  over all `char.IsWhiteSpace`; `EmbeddedMessageMetadata`'s `TrimEnd('.', ' ')` leaves a
  re-exposed non-breaking space). Fix is to relocate
  `FileNameSanitizer` to `Rag.NET.Abstractions` and delete both copies — a package-layout
  change, so deliberately not done inside 2.1. → unscheduled
- **Stack-recursive email traversal** (Phase 2.1, Part C): `MaxEmbeddedDepth` is capped at 64
  because the embedded-message traversal recurses on the stack — ~500 levels (~40 KB of
  crafted MIME at ~81 bytes/level) terminates the process with an uncatchable
  `STATUS_STACK_OVERFLOW`. The ceiling is measured headroom, not a proof. Converting the
  traversal to an explicit work queue would remove the class entirely and is the real fix if
  a large `MaxEmbeddedDepth` is ever wanted. → unscheduled
- **Unsanitized webhook filename** (found in the Phase 2.1 Part A review):
  `src/Rag.NET.Api/Webhooks/GenericWebhookPayloadParser.cs:77` builds `$"{documentId}.txt"`
  straight from an untrusted webhook payload. Different assembly (`Rag.NET.Api` does not
  reference `Rag.NET.DataProviders`), so outside the connector scope of 2.1. → unscheduled

### Closed

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

## Milestone 2: Deferred Items & Technical Debt [status: active]
**Goal:** Follow through on what Milestone 1 delivered around rather than through — the features scoped out during brainstorming, and the debt review cycles surfaced. No delivered feature row should keep an unstated caveat.
**Started:** 2026-07-26
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Every Milestone 1 deferral delivered or re-recorded with a current reason
- [ ] The follow-up debt list above empty or explicitly re-justified
- [ ] All tests passing

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

### Phase 2.5: Service Bus Ingestion Trigger [status: pending]
**Items:** Service Bus trigger alongside the existing webhook/polling paths (deferred in Phase 1.3)

**Not in scope:** the CLI reindex command (belongs with the CLI tool in Milestone 4); Pinecone live sparse-write verification (needs a live account — documented as a coverage gap by decision on 2026-07-26).

## Milestone 3: Quality Hardening & Evaluation [status: pending]
**Goal:** Close the evaluation-tooling gap and harden quality: RAGAS metrics, dataset tooling, A/B testing, pipeline debugging, and CI coverage for the Docker-dependent suites.
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Integration/vector-store suites run in CI (Dockerized)
- [ ] All tests passing

### Phase 3.1: RAGAS-Style Metrics [status: pending]
**Backlog items:** RAGAS-Style Metrics

### Phase 3.2: Evaluation Dataset Builder [status: pending]
**Backlog items:** Evaluation Dataset Builder

### Phase 3.3: A/B Testing Framework [status: pending]
**Backlog items:** A/B Testing Framework

### Phase 3.4: Pipeline Debugger / Trace Viewer [status: pending]
**Backlog items:** Pipeline Debugger / Trace Viewer

### Phase 3.5: CI Integration Coverage [status: pending]
**Goal:** Run the Testcontainers-based vector-store and integration suites in CI. (Not a features.md row — quality-hardening scope.)

## Milestone 4: Release Readiness (v1.0) [status: pending]
**Goal:** Make Rag.NET shippable — CI, NuGet publishing, first-class configuration, logging, telemetry, and runnable samples.
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Full solution builds 0 warnings / 0 errors from a clean restore
- [ ] All non-Docker unit test projects passing
- [ ] CI pipeline builds, tests, and produces NuGet packages
- [ ] Release tagged v1.0

### Phase 4.1: NuGet Publishing Pipeline [status: pending]
**Goal:** GitHub Actions CI (build + test) and NuGet packaging/publishing with MinVer versioning.
**Backlog items:** NuGet Publishing Pipeline

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
