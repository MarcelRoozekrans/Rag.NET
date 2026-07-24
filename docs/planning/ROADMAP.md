# Project Roadmap

Backlog source: the unchecked items in `docs/reference/features.md` (31 items as of 2026-07-24).
Every backlog item is assigned to exactly one phase below. When a phase completes, tick the
corresponding rows in features.md.

## Milestone 1: Feature Backlog [status: active]
**Goal:** Work the remaining feature backlog to completion — chunking, retrieval techniques, ingestion ops, resilience, parsers, connectors, and vector stores.
**Started:** 2026-07-24
**Definition of Done:**
- [ ] All planned phases complete
- [ ] Every feature row it covers ticked in features.md with tests and docs
- [ ] All tests passing

### Phase 1.1: Chunking Strategies [status: pending]
**Backlog items:** Sliding Window Chunking with Overlap; Proposition Extraction Chunking; Late Chunking

### Phase 1.2: Retrieval Techniques [status: pending]
**Backlog items:** Hypothetical Document Embeddings v2; FLARE; Sparse Embedding Retrieval (SPLADE); Multi-Index Federation

### Phase 1.3: Ingestion Operations [status: pending]
**Backlog items:** Batch Ingestion Optimiser; Webhook / Event-Driven Ingestion; Embedding Versioning & Re-indexing

### Phase 1.4: Resilience & Cost Controls [status: pending]
**Backlog items:** LLM Fallback Chain; Rate Limiting & Cost Budgeting

### Phase 1.5: Document Parsers [status: pending]
**Backlog items:** EPUB Parser; Email File Parser (EML/MSG); PDF Table Extraction; OCR for Scanned PDFs

### Phase 1.6: Connectors [status: pending]
**Backlog items:** Email Connector (Outlook/Exchange); Linear Issue Tracker

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
