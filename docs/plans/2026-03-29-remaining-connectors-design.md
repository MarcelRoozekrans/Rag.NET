# Remaining Connectors — Design

**Date:** 2026-03-29
**Scope:** GitLab, Bitbucket, Zendesk (tickets + articles), Airtable (rows + attachments)
**Branch:** `feature/remaining-connectors`

## Problem

4 connectors remain on the feature roadmap: GitLab, Bitbucket, Zendesk, and Airtable. All follow established patterns from the 13 existing connectors.

## Package & SDK Selection

| Connector | Package | SDK | Auth |
|---|---|---|---|
| GitLab | `Rag.NET.DataProviders.GitLab` | NGitLab (v11.4+) | PAT via `PRIVATE-TOKEN` header |
| Bitbucket | `Rag.NET.DataProviders.Bitbucket` | Refit (no good SDK) | App Password via Basic Auth |
| Zendesk | `Rag.NET.DataProviders.Zendesk` | ZendeskApi.Client (JustEat, v7.0+) + Refit for incremental/Help Center | API Token via Basic Auth (`email/token:key`) |
| Airtable | `Rag.NET.DataProviders.Airtable` | Airtable.NET (v1.8+) | PAT via Bearer token |

## Connector Designs

### GitLab

Mirrors the GitHub connector. Enumerates repository files via tree/blob API with commit SHA-based delta.

- **Full traversal:** `GET /projects/:id/repository/tree?recursive=true` → filter blobs → `GET /projects/:id/repository/files/:path/raw?ref=main`
- **Delta:** `GET /projects/:id/repository/compare?from={deltaToken}&to=HEAD` → changed files → fetch raw. Removed files filtered out.
- **DeltaToken:** commit SHA
- **ETag:** blob SHA from tree listing
- **Options:** `GitLabOptions` extends `CloudStorageOptions` with `BaseUrl`, `ProjectIdOrPath` (numeric ID or `namespace/project`), `Ref` (default `"main"`)
- **DI:** `AddGitLabDataProvider(baseUrl, projectIdOrPath, token, configure?)`
- **NGitLab usage:** `IGitLabClient` → `client.GetRepository(projectId)` → `.Tree`, `.GetRawBlob()`, `.Compare()`

### Bitbucket

Same tree/blob approach as GitHub/GitLab, via Refit.

- **Full traversal:** `GET /2.0/repositories/{workspace}/{repo}/src/{commit}/` recursive pagination → filter files → same endpoint for raw content
- **Delta:** `GET /2.0/repositories/{workspace}/{repo}/diffstat/{oldCommit}..{newCommit}` → paginated changed files → fetch non-removed
- **DeltaToken:** commit hash
- **ETag:** file hash from source listing
- **Pagination:** follow `next` URL in JSON response until absent
- **Options:** `BitbucketOptions` extends `CloudStorageOptions` with `Workspace`, `RepoSlug`, `Ref` (default `"main"`)
- **DI:** `AddBitbucketDataProvider(workspace, repoSlug, username, appPassword, configure?)`
- **Refit interface:** `IBitbucketApi` with `GetSourceAsync`, `GetDiffstatAsync`, `GetLatestCommitAsync`

### Zendesk

Single package, two providers: `ZendeskTicketsDataProvider` and `ZendeskArticlesDataProvider`.

**Tickets:**
- **Full:** `GET /api/v2/incremental/tickets/cursor.json?start_time=0` → for each ticket, `GET /api/v2/tickets/{id}/comments`
- **Delta:** same endpoint with `start_time={deltaToken}`
- **DeltaToken:** `end_time` from response (Unix epoch)
- **ETag:** `updated_at` timestamp
- **Content:** markdown with subject as title, status, priority, requester, comments section
- **SDK:** ZendeskApi.Client for ticket listing; Refit for incremental cursor + Help Center (not covered by SDK)

**Articles:**
- **Full:** `GET /api/v2/help_center/incremental/articles.json?start_time=0`
- **Delta:** same with `start_time={deltaToken}`
- **DeltaToken:** `end_time` from response
- **Content:** HTML body stripped to markdown via `HtmlTagRegex`

**DI:**
- `AddZendeskTicketsDataProvider(subdomain, email, apiToken, configure?)`
- `AddZendeskArticlesDataProvider(subdomain, email, apiToken, configure?)`
- **Options:** `ZendeskTicketsOptions` and `ZendeskArticlesOptions` extending `CloudStorageOptions` with `Subdomain`, `Email`
- **Refit interface:** `IZendeskApi` with `GetIncrementalTicketsAsync`, `GetTicketCommentsAsync`, `GetIncrementalArticlesAsync`

### Airtable

Single provider enumerating rows as markdown and extractments as file handles.

- **Full:** List records via Airtable.NET SDK → per record: markdown FileHandle + one FileHandle per attachment
- **Delta:** `filterByFormula=LAST_MODIFIED_TIME()>'{deltaToken}'` — requires "Last modified time" field. DeltaToken = ISO 8601 timestamp.
- **ETag:** record ID + hash of field values

**Row markdown format:**
- First field as `# Title`
- Short fields in a `| Field | Value |` table
- Long text fields as separate `## FieldName` sections
- Checkbox, date, number, select all stringified

**Attachments:**
- URLs are temporary (expire after hours) — download during enumeration
- FileHandle.Id = `{recordId}/{fieldName}/{attachmentId}`
- `OpenContentAsync` downloads from signed URL at call time

**Options:** `AirtableOptions` extends `CloudStorageOptions` with `BaseId`, `TableName`, `View` (optional), `LastModifiedFieldName` (optional, enables delta)
**DI:** `AddAirtableDataProvider(baseId, tableName, token, configure?)`
**Rate limiting:** 5 req/s per base. `AddStandardResilienceHandler` for retry on 429.

## Task Breakdown

| # | Task | Parallel | Dependencies |
|---|---|---|---|
| 1 | GitLab connector (NGitLab) | Yes | — |
| 2 | Bitbucket connector (Refit) | Yes | — |
| 3 | Zendesk Tickets provider (SDK + Refit) | Yes | — |
| 4 | Zendesk Articles provider (Refit) | No | After task 3 (shares package) |
| 5 | Airtable connector (SDK) | Yes | — |
| 6 | Update docs (data-providers.md, README, index, features.md) | No | After 1-5 |
| 7 | Build + test full solution | No | After 6 |

Each connector task includes: package setup, DTOs/interfaces, provider implementation, DI extensions, XML doc comments, ~5 tests, commit.
