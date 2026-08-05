# Connector Timestamp Threading Implementation Plan (Phase 4.10)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Give connectors a typed channel for the timestamps they already hold, so real values reach `DocumentMetadata` instead of an opaque `ETag` or a hand-formatted tag.

**Architecture:** `FileHandle` and `FileEntry` gain optional `CreatedAt`/`UpdatedAt`; `MetadataBehavior` emits `updated_at` from the typed field as it already does for `created_at`; `TimeWeightedRetriever` prefers `UpdatedAt` over `CreatedAt`. Connectors populate whichever concept they genuinely have — never the other.

**Tech Stack:** .NET 10, xUnit v3, WireMock (connector cassettes).

**Design:** `docs/plans/2026-08-05-connector-timestamps-design.md`

---

## A correction to the design, measured before planning

The design says **eight** connectors hand-write `updated_at`. **It is five.** The eight came from
counting every writer of the four `FallbackMetadataKeys`:

| Tag | Writers | Becomes reserved? |
|---|---|---|
| `updated_at` | **5** — Asana `:129`, Jira `:195`, Notion `:120`, ZendeskArticles `:112`, ZendeskTickets `:123` | **Yes** |
| `published_at` | 1 — RSS `:119` | No |
| `lastmod` | 1 — Sitemap `:88` | No |
| `received_at` | 1 — Exchange `:271` | No |
| `date` | 3 — Gmail `:120`, Teams `:259`, Slack `:130` | No |

**So the runtime hazard is five migration sites, not eight.** The other tags keep their own keys
and stay unreserved, exactly as the design intends.

## The exact shapes to thread through

```csharp
// src/Rag.NET.DataProviders/FileHandle.cs
public sealed record FileHandle(
    string Id, string FileName, string? ETag,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    IReadOnlyDictionary<string, string>? Metadata = null);

// src/Rag.NET/DataProviders/FileEntry.cs
public sealed record FileEntry(
    EntryId Id, string FileName,
    Func<CancellationToken, Task<Stream>> OpenContentAsync,
    string? ETag = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

// src/Rag.NET.DataProviders/FileContentProviderBase.cs:47-52 — the copy site
```

Both are positional records; add the two new parameters **last, with defaults**, so no existing
positional construction breaks.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`, `TreatWarningsAsErrors=false`.** MA0051 (≤60-line methods), MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Central Package Management is on — no `Version` attributes on `PackageReference`.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- A file watcher edits `.csproj` concurrently — `git status` before committing.

**Baselines (main, 2026-08-05):** `Rag.NET.Tests` **1159**, `RepoConventions` **36 + 1 skip**, `PackageValidation` **20**, `DataProviders.Tests` **69**, `Microsoft365.Tests` **70**, `Evaluation.Tests` **388**. Measure each connector suite before touching it.

---

## Task 1: The typed fields and the two threading paths

**Files:**
- `src/Rag.NET.Abstractions/Models/DocumentMetadata.cs` — add `UpdatedAt`
- `src/Rag.NET.DataProviders/FileHandle.cs`, `src/Rag.NET/DataProviders/FileEntry.cs` — add both
- `src/Rag.NET.DataProviders/FileContentProviderBase.cs:47-52` — copy both
- `src/Rag.NET/DataProviders/RagPipelineExtensions.cs` (`BuildMetadata`, ~322-329) — copy both onto `DocumentMetadata`

**Step 1: Write two failing tests** — one asserting a `FileEntry` timestamp reaches
`DocumentMetadata`, one asserting it survives `FileHandle` → `FileEntry` via
`FileContentProviderBase`. Both fail today (no field exists).

**Step 2:** Add `public DateTime? UpdatedAt { get; init; }` to `DocumentMetadata`, and
`DateTime? CreatedAt = null, DateTime? UpdatedAt = null` **as the last positional parameters** of
both records. Copy them at both sites.

**Step 3:** Tests pass. Full solution builds 0 warnings.

**The second path — do not skip this.** `RssDataProvider`, `SitemapDataProvider` and
`WebCrawlerDataProvider` implement `IFileContentProvider` **directly** and never touch
`FileHandle`. Add a test proving a timestamp set by an `IFileContentProvider`-direct provider
reaches `DocumentMetadata`. **Missing this path fails silently** — those connectors would simply
never set a timestamp, which is indistinguishable from a connector that has none.

---

## Task 2: Emit the tag from the typed field

**Files:**
- `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs` (~line 20)

**Step 1: Write the failing test** — a `DocumentMetadata` with `UpdatedAt` set produces an
`updated_at` chunk-metadata entry in ISO-8601 (`"O"`); with it null, **no** entry appears.

**Step 2:** Mirror the existing `created_at` write exactly:

```csharp
if (ctx.Metadata.UpdatedAt is { } updatedAt)
{
    chunk.Metadata.TryAdd(ReservedMetadataKeys.UpdatedAt, updatedAt.ToString("O"));
}
```

Keep `TryAdd` — a connector tag must still win, per `ReservedMetadataKeys`' own doc comment.

**Do not reserve the key yet.** Reservation is Task 4, and it must land with the migrations.

---

## Task 3: Ranking precedence

**Files:**
- `src/Rag.NET/Retrieval/TimeWeightedRetriever.cs` (`ResolveTimestamp`, ~70-92)

**Step 1: Write failing tests** for the full order: `updated_at` wins over `created_at`;
`created_at` used when `updated_at` absent; fallback keys when both absent; **neutral 1.0 when all
absent** (this last one already passes — Phase 4.9 pinned it as
`AbsentTimestampAndNoFallbackMatch_ScoresExactlyBaseScore`; keep it green).

**Step 2:** Add `updated_at` ahead of `created_at` in resolution.

**Step 3:** Run the whole `TimeWeightedRetrieverTests` suite. **If a Phase 4.9 test changes
meaning, stop and report** — 4.9's neutral-on-absence property is the foundation this design rests
on and must not move.

---

## Task 4: Reserve the key AND migrate all five — one commit

**This is the phase's one runtime hazard.** Reserving `updated_at` while any connector still writes
it by hand throws `ReservedMetadataKeyException` **at runtime, not compile time** — and for several
connectors that means only against a live service. **Reservation and all five migrations land
together, or neither lands.**

**Files:**
- `src/Rag.NET.Abstractions/Models/ReservedMetadataKeys.cs` — add `UpdatedAt = "updated_at"` to the constant list **and** the reserved set
- `src/Rag.NET.DataProviders.Asana/AsanaDataProvider.cs:129`
- `src/Rag.NET.DataProviders.Jira/JiraDataProvider.cs:195`
- `src/Rag.NET.DataProviders.Notion/NotionDataProvider.cs:120`
- `src/Rag.NET.DataProviders.Zendesk/ZendeskArticlesDataProvider.cs:112`
- `src/Rag.NET.DataProviders.Zendesk/ZendeskTicketsDataProvider.cs:123`

**Step 1:** For each of the five, delete the `metadata["updated_at"] = …` line and set
`UpdatedAt` on the `FileHandle`/`FileEntry` instead. The source values are strings — parse to
`DateTime` (round-trip/ISO); **if a value does not parse, leave the field unset rather than
guessing a format.** Report any connector whose value does not parse cleanly.

**Step 2: Write one test per connector** asserting the timestamp now arrives as the typed field and
still surfaces as an `updated_at` chunk tag. **Five tests, no exceptions** — a missed connector is
invisible until it runs.

**Step 3:** Add a guard test asserting **no** provider under `src/Rag.NET.DataProviders.*` writes a
reserved key by hand. Prove it fails by reinstating one of the five lines, then revert. This is
what stops the sixth connector reintroducing the hazard.

**Step 4:** Run every affected connector suite plus `DataProviders.Tests`. State counts.

---

## Task 5: Populate the connectors that already hold the value

No DTO or cassette changes — the data is already in hand. Group into commits by family.

**Populate `UpdatedAt`:** Dropbox (`ServerModified`), Linear (`updatedAt` — currently ETag/watermark only, never tagged), Sitemap (`lastmod`), Confluence *(deferred to Task 6 — its DTO drops the value)*.

**Populate `CreatedAt`:** Airtable (`CreatedTime`), Gmail (`message.Date`), Slack (`msg.Ts`).

**Populate both:** AzureBlob (`CreatedOn`/`LastModified`), OneDrive + SharePoint + Teams (`CreatedDateTime`/`LastModifiedDateTime`), Exchange (`receivedDateTime` → `CreatedAt`, `lastModifiedDateTime` → `UpdatedAt`), RSS/Atom (`published` → `CreatedAt`, `updated` → `UpdatedAt`), LocalFiles (`CreationTimeUtc`/`LastWriteTimeUtc`).

**Leave unset, deliberately:** GitHub, GitLab, WebCrawler — no vendor timestamp exists. **Add a test asserting these produce no timestamp**, so "we checked and there is none" is recorded rather than looking like an oversight.

**Bitbucket:** investigate whether the wire payload carries a commit date. **If unconfirmed, leave unset and record why** — an unset field is a truthful "unknown".

**Note on Slack and Teams:** their existing `date` tags are **day-granularity**. Set the typed field from the full underlying value (`msg.Ts`, `CreatedDateTime`), and **leave the `date` tags exactly as they are** — normalising them is out of scope per design §8.

---

## Task 6: The three that need DTO widening and cassettes

**Confluence, Box, GoogleDrive** — these do not fetch the timestamp at all.

- **Confluence** — `ConfluenceVersion.cs:5-6` maps only `number`; add `when`. The `expand` in `IConfluenceApi.cs:9-24` may need widening.
- **Box** — `BoxDataProvider.cs:49-51` requests `["id","name","type","sha1"]`; add `created_at`/`modified_at`.
- **GoogleDrive** — four field masks (`:53-56, :86-89, :147-149, :163-165`) request `id, name, mimeType, md5Checksum`. **All four need widening** — miss one and that code path silently has no timestamp.

**Re-record the WireMock cassettes** for each. This is the cost Phase 2.2 declined to pay for these same three connectors — it is one cost seen twice, not two costs.

**Jira is NOT in this group.** It already requests `updated` (`IJiraApi.cs:14`) and is handled in Task 4.

---

## Task 7: Documentation and close

- `docs/guide/retrieval.md` — the resolution order is now `UpdatedAt` → `CreatedAt` → fallback keys → neutral. Phase 4.9 rewrote this section; update rather than append.
- `docs/guide/data-providers.md` — which connectors supply which timestamp, and the four that supply none.
- `docs/planning/ROADMAP.md` and `MILESTONE.md` — close **Phase 4.10** in the house form.

**Record:** the corrected count (**five** `updated_at` writers, not eight); that **Jira never needed DTO work**, correcting Phase 4.9's contradiction; which connectors remain without timestamps and why; and Bitbucket's outcome.

**Do not tick a DoD box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.DataProviders.Tests
dotnet test tests/Rag.NET.DataProviders.Microsoft365.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.PackageValidation.Tests
```

Plus every connector suite touched. **The deliverable is that a document ingested from Notion, Jira or SharePoint carries the source system's real timestamp — and that the four connectors with none carry nothing rather than a fabrication.**
