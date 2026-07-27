# Milestone 2: Deferred Items & Technical Debt

**Status:** active
**Started:** 2026-07-26

## Goal

Follow through on everything Milestone 1 delivered *around* rather than *through*: the
features that were scoped out during brainstorming, and the engineering debt that review
cycles surfaced but that did not belong on an unrelated phase's branch. When this milestone
closes, no delivered feature row should carry an unstated caveat.

Completed milestones are archived under `docs/planning/milestones/`.

## Definition of Done

- [ ] All planned phases complete
- [ ] Every deferral recorded in the Milestone 1 audit either delivered or re-recorded with a
      current reason (not simply carried forward)
- [ ] The follow-up debt list in ROADMAP.md is empty or explicitly re-justified
- [ ] All tests passing; solution builds 0 warnings / 0 errors

## Scope

Carried from the Milestone 1 audit and the ROADMAP follow-up debt list:

| Item | Origin | Phase |
|---|---|---|
| Shared filename sanitizer (Exchange + Linear verbatim copies, Gmail divergent) | Phase 1.6 review | 2.1 |
| Graph transport-exception mapping (`HttpRequestException` bypasses the Result channel) | Phase 1.6 review | 2.1 |
| Embedded-message recursion (EML `MessagePart` / MSG nested `Storage.Message`) | Phase 1.5 review | 2.1 |
| PDF table dominance-guard refinement (rescue full-page Key/Value tables) | Phase 1.5 review | 2.1 |
| Persistent-memory score normalization vs federated RRF scores | Phase 1.2 review | 2.1 |
| `ConfigureResilience` registers a `"rag-net"` pipeline nothing consumes | Pre-existing | 2.1 |
| `FileHandle.Metadata` populated by only 2 of 21 connectors | Phase 1.6 review | 2.2 |
| ~~PgVector sparse storage (SPLADE)~~ | Phase 1.2 scope decision | **done in 2.3** |
| ~~Azure Document Intelligence OCR~~ | Phase 1.5 scope decision | **done in 2.4** |
| Service Bus ingestion trigger | Phase 1.3 scope decision | 2.5 |

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
5. Phase 2.5 — Service Bus Ingestion Trigger [pending]

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
