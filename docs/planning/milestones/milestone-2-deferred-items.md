# Milestone 2: Deferred Items & Technical Debt

**Status:** complete
**Started:** 2026-07-26
**Completed:** 2026-07-27

## Goal

Follow through on everything Milestone 1 delivered *around* rather than *through*: the
features that were scoped out during brainstorming, and the engineering debt that review
cycles surfaced but that did not belong on an unrelated phase's branch. When this milestone
closes, no delivered feature row should carry an unstated caveat.

Completed milestones are archived under `docs/planning/milestones/`.

## Definition of Done

- [x] All planned phases complete
- [x] Every deferral recorded in the Milestone 1 audit either delivered or re-recorded with a
      current reason (not simply carried forward)
- [x] The follow-up debt list in ROADMAP.md is empty or explicitly re-justified
- [x] All tests passing; solution builds 0 warnings / 0 errors

## Scope

Carried from the Milestone 1 audit and the ROADMAP follow-up debt list:

| Item | Origin | Phase |
|---|---|---|
| ~~Shared filename sanitizer (Exchange + Linear verbatim copies, Gmail divergent)~~ | Phase 1.6 review | **done in 2.1** |
| ~~Graph transport-exception mapping (`HttpRequestException` bypasses the Result channel)~~ | Phase 1.6 review | **done in 2.1** |
| ~~Embedded-message recursion (EML `MessagePart` / MSG nested `Storage.Message`)~~ | Phase 1.5 review | **done in 2.1** |
| ~~PDF table dominance-guard refinement (rescue full-page Key/Value tables)~~ | Phase 1.5 review | **done in 2.1** |
| ~~Persistent-memory score normalization vs federated RRF scores~~ | Phase 1.2 review | **done in 2.1** |
| ~~`ConfigureResilience` registers a `"rag-net"` pipeline nothing consumes~~ | Pre-existing | **done in 2.1** |
| ~~`FileHandle.Metadata` populated by only 2 of 21 connectors~~ | Phase 1.6 review | **done in 2.2** |
| ~~PgVector sparse storage (SPLADE)~~ | Phase 1.2 scope decision | **done in 2.3** |
| ~~Azure Document Intelligence OCR~~ | Phase 1.5 scope decision | **done in 2.4** |
| ~~Service Bus ingestion trigger~~ | Phase 1.3 scope decision | **done in 2.5** |

## Explicitly not in scope

- **CLI reindex command** — depends on the `Rag.NET CLI Tool` that does not exist yet; it
  belongs with the CLI in the release milestone, not here.
- **Pinecone live sparse-write verification** — requires a live Pinecone account; the local
  emulator rejects sparse writes. Decision (2026-07-26): leave it documented as a labelled
  coverage gap rather than redesign around the emulator.

## Phases

1. Phase 2.1 — Engineering Debt Sweep [complete — 2026-07-26]
2. Phase 2.2 — Connector Metadata Consistency [complete — 2026-07-27]
3. Phase 2.3 — PgVector Sparse Storage [complete — 2026-07-27]
4. Phase 2.4 — Azure Document Intelligence OCR [complete — 2026-07-27]
5. Phase 2.5 — Service Bus Ingestion Trigger [complete — 2026-07-27]

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
| 2026-07-27 | **Pass** — all 5 phases complete. Every item in the scope table above is delivered; both explicitly-out-of-scope items keep a current, checked reason. Solution builds 0 warnings / 0 errors; `Rag.NET.Tests` 1311, `Rag.NET.Api.Tests` 63, `Rag.NET.DataProviders.Tests` 69, `Rag.NET.Ingestion.AzureServiceBus.Tests` 79 — all green, 0 skipped. | The ROADMAP follow-up debt list is **not** empty, but no entry is unscheduled: the two survivors (retiring `EmbeddedMessageMetadata.Sanitize`, and converting the stack-recursive email traversal to a work queue) are both behaviour changes rather than refactors, and are now scheduled into **Phase 3.6** with re-justified reasons — the sanitizer entry's original blocker was removed by 2.5 and its old text had become false. Carried out of scope with current reasons: the CLI reindex command (depends on the CLI tool, Phase 4.6) and Pinecone live sparse-write verification (needs a paid account; labelled coverage gap). Newly recorded during 2.5 and documented where a user meets them: re-ingest is a clean replace for BM25 and the data manager but only a partial replace for vectors and parent chunks (stale tail chunks survive a shorter re-ingest), and that replace is a single-writer guarantee — no per-`DocumentId` lock exists, so concurrent same-document ingests can still interleave unless Service Bus sessions are enabled. |
