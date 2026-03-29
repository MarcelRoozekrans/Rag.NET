# Connector Quality Sweep — Design

**Date:** 2026-03-29
**Scope:** All 7 new connectors (Confluence, Jira, Notion, Asana, Slack, Microsoft Teams, Gmail)
**Branch:** `feature/connector-quality-sweep`

## Problem

The 7 new SaaS connectors are functionally complete with 32 passing tests, but an audit revealed gaps across three areas:

1. **Documentation** — `data-providers.md` connector table stops at GitHub; no DI examples, auth guidance, or XML doc comments for any new connector; README, index, and samples don't reference them
2. **Tests** — only happy-path coverage; missing error handling, null field rendering, content output verification, pagination edge cases, cancellation, and connector-specific scenarios
3. **Benchmarks** — zero benchmarks for any new connector despite mature infrastructure (12 existing benchmark classes)

## Design

### Workstream A: Documentation

**data-providers.md updates:**
- Extend connector reference table (line 74) with all 7 connectors: package, SDK, auth type, delta mechanism
- Add DI registration example per connector following existing pattern
- Document auth requirements per platform (token type, where to obtain, required scopes)
- Document delta token semantics (what to persist, format, behavior)

**XML doc comments (21 source files = 7 connectors x 3 files):**
- `*DataProvider.cs` — class-level summary: full/delta behavior, content format, limitations
- `*Options.cs` — property-level docs for every property
- `*DataProviderExtensions.cs` — method-level docs with parameter descriptions

**Broader docs:**
- `README.md` — add data providers section listing all connectors
- `docs/index.md` — update package diagram with 7 new packages
- `samples/Rag.NET.Sample` — commented-out example wiring Confluence + Slack

### Workstream B: Comprehensive Tests

**Test categories per connector (~12-15 new tests each, ~85-100 total):**

| Category | Count | Description |
|---|---|---|
| Input validation | 2-3 | Invalid DeltaToken/key formats, regex rejection |
| Error handling | 2-3 | HTTP 400/5xx, stale delta fallback, ok:false |
| Null/optional fields | 2-3 | All nullable properties absent, partial combos |
| Content rendering | 2-3 | Markdown output verification, all block/field types |
| Pagination edge cases | 2 | Empty page, 3+ pages, cursor exhaustion |
| Cancellation | 1 | CancellationToken mid-enumeration |
| Connector-specific | 1-2 | See below |

**Connector-specific scenarios:**

- **Confluence:** CQL injection prevention, cursor extraction with `&`, HTML entity stripping
- **Jira:** JQL construction with/without ProjectKey, comments with null author
- **Notion:** All block types (heading_1-3, bulleted/numbered_list, code, quote), title fallback to page ID
- **Asana:** ProjectGid vs WorkspaceGid routing, subtask rendering, token-per-call refresh
- **Slack:** Channel pinning, multi-day grouping, user cache hit/miss, unknown user fallback
- **Teams:** TeamId/ChannelId pinning combos, HTML stripping, null CreatedDateTime fallback
- **Gmail:** Subject sanitization (invalid filename chars), TextBody vs HtmlBody, delta UID range, null subject fallback

**Test patterns:**
- Same `FakeHandler` / `FakeSequentialHandler` as existing tests
- NSubstitute for Gmail (MailKit mocks)
- Verify actual markdown content via `OpenContentAsync()` stream reads
- `OperationCanceledException` assertion for cancellation

### Workstream C: Benchmarks

**Shared baseline — `ConnectorIngestionBenchmarks.cs`:**
- All 7 connectors, 3 scenarios: baseline (full traversal, no store), warm cache (ETags match), cold store (read + hash + ingest)
- Mocked HTTP with canned JSON (50 items)
- `[MemoryDiagnoser]` for allocation tracking

**Per-connector benchmarks (7 files):**

| Connector | Specific scenarios |
|---|---|
| Confluence | Full vs delta, HTML stripping overhead |
| Jira | Full vs delta, JQL construction cost |
| Notion | Search + block fetching (2 API calls per page) |
| Asana | Task + subtask fetching, token refresh cost |
| Slack | Day-batching grouping, thread reply expansion |
| Teams | Day-batching grouping, HTML stripping |
| Gmail | IMAP fetch + body selection overhead |

**Documentation:** Extend `benchmarks.md` with "Data Connectors" section.

## Task Breakdown

### Group 1 — Documentation (parallel)
1. Update `data-providers.md` — table, DI examples, auth, delta docs
2. XML doc comments for 21 source files
3. Update `README.md`
4. Update `docs/index.md`
5. Update `samples/Rag.NET.Sample`

### Group 2 — Tests (parallel, one task per connector)
6. Confluence — ~12-15 new tests
7. Jira — ~12-15 new tests
8. Notion — ~12-15 new tests (heavy on block types)
9. Asana — ~12-15 new tests
10. Slack — ~12-15 new tests
11. Microsoft Teams — ~12-15 new tests
12. Gmail — ~12-15 new tests

### Group 3 — Benchmarks (after Group 2)
13. `ConnectorIngestionBenchmarks.cs` — shared baseline
14. Per-connector benchmark classes (7 files)
15. Update `benchmarks.md`

### Group 4 — Finalization
16. Build + test full solution
17. Update `features.md` if needed

Groups 1 and 2 run in parallel. Group 3 after Group 2. Group 4 is the final gate.
