# Project Roadmap

Backlog source: the unchecked items in `docs/reference/features.md` (31 items as of 2026-07-24).
Every backlog item is assigned to exactly one phase below. When a phase completes, tick the
corresponding rows in features.md.

## Recorded follow-up debts (cross-phase, from review cycles)

- **Connector metadata consistency** (Phase 1.6): only Exchange/Linear populate the new
  `FileHandle.Metadata`; ~20 sibling connectors have obvious candidates (Gmail from/date,
  Teams team/channel, Zendesk status, …) currently inlined into rendered Markdown.
- **Graph transport-exception mapping** (Phase 1.6): raw `HttpRequestException` bypasses the
  Result channel in all Graph connectors (Exchange inherits the sibling posture).
- **Shared `SanitizeFileName` helper** (Phase 1.6): now three verbatim copies (Gmail,
  Exchange, Linear) — extract into `Rag.NET.DataProviders`.
- **Embedded-message recursion** (Phase 1.5): EML `MessagePart` / MSG nested `Storage.Message`
  are warn-and-skipped; recursing them is the natural follow-up.
- **PDF table dominance-guard refinement** (Phase 1.5): exempt runs averaging <= ~2 words/cell
  to rescue full-page Key/Value tables (candidate noted in the guide).
- **Persistent-memory score normalization** (Phase 1.2): `PersistentConversationMemory`'s
  MinScore filter is incompatible with federated RRF scores (documented limitation).
- **`ConfigureResilience` dangling pipeline** (pre-existing): registered but unconsumed
  (documented in observability.md + resilience.md).

## Milestone 1: Feature Backlog [status: active]
**Goal:** Work the remaining feature backlog to completion — chunking, retrieval techniques, ingestion ops, resilience, parsers, connectors, and vector stores.
**Started:** 2026-07-24
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Every feature row it covers ticked in features.md with tests and docs
- [ ] All tests passing

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

### Phase 1.7: Vector Stores [status: pending]
**Backlog items:** Weaviate Vector Store; Chroma Vector Store; Pinecone Vector Store

## Milestone 2: Quality Hardening & Evaluation [status: pending]
**Goal:** Close the evaluation-tooling gap and harden quality: RAGAS metrics, dataset tooling, A/B testing, pipeline debugging, and CI coverage for the Docker-dependent suites.
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Integration/vector-store suites run in CI (Dockerized)
- [ ] All tests passing

### Phase 2.1: RAGAS-Style Metrics [status: pending]
**Backlog items:** RAGAS-Style Metrics

### Phase 2.2: Evaluation Dataset Builder [status: pending]
**Backlog items:** Evaluation Dataset Builder

### Phase 2.3: A/B Testing Framework [status: pending]
**Backlog items:** A/B Testing Framework

### Phase 2.4: Pipeline Debugger / Trace Viewer [status: pending]
**Backlog items:** Pipeline Debugger / Trace Viewer

### Phase 2.5: CI Integration Coverage [status: pending]
**Goal:** Run the Testcontainers-based vector-store and integration suites in CI. (Not a features.md row — quality-hardening scope.)

## Milestone 3: Release Readiness (v1.0) [status: pending]
**Goal:** Make Rag.NET shippable — CI, NuGet publishing, first-class configuration, logging, telemetry, and runnable samples.
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Full solution builds 0 warnings / 0 errors from a clean restore
- [ ] All non-Docker unit test projects passing
- [ ] CI pipeline builds, tests, and produces NuGet packages
- [ ] Release tagged v1.0

### Phase 3.1: NuGet Publishing Pipeline [status: pending]
**Goal:** GitHub Actions CI (build + test) and NuGet packaging/publishing with MinVer versioning.
**Backlog items:** NuGet Publishing Pipeline

### Phase 3.2: Options Alignment & Validation [status: pending]
**Goal:** Align pipeline options on IOptions and validate them with ZeroAlloc.Validation.
**Backlog items:** IOptions Alignment + ZeroAlloc Validation for pipeline options

### Phase 3.3: Structured Logging Enrichment [status: pending]
**Goal:** Consistent scoped/structured logging across ingestion, retrieval, and answer generation.
**Backlog items:** Structured Logging Enrichment

### Phase 3.4: OpenTelemetry Tracing & Metrics [status: pending]
**Goal:** First-class OTel wiring (exporter guidance, resource attributes, sample dashboards) on top of the existing RagTelemetry ActivitySource/Meter.
**Backlog items:** OpenTelemetry Tracing & Metrics

### Phase 3.5: Sample Applications [status: pending]
**Goal:** End-to-end runnable samples covering the main library scenarios.
**Backlog items:** Sample Applications

### Phase 3.6: Rag.NET CLI Tool [status: pending]
**Goal:** `dotnet tool` for ingest/query/evaluate against a configured pipeline.
**Backlog items:** Rag.NET CLI Tool
