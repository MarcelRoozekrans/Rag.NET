# Connector Metadata Consistency Implementation Plan (Phase 2.2)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Every connector populates `FileHandle.Metadata` to one codified convention, reserved keys are enforced rather than hoped for, and `provider_id` is written centrally so all 21 connectors gain it.

**Architecture:** Per `docs/plans/2026-07-26-connector-metadata-design.md`. Part A lands the framework (reserved-key guard, `provider_id`, the shared contract test) and **must complete before B and C**, which both depend on the contract helper. B (file/blob connectors) and C (record/document connectors) touch disjoint projects and can run concurrently. D is docs.

**Tech Stack:** .NET 10, xUnit v3, NSubstitute (hand-written fakes for `ValueTask` members — EPS06).

**Conventions:** MA0051 (≤60-line methods), MA0015, ZA0601/ZA0501, EPS05/EPS06, HLQ012/HLQ013 — all warnings-as-errors, build must end 0/0. xUnit v3 `TestContext.Current.CancellationToken`; deterministic tests, no sleeps. Conventional commits ending with a blank line then `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Never stage `.lucent/*` or `.claude/worktrees/*`** — `git add` explicit paths only.

**The convention every connector must follow** (design §1 — reread it before writing any connector):
- `snake_case` lowercase keys, unprefixed.
- Booleans as `"true"`/`"false"` string literals — **never** `bool.ToString()`, which yields `"True"` and breaks ordinal tag matching.
- Timestamps as `ToString("o", CultureInfo.InvariantCulture)`.
- Optional fields **omitted**, never written as empty strings.
- `new Dictionary<string, string>(StringComparer.Ordinal)`.
- Nothing to add → return `null`, not an empty dictionary.
- **Build the dictionary in a synchronous `ToHandle` helper, never inline in the async iterator** — a `foreach` there trips HLQ012 (see the existing pragmas at `OneDriveDataProvider.cs:153-155` and `SharePointDataProvider.cs:99-101`; do not add more).

**Reference implementations to read first:** `src/Rag.NET.DataProviders.Exchange/ExchangeMailDataProvider.cs:260-279` and `src/Rag.NET.DataProviders.Linear/LinearDataProvider.cs:232-265`.

---

## Part A — Framework (do this first; B and C depend on it)

### Task A1: reserved keys + the `BuildMetadata` guard

**Files:**
- Create: a reserved-key definition in `src/Rag.NET.Abstractions/` (place it next to the other shared constants — check where `ParentChunkKeyHelper`'s `_parentKey` and `TimeWeightedRetriever`'s `CreatedAtKey` live and pick the home that lets both `Rag.NET` and connectors reference it without a new project reference).
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs:225-248` (`BuildMetadata`).
- Test: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`.

The reserved set: `document_id`, `file_name`, `created_at`, `provider_id`, `_parentKey`, `allowed_roles`, `trust_level`. Source them from the existing constants where they already exist (`ParentChunkKeyHelper.ParentKeyMetadata`, `TimeWeightedRetriever.CreatedAtKey`) rather than re-typing string literals — a duplicated literal that drifts is exactly the bug this guard exists to prevent.

**Why this throws** (design §2): connector tag keys are string literals in connector code, so a collision repeats identically for every document. A `Result` failure would emit N copies of one authoring bug and still ship a corrupted ranking. Throw with the offending key named.

**Write the failing test first:**

```csharp
// 1. BuildMetadata_EntryTagCollidingWithReservedKey_Throws — theory over all 7 reserved keys,
//    asserting the message names the offending key.
// 2. BuildMetadata_NonReservedEntryTag_IsForwarded — the existing merge behaviour is unchanged.
```

Run them, watch the first fail, then implement.

**Commit:** `feat(data-providers): reject connector tags that shadow reserved chunk keys`

### Task A2: `provider_id` written centrally

**Files:**
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs` — `BuildMetadata` needs the `ProviderId` that `IngestFromProviderAsync` already receives and currently discards. Both call sites are `:139` (no hash store) and `:182` (hash-store path) — update both.
- Test: same file as A1.

```csharp
// 3. IngestFromProviderAsync_WritesProviderIdTag — asserts tags["provider_id"] == the passed id
//    on BOTH call paths (with and without a hash store; the hash-store path is a separate branch).
```

`provider_id` is reserved (A1), so a connector cannot shadow it — add a test proving that too.

**Commit:** `feat(data-providers): tag ingested documents with their provider id`

### Task A3: the base-class forwarding test (currently missing)

**Files:**
- Test: `tests/Rag.NET.DataProviders.Tests/FileContentProviderBaseTests.cs`

`FileContentProviderBase.cs:52` forwards `handle.Metadata` to `FileEntry.Metadata` and **nothing tests it**, even though `GetFilesAsync_ETagIsForwardedFromHandle` (`:91-102`) exists for the sibling field. The local `Handle(...)` helper at `:24-25` omits the parameter — extend it.

```csharp
// 4. GetFilesAsync_MetadataIsForwardedFromHandle
// 5. GetFilesAsync_NullMetadataStaysNull — pins the null convention
```

**Commit:** `test(data-providers): pin FileHandle.Metadata forwarding through the base class`

### Task A4: the shared contract check

**Files:**
- Create: a contract helper in `tests/Rag.NET.DataProviders.Tests/` (it must be consumable by the other 20 connector test projects — check how they reference shared test code today; if there is no existing mechanism, the cheapest honest option is a small shared source file linked via `<Compile Include="..\..." Link="..." />`, the same pattern Part B of Phase 2.1 used for `src/Shared/GraphErrorMapping.cs`. If you pick something else, say why.)

The check, given an `IReadOnlyDictionary<string,string>?`:
- every key is `snake_case` (lowercase, digits, underscores; no leading/trailing underscore)
- no key is in the reserved set
- no value is null or empty
- the dictionary's comparer is `StringComparer.Ordinal`
- if the connector has nothing to add, the whole dictionary is `null` rather than empty

Add a self-test proving the checker itself rejects each violation — a contract test that cannot fail is worse than none.

**Commit:** `test(data-providers): shared metadata contract check for connector suites`

---

## Part B — File/blob connectors (9)

Nothing here renders Markdown, so metadata is purely additive and there is no duplicate-vs-move question. **Only emit fields already in hand** — do not widen API field selections or DTOs (design "Recorded, not fixed").

For each connector: add the keys, add a per-connector test pinning them, and apply the Part A contract check.

| Connector | File:line of `FileHandle` construction | Keys to emit (verify each is genuinely in hand before emitting) |
|---|---|---|
| AzureBlob | `AzureBlobDataProvider.cs:39-49` | `path` (`blob.Name`), `container` (from `_container`) |
| Box | `BoxDataProvider.cs:65-74` full, `:106-115` delta | `folder_id` (traversal stack, `:43`), `change_status` (delta: `ev.EventType`) |
| Dropbox | `DropboxDataProvider.cs:56-69` full, `:92-105` delta | `path` (`file.PathDisplay`), `folder` (`_options.FolderPath`) |
| GoogleDrive | `BuildHandle` `:161-183`, callers `:64`, `:99`, `:127` | `mime_type` (available at every call site but currently dropped), `folder_id` where the traversal knows it (`:80`) |
| OneDrive | `ToHandle` `:178-190` | `path` (`item.ParentReference?.Path`), `drive_id` |
| SharePoint | `ToHandle` `:124-135` | `path`, `drive_id` (`_options.DriveId`) |
| GitHub | `:55-64` tree, `:81-90` delta | `path`, `repo` (`{_owner}/{_repo}`), `ref` (`_options.Branch`), `change_status` (delta: `file.Status`, already inspected at `:78`) |
| GitLab | `:54-65` tree, `:83-95` delta | `path`, `project` (`_options.ProjectIdOrPath`), `ref` (`_options.Ref`), `change_status` (delta: derive from `IsNewFile`/`IsRenamedFile`/`IsDeletedFile`) |
| Bitbucket | `:72-77` src, `:122-127` diffstat | `path`, `repo` (`{Workspace}/{RepoSlug}`), `ref` (`_options.Ref`), `change_status` (diffstat: `entry.Status`, checked at `:113`) |

**Note on `change_status`:** normalise to a small stable vocabulary (`added`/`modified`/`removed`/`renamed`) rather than passing each vendor's raw string through — the whole point of a tag is cross-connector filterability. Document the mapping per connector in a comment.

**Commit per connector** (or in small batches of related ones), e.g. `feat(data-providers): emit path and repo metadata from GitHub`.

---

## Part C — Record/document connectors (10)

**Duplicate, do not move** (design §4): where a connector already renders `**Status:** …` into Markdown, that line **stays** and the value is *additionally* emitted as a tag. The Markdown is embedded (drives recall); tags are filtered (`HasTagSpec`). Removing the line would silently degrade retrieval.

**Several of these need `static ToHandle` → instance conversion** so `_options` is reachable (design §6). Do the conversion in the same commit as that connector's metadata.

| Connector | File:line | Static? | Keys | Already inlined in Markdown? |
|---|---|---|---|---|
| Airtable | `ToMarkdownHandle` `:96-108`, attachments `:128-132` | yes | `base_id`, `table`, `record_id`; attachments also `field`, `attachment_id` | fields are the content |
| Asana | `ToHandle` `:92-101` | yes | `assignee`, `completed` (`"true"`/`"false"` — note `:110` currently renders raw `{task.Completed}` giving `"True"`; the **tag** must be lowercase), `due_on`, `updated_at` (`ModifiedAt`) | yes `:106-110` — keep |
| Confluence | `ToHandle` `:151-160` | yes | `page_id`, `version`; `space` only if reachable from options after conversion | title only |
| Jira | `ToHandle` `:168-177` | yes | `issue_key`, `status`, `priority`, `assignee`, `updated_at`; `project` from options after conversion | yes `:184-188` — keep |
| Notion | `BuildHandleAsync` `:95-100` | no (instance) | `page_id`, `database_id` (`_options.DatabaseId`), `updated_at` (`LastEditedTime`) | title only |
| Slack | `:100-107` | no | `channel`, `channel_id`, `date`, `message_count` | yes `:191`, `:209` — keep |
| MicrosoftTeams | `GroupByDay` `:229-237` | yes | `team_id`, `channel_id`, `channel`, `date`, `message_count` | yes `:252`, `:261` — keep |
| Zendesk tickets | `ToHandle` `:96-105` | yes | `ticket_id`, `status`, `priority`, `updated_at` | yes `:110-114` — keep |
| Zendesk articles | `ToHandle` `:86-95` | yes | `article_id`, `section_id` (**already parsed at `ZendeskArticle.cs:20-21` and used nowhere today — free win**), `updated_at` | title only |
| Gmail | `ToHandle` `:92-103` | yes | `from`, `date`, `has_attachments` | yes `:110` — keep |

**The three Web providers** (`WebCrawlerDataProvider.cs:62-69`, `RssDataProvider.cs:46-58` and `:74-86`, `SitemapDataProvider.cs:61-73`) emit `FileEntry` **directly** — they do not extend `FileContentProviderBase`, so they set `FileEntry.Metadata` rather than `FileHandle.Metadata`. Keys: crawler `url`, `depth`, `host`; RSS `url`, `published_at`, `author` where present; sitemap `url`, `lastmod`. Apply the same contract check.

**Commit per connector or small batch**, e.g. `feat(data-providers): emit issue metadata from Jira`.

---

## Part D — Docs

**Files:**
- Modify: `docs/guide/data-providers.md`

Add a **Metadata** section — none of this is documented today:
- The convention (§1 of the design), as a short table.
- A per-connector key table covering all 21 connectors, including Exchange's `folder`/`has_attachments`/`received_at`, which are currently undocumented entirely.
- The reserved-key list and what happens on collision (throws, naming the key).
- `provider_id` — present on every ingested document, and what it is for.
- The precedence rule: base metadata is written first, entry metadata wins on collision.

**Commit:** `docs(data-providers): document the connector metadata convention and keys`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Every connector test project green, plus `tests/Rag.NET.Tests` (~1235) and `tests/Rag.NET.DataProviders.Tests` as regression nets.
3. Every one of the 21 connectors is covered by the contract check — grep to confirm none was missed, and state the count explicitly.
4. `docs/planning/ROADMAP.md` — the "Connector metadata consistency" debt moves to Closed; `MILESTONE.md` Phase 2.2 complete. **At close-out, after the whole-phase review — not per part.**
5. Whole-phase review; merge decision.
