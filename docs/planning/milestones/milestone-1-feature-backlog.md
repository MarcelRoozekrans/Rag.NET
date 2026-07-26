# Milestone 1: Feature Backlog

**Status:** complete
**Started:** 2026-07-24
**Completed:** 2026-07-26

## Goal

Work the remaining feature backlog from `docs/reference/features.md` to completion:
chunking strategies (sliding window, proposition extraction, late chunking), retrieval
techniques (HyDE v2, FLARE, SPLADE, multi-index federation), ingestion operations
(batch optimiser, webhooks, embedding versioning), resilience and cost controls
(fallback chain, rate limiting), document parsers (EPUB, EML/MSG, PDF tables, OCR),
connectors (Outlook/Exchange, Linear), and vector stores (Weaviate, Chroma, Pinecone).
Each feature ships with tests and a features.md entry ticked.

## Definition of Done

- [x] All planned phases complete
- [x] Every feature row covered by this milestone ticked in features.md with tests and docs
- [x] All tests passing

## Phases

1. Phase 1.1 — Chunking Strategies [complete — 2026-07-24]
2. Phase 1.2 — Retrieval Techniques [complete — 2026-07-24]
3. Phase 1.3 — Ingestion Operations [complete — 2026-07-24]
4. Phase 1.4 — Resilience & Cost Controls [complete — 2026-07-25]
5. Phase 1.5 — Document Parsers [complete — 2026-07-25]
6. Phase 1.6 — Connectors [complete — 2026-07-25]
7. Phase 1.7 — Vector Stores [complete — 2026-07-26]

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
| 2026-07-26 | **Pass** — all 7 phases complete; the 21 backlog rows this milestone covers are ticked with tests and docs (the 10 rows still unticked in features.md all belong to Milestones 2 and 3). Solution builds 0 warnings / 0 errors; Rag.NET.Tests 1202/1202 plus every vector-store and integration suite green. | Deferred within delivered features, each documented where a user would meet it: PgVector sparse storage (SPLADE runs on Qdrant, Pinecone, in-memory); Service Bus ingestion trigger and the CLI reindex command; OCR limited to Tesseract behind the `EnableOcr` gate (Azure Document Intelligence deferred); Pinecone's same-record sparse write path unverified against live serverless. Cross-phase engineering debts are tracked in ROADMAP.md. |
