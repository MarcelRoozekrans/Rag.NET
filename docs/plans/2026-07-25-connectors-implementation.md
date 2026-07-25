# Connectors Implementation Plan (Phase 1.6)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the two backlog connector rows: an Exchange email connector emitting raw rfc822 via Microsoft Graph, and the repo's first GraphQL connector for Linear.

**Architecture:** Per `docs/plans/2026-07-25-connectors-design.md`. Part A mirrors the MicrosoftTeams Graph connector (GraphServiceClient + ClientSecretCredential + OdataNextLink paging + FakeGraphHandler tests) with the rfc822 `$value` twist; Part B mirrors the Slack ZeroAlloc.Rest connector with Notion's POST-with-`[Body]` shape for the GraphQL endpoint and the Zendesk watermark pattern.

**Tech Stack:** .NET 10, xUnit v3, Microsoft.Graph 5.* + Azure.Identity (Exchange), ZeroAlloc.Rest 1.1.3 (Linear), WireMock (integration).

**Conventions:** as previous phases — MA0051/MA0015/ZA0601/ZA0501/EPS05/HLQ warnings-as-errors, LoggerMessage, commit trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`, filtered tests during work, one `dotnet build Rag.NET.slnx` per part, TDD throughout.

---

## Part A — Exchange email connector

### Task A1: project + provider + options

**Files:**
- Create: `src/Rag.NET.DataProviders.Exchange/Rag.NET.DataProviders.Exchange.csproj` — copy `src/Rag.NET.DataProviders.MicrosoftTeams/Rag.NET.DataProviders.MicrosoftTeams.csproj` VERBATIM (Graph 5.*, Kiota 1.22.0 CVE pin with its comment, Azure.Identity 1.*, Http.Resilience 10.*), adjust ids/InternalsVisibleTo. Add to `Rag.NET.slnx` (+ the test project from A2).
- Create: `src/Rag.NET.DataProviders.Exchange/ExchangeMailOptions.cs` — `: CloudStorageOptions` (read it + MicrosoftTeamsOptions first): `Mailbox` (string, required), `FolderIds` (IReadOnlyList<string>?, null → Inbox), `MaxResults = 500`.
- Create: `src/Rag.NET.DataProviders.Exchange/ExchangeMailDataProvider.cs` — read `src/Rag.NET.DataProviders.MicrosoftTeams/MicrosoftTeamsDataProvider.cs` FIRST and mirror its shape (`FileContentProviderBase`, `GetFileHandlesAsync` yielding `Result<FileHandle, RagError>`, OdataNextLink paging loop, error mapping). Specifics per design §1:
  - Folders: `options.FolderIds ?? ["inbox"]` (Graph well-known folder name — verify "inbox" is a valid well-known id in the SDK).
  - List request: `/users/{Mailbox}/mailFolders/{folder}/messages` with `$select=id,subject,receivedDateTime,lastModifiedDateTime,hasAttachments`, `$orderby=receivedDateTime asc`, `$filter=receivedDateTime ge {DeltaToken}` when `options.DeltaToken` parses as DateTimeOffset (invalid token → Result failure naming it), `$top` page size; stop at `MaxResults` total.
  - Handle: `Id = "{folder}/{message.Id}"`, `FileName = SanitizeFileName(subject) + ".eml"` (fallback `message-{id}.eml`; reuse/mirror whatever filename sanitization helper the sibling connectors use — grep Sanitize in DataProviders), `ETag = lastModifiedDateTime?.ToString("o")`, Metadata: `folder`, `received_at`, `has_attachments`.
  - Content: lazy — the handle's open delegate fetches `/users/{Mailbox}/messages/{id}/$value` via `graph.Users[mailbox].Messages[id].Content.GetAsync(...)` (VERIFY the Graph 5.x SDK path for `$value` — inspect the generated fluent API; it may be `.Content` — do not guess; if the SDK lacks it, fall back to a raw `RequestAdapter` call and document).
  - Watermark: track max receivedDateTime; `public string? GetDeltaToken()` (Zendesk pattern — read `ZendeskTicketsDataProvider.GetDeltaToken`).

### Task A2: unit tests + DI

**Files:**
- Create: `src/Rag.NET.DataProviders.Exchange/ExchangeMailDataProviderExtensions.cs` — mirror `MicrosoftTeamsDataProviderExtensions.cs` (AddDataProviderHttpClient("Exchange"), ClientSecretCredential, GraphServiceClient, singleton IFileContentProvider); validate `Mailbox` non-empty (ArgumentException at registration).
- Create: `tests/Rag.NET.DataProviders.Exchange.Tests/` (copy the MicrosoftTeams.Tests csproj shape) — `ExchangeMailDataProviderTests.cs` with a `FakeGraphHandler` (read the Teams one; substring-keyed canned JSON):

```csharp
// 1. Enumerate_InboxDefault_EmitsEmlHandles (ids, .eml filenames, ETag mapping, metadata).
// 2. Enumerate_MultipleFolders_AllListed.
// 3. Enumerate_Paging_FollowsNextLink (2 pages).
// 4. Enumerate_DeltaToken_AppendsReceivedDateTimeFilter (assert the request URL contains the ge filter).
// 5. Enumerate_InvalidDeltaToken_YieldsFailure.
// 6. Content_IsLazy ($value endpoint NOT hit during enumeration; hit exactly once on OpenContentAsync — assert via the handler's request log; content bytes round-trip).
// 7. Enumerate_MaxResults_Caps.
// 8. GraphError_MapsToResultFailure (500 on the list endpoint).
// 9. Watermark_AdvancesToMaxReceived (GetDeltaToken after enumeration).
// 10. Filename_Sanitized (subject with invalid chars) + fallback for empty subject.
// DI: registration test (resolves IFileContentProvider as ExchangeMailDataProvider; empty Mailbox throws).
```

**Commits:** `feat(data-providers): Exchange mail connector via Microsoft Graph` (A1+A2 split as two commits: provider, then tests+DI — or one per task, implementer's judgment within convention).

### Task A3: integration test + docs

- WireMock integration test in `tests/Rag.NET.DataProviders.IntegrationTests/ExchangeMailDataProviderTests.cs` — read the MicrosoftTeams integration test + `WireMockServerFixture.LoadCassettes` convention; cassettes for list + `$value` flows; assert end-to-end `IngestFromProviderAsync` ingests an .eml through a registered `AddEmailParser()` (proving the Phase 1.5 interplay — the whole point of the rfc822 decision; include one message with a text attachment exercising the dispatcher).
- Docs: data-providers guide section (setup incl. app registration scopes — `Mail.Read` application permission; the `.eml`→AddEmailParser requirement; watermark persistence example; Graph delta-query deferral note). features.md: tick the Email Connector row + Status.

**Commit** `feat(data-providers): Exchange connector integration test + docs; tick feature`

---

## Part B — Linear issue tracker

### Task B1: project + GraphQL client + provider

**Files:**
- Create: `src/Rag.NET.DataProviders.Linear/Rag.NET.DataProviders.Linear.csproj` — copy the Slack csproj (ZeroAlloc.Rest + SystemTextJson 1.1.3, Http.Resilience). Slnx entries (+ test project).
- Create: `src/Rag.NET.DataProviders.Linear/ILinearApi.cs` — read `src/Rag.NET.DataProviders.Notion/INotionApi.cs` FIRST (the POST-with-[Body] template): single `[Post("/graphql")]` method, `LinearGraphQlRequest { string Query, LinearIssueVariables Variables }` (typed variables record — first/after/filter fields; avoid JsonElement if the serializer handles records cleanly — verify against ZeroAlloc.Rest.SystemTextJson), response DTOs for `data.issues.nodes[]` (identifier/title/description/updatedAt/url/state{name,type}/project{name}/assignee{name}/team{key}/comments.nodes[]{body,createdAt,user{name}}) + `pageInfo { hasNextPage, endCursor }` + top-level `errors[] { message }`.
- Create: `src/Rag.NET.DataProviders.Linear/LinearOptions.cs` — `: CloudStorageOptions`: `TeamKeys`, `States` (validated values: backlog/unstarted/started/completed/canceled — NOTE Linear spells it "canceled"; VERIFY the state-type enum values against Linear's public schema docs and pin what you find), `PageSize = 50`.
- Create: `src/Rag.NET.DataProviders.Linear/LinearDataProvider.cs` — `IFileContentProvider` (read `SlackDataProvider` for the Result-streaming + watermark conventions): build the issues query string (const, with `$filter/$after/$first` variables), cursor loop until `hasNextPage` false; GraphQL `errors` non-empty → Result failure naming the messages; per-issue Markdown FileEntry per design §2 (identifier/title heading, state|project|assignee metadata line, description, `## Comments` with author + createdAt); `Id = identifier`, `ETag = updatedAt("o")`, Metadata: team/state/project/url; watermark = max updatedAt → `GetDeltaToken()`.

### Task B2: unit tests + DI

- Create: `src/Rag.NET.DataProviders.Linear/LinearDataProviderExtensions.cs` — mirror Slack's: `AddLinearDataProvider(string apiKey, Action<LinearOptions>? configure = null, string? baseUrl = null)`; `AddILinearApi` generated registration, BaseAddress `https://api.linear.app` (or baseUrl), `Authorization: {apiKey}` header — VERIFY Linear's auth header format (bare key vs Bearer) against their docs and pin it in a comment; resilience handler; validate apiKey non-empty + state values.
- Create: `tests/Rag.NET.DataProviders.Linear.Tests/LinearDataProviderTests.cs` — FakeLinearApi fakes (Slack pattern):

```csharp
// 1. Enumerate_SinglePage_MarkdownRendering (exact content: heading, metadata line, description, comments section).
// 2. Enumerate_CursorPagination (two pages; captured after-cursors).
// 3. Filters_TeamAndState_InVariables (captured request variables).
// 4. DeltaToken_AppliedAsUpdatedAtFilter + Watermark_Advances.
// 5. GraphQlErrors_YieldFailure (errors array → failure naming message).
// 6. HttpFailure_Propagates (HttpError → RagError.HttpFailed).
// 7. InvalidStateValue_ThrowsAtRegistration (DI validation).
// 8. DI registration resolves provider; empty apiKey throws.
// 9. Issue with no comments/description → sections omitted gracefully.
```

**Commits:** `feat(data-providers): Linear issue tracker connector (first GraphQL connector)` + tests/DI per convention.

### Task B3: integration test + docs + close-out

- WireMock integration test (cassettes for the GraphQL POST: assert the request body contains the issues query + variables; scenario-state cursor pagination — read the Zendesk integration test for the stateful pattern).
- Docs: data-providers guide Linear section (API key setup, team/state filtering, watermark, first-GraphQL note). features.md: tick the Linear row + Status.
- `docs/planning/ROADMAP.md` + `MILESTONE.md`: Phase 1.6 complete (2026-07-25).

**Commit** `feat(data-providers): Linear connector integration test + docs; tick feature; complete phase 1.6`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. New test projects + `tests/Rag.NET.DataProviders.IntegrationTests` + full `tests/Rag.NET.Tests` green.
3. features.md: both rows ticked. Final whole-phase review; merge decision.
