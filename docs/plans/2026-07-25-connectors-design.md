# Connectors — Design (Phase 1.6)

**Date:** 2026-07-25
**Milestone:** 1 — Feature Backlog, Phase 1.6
**Covers features.md rows:** Email Connector (Outlook/Exchange); Linear Issue Tracker

## Scope decisions (agreed)

1. **Exchange emits raw `message/rfc822`** (`.eml` FileEntries via Graph's
   `/users/{upn}/messages/{id}/$value`), NOT pre-rendered Markdown (the Gmail sibling's
   approach) — this is the only path that satisfies the spec's "attachment parsing delegates
   to existing parsers": Phase 1.5's `EmailDocumentParser` + `EmailAttachmentDispatcher` do
   exactly that on rfc822 input.
2. **App-only auth** (`ClientSecretCredential` + configured mailbox UPN), matching the
   uniform Teams/OneDrive/SharePoint convention; delegated `/me` flows out of scope.
3. **Linear is the repo's first GraphQL connector**, built on the existing ZeroAlloc.Rest
   POST-with-`[Body]` pattern (Notion precedent) — no new GraphQL client dependency.
4. Both connectors follow the Zendesk watermark pattern: opaque `DeltaToken` on the options +
   `GetDeltaToken()` for the caller to persist; hash-store ETag fast-skip on top.

## 1. Exchange Email Connector

**Package:** `src/Rag.NET.DataProviders.Exchange` (new; mirror the MicrosoftTeams csproj:
`Microsoft.Graph 5.*`, `Microsoft.Kiota.Abstractions 1.22.0` CVE pin, `Azure.Identity 1.*`,
`Microsoft.Extensions.Http.Resilience 10.*`, InternalsVisibleTo tests)

- `ExchangeMailDataProvider : FileContentProviderBase` — injected `GraphServiceClient` +
  `ExchangeMailOptions`.
- **Enumeration:** for each configured folder (null → Inbox):
  `/users/{Mailbox}/mailFolders/{folder}/messages` selecting
  `id, subject, receivedDateTime, lastModifiedDateTime, hasAttachments`, filtered by
  `receivedDateTime ge {DeltaToken}` when set, ordered ascending, `OdataNextLink` paging,
  capped at `MaxResults`.
- **FileHandle mapping:** `Id = {folderId}/{messageId}`, `FileName = "{subject}.eml"`
  (sanitized, fallback "message-{id}.eml"), `ETag = lastModifiedDateTime` ("o" format),
  Metadata: folder, receivedDateTime, hasAttachments. Content: lazy fetch of
  `/users/{Mailbox}/messages/{id}/$value` (raw MIME stream) only when ingested.
- **Watermark:** track max `receivedDateTime` seen; `GetDeltaToken()` returns it (ISO "o").
  Same-timestamp duplicates on the next run are caught by the hash-store skip.
- `ExchangeMailOptions : CloudStorageOptions`: `Mailbox` (required UPN),
  `FolderIds` (IReadOnlyList<string>?, null = Inbox), `MaxResults = 500`.
- DI: `AddExchangeMailDataProvider(tenantId, clientId, clientSecret, Action<ExchangeMailOptions> configure)`
  — `AddDataProviderHttpClient("Exchange")`, `ClientSecretCredential`, `GraphServiceClient`,
  singleton `IFileContentProvider` (Teams shape verbatim). Validate Mailbox non-empty.
- Docs note: ingesting `.eml` entries requires `AddEmailParser()` (and optionally
  `AddPdfParser()` etc. for attachments) — the connector emits rfc822 by design.

## 2. Linear Issue Tracker

**Package:** `src/Rag.NET.DataProviders.Linear` (new; ZeroAlloc.Rest 1.1.3 +
SystemTextJson + Http.Resilience — Slack csproj shape)

- `ILinearApi` (internal, `[ZeroAllocRestClient]`): single
  `[Post("/graphql")] Task<Result<LinearGraphQlResponse, HttpError>> QueryAsync([Body] LinearGraphQlRequest request, CancellationToken ct)`
  where `LinearGraphQlRequest = { string Query, JsonElement/record Variables }` and the
  response wraps `data`/`errors` (GraphQL-level errors surface as `RagError.HttpFailed`-style
  failures with the error messages — exact modeling in planning).
- **Query:** paginated `issues(first: N, after: $cursor, filter: $filter, orderBy: updatedAt)`
  selecting `identifier, title, description, updatedAt, url, state { name type },
  project { name }, assignee { name }, team { key }, comments { nodes { body createdAt user { name } } }`.
  Filter built from options: `team: { key: { in: $TeamKeys } }`, `state: { type: { in: $States } }`,
  `updatedAt: { gt: $DeltaToken }`.
- **FileEntry mapping:** one Markdown entry per issue — `Id = issue.identifier`,
  `FileName = "{identifier} {title}.md"` (sanitized), `ETag = updatedAt` ("o"),
  Metadata: team, state, project, url. Content: `# {identifier}: {title}`, metadata line
  (state/project/assignee), description, `## Comments` with author/date-attributed bodies.
  Content built eagerly per page but wrapped lazily per FileEntry convention.
- **Watermark:** max `updatedAt` seen → `GetDeltaToken()`.
- `LinearOptions : CloudStorageOptions`: `TeamKeys` (IReadOnlyList<string>?, null = all),
  `States` (IReadOnlyList<string>?, null = all; values validated against
  active/completed/cancelled/backlog/unstarted/started — Linear state *types*), `PageSize = 50`.
- DI: `AddLinearDataProvider(apiKey, Action<LinearOptions>? configure, string? baseUrl = null)`
  — `AddILinearApi` (generated), `Authorization: {apiKey}` header (Linear uses the bare key,
  no Bearer prefix — verify against the API docs in planning), resilience handler, singleton
  provider. `baseUrl` for WireMock tests (Slack convention).

## Error handling summary

House posture: per-entry failures surface as `Result` failures into
`ProviderIngestionResult.Errors` (driver handles); Graph/HTTP errors map to `RagError`
(existing connector conventions); GraphQL `errors` arrays become failures naming the messages;
cancellation propagates. No connector-level retries beyond the standard resilience handler.

## Testing

- Exchange: `FakeGraphHandler` HTTP-layer stubs (Teams pattern) — folder enumeration incl.
  multiple folders, `OdataNextLink` paging, DeltaToken filter presence in the request URL,
  `$value` fetched lazily (not during enumeration — assert request log), ETag/metadata
  mapping, sanitized filenames, Graph error → Result failure, MaxResults cap, watermark
  advance. DI registration test.
- Linear: `FakeLinearApi` fakes (Slack pattern) — cursor pagination, team/state filter
  variables, watermark filter + advance, Markdown rendering (exact content incl. comments),
  GraphQL-errors → failure, HTTP failure propagation, state-type validation. DI test.
- WireMock integration tests for both in `tests/Rag.NET.DataProviders.IntegrationTests`
  (cassettes; Linear asserts the GraphQL POST body shape; Exchange asserts auth-free canned
  Graph flows per the existing Teams integration precedent).
- features.md: both rows ticked; ROADMAP/MILESTONE Phase 1.6 complete.

## Out of scope

- Delegated (`/me`) Graph auth; Graph change-notifications/webhooks (the Phase 1.3 webhook
  endpoint is the generic path).
- Linear attachments/documents; Linear webhook triggers.
- Graph delta queries (`/delta`) — date-range watermark suffices for v1 (documented).
