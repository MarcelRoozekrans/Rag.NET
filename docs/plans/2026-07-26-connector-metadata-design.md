# Connector Metadata Consistency — Design (Phase 2.2)

**Date:** 2026-07-26
**Milestone:** 2 — Deferred Items & Technical Debt, Phase 2.2
**Covers:** the "Connector metadata consistency" debt recorded in `docs/planning/ROADMAP.md`

Phase 1.6 added `FileHandle.Metadata` and wired it through to chunk tags, but only the two
connectors built in that phase populate it. The other 19 emit nothing, so a chunk ingested
from Slack, Jira or GitHub carries no filterable record of where it came from.

## Scope decisions (agreed)

1. **Fields already in hand, plus the static→instance refactor** that lets ~9 connectors see
   their options and contribute container context. No DTO or query-string widening — that
   would re-record WireMock cassettes across five connectors, and is recorded as debt instead.
2. **Reserved keys are enforced by failing fast**, not documented and hoped for.
3. **`provider_id` is written centrally** so all 21 connectors get it consistently.
4. **A shared contract test** plus per-connector key assertions.

---

## 1. The convention, codified

Today the convention exists only as a pattern in two files. It becomes explicit:

| Rule | Reason |
|---|---|
| `snake_case`, lowercase, unprefixed | Matches the existing Exchange/Linear keys and the framework's own `document_id`/`file_name`/`created_at`. |
| Values are always `string` | `IReadOnlyDictionary<string, string>` — there is no other option. |
| Booleans are `"true"` / `"false"` | `bool.ToString()` yields `"True"`, which breaks ordinal tag matching in `HasTagSpec`. |
| Timestamps are `ToString("o", CultureInfo.InvariantCulture)` | ISO-8601 round-trip; the repo enforces culture-explicit formatting (MA0011/CA1305). |
| Optional fields are **omitted**, never written empty | Linear's precedent; its tests assert absence. An empty value is indistinguishable from a real one at query time. |
| Dictionary uses `StringComparer.Ordinal` | `DocumentMetadata.Tags` and `BuildMetadata` are ordinal; a mismatched comparer would make lookups inconsistent. |
| Prefer stable identifiers over display names | Linear chooses `state_type` (category) over `state` (workspace-specific display name) for exactly this reason. Emit both where both are useful. |
| Nothing to add → return `null`, not an empty dictionary | The parameter already defaults to `null` and the pipeline branches on `is not null`. One representation, not two. |

**Build the dictionary in a synchronous `ToHandle` helper, never inline in the async
iterator.** `foreach` over a list inside an async iterator trips `HLQ012` (it wants
`CollectionsMarshal.AsSpan`, which cannot cross `yield`/`await`), which `OneDriveDataProvider`
and `SharePointDataProvider` currently work around with local pragmas. Building metadata in a
synchronous helper — as Exchange and Linear do — avoids the rule entirely rather than
suppressing it.

## 2. Reserved keys, enforced

`MetadataBehavior` applies connector tags **first**, with `TryAdd`:

```csharp
foreach (var tag in ctx.Metadata.Tags)
    chunk.Metadata.TryAdd(tag.Key, tag.Value);
chunk.Metadata.TryAdd("document_id", ctx.Metadata.DocumentId);
chunk.Metadata.TryAdd("file_name",   ctx.Metadata.FileName);
chunk.Metadata.TryAdd("created_at",  ctx.Metadata.CreatedAt.ToString("O"));
```

So a connector tag named `created_at` does not lose to the framework — it **shadows** it, and
`TimeWeightedRetriever` (which reads `created_at`) then ranks on connector data with no
warning. Adding keys across 19 connectors makes that collision far more likely.

**Design:** the reserved set moves to one place in `Rag.NET.Abstractions`:

`document_id`, `file_name`, `created_at`, `provider_id`, `_parentKey`, `allowed_roles`,
`trust_level`

`BuildMetadata` throws a clear exception naming the offending key and connector when entry
metadata collides with it.

**Why throw rather than yield a `Result` failure**, against the house per-entry-failure
posture: connector tag keys are string literals in connector code, so a collision is a
deterministic authoring bug that would repeat identically for every document in the run. A
`Result` failure would produce N copies of the same error and let a corrupted ranking ship; a
throw surfaces it on the first document, in development. This is a programming error, not a
data error.

## 3. `provider_id`, written centrally

`ProviderId` is already passed to `IngestFromProviderAsync` and then discarded. `BuildMetadata`
writes it as a tag, so every connector gains it without per-connector work and users can
filter or re-ingest by source. It joins the reserved set, so a connector cannot shadow it.

## 4. Duplicate into tags; do not move out of content

Six connectors already render a metadata header into their Markdown — for example Jira:

```csharp
sb.Append($"**Status:** {issue.Fields.Status.Name}");
if (issue.Fields.Priority is not null)
    sb.Append($"  **Priority:** {issue.Fields.Priority.Name}");
```

Those lines **stay**, and the same values are additionally emitted as tags. The Markdown body
is what gets embedded, so it drives semantic recall; tags are what `HasTagSpec` filters on.
Moving values out of the body would silently degrade retrieval quality for anyone searching
for the text. Linear already does exactly this (`AppendMetadataLine` *and* tags) and is the
sanctioned precedent. Exchange is not a counter-example — it emits opaque RFC 822 MIME it
never renders.

## 5. Per-connector keys

Only fields already in hand at handle construction. Container context becomes reachable where
the static→instance refactor applies.

**File/blob pass-through** — AzureBlob, Box, Dropbox, GoogleDrive, OneDrive, SharePoint,
GitHub, GitLab, Bitbucket. Nothing is inlined into content today, so metadata is purely
additive: `path`, a container key (`repo`, `workspace`, `drive`, `folder`, `container`),
`ref`/`branch` where the connector is repository-shaped, and `change_status` on the delta
paths that already inspect it (GitHub `file.Status`, GitLab `IsNewFile`/`IsRenamedFile`,
Bitbucket `entry.Status`, Box `ev.EventType`).

**`path` means the file's own full path** — the value a user would filter with
`path` starts-with `docs/`. **OneDrive and SharePoint emit `parent_path`, not `path`.**
Graph's `DriveItem` exposes `ParentReference.Path`, which is the *containing folder* and
carries a `/drive/root:` namespace prefix; it is not the file's path. This document
originally specified `item.ParentReference?.Path` under the key `path` for those two
connectors — that was **an error in this design**, corrected during Part B. Filing a parent
folder path under `path` would have made a cross-connector `path` filter silently match
nothing on OneDrive and SharePoint, which is precisely the "worse than no tag" outcome this
phase exists to prevent. Emitting the file's own path there would require composing
`ParentReference.Path` with `Name` and stripping the namespace prefix — new behaviour, so it
is out of scope here and the honest parent-folder value keeps an honest key.

**Record/document** — Airtable, Asana, Confluence, Jira, Notion, Slack, MicrosoftTeams,
Zendesk (tickets + articles), Gmail, Web (Crawler/RSS/Sitemap): `url` where one exists,
`status`/`state`, author or assignee, a container key (`space`, `project`, `channel`,
`section_id`, `base_id`/`table`), and `updated_at`.

Zendesk articles is a free win: `SectionId` is already parsed into the DTO and used nowhere at
all today.

The exact key list per connector is fixed in the implementation plan, not here, because it
depends on field-by-field checking of what each DTO actually carries.

## 6. Static → instance conversion

Roughly nine connectors build handles in `static` helpers (Asana, Confluence, Jira, Zendesk
×2, Gmail, MicrosoftTeams, Airtable), so `_options` is out of scope and container context —
the most useful filter dimension — cannot be emitted. These become instance methods. The diff
is wide but mechanical, and it is a prerequisite for "only this Confluence space" or "only
this Jira project" being expressible at all.

## 7. Error handling

Connectors do not fail because metadata is unavailable: a missing optional field is omitted,
never an error. The only new failure mode is the reserved-key collision in §2, which is a
programming error and is meant to be loud. Existing per-entry `Result` failure behaviour and
cancellation propagation are untouched.

## 8. Testing

- **A shared contract check**, applied by every connector's suite: keys are `snake_case`, no
  reserved-key collision, no null or empty values, dictionary is ordinal. This is what catches
  convention drift across 19 connectors; per-connector tests alone cannot.
- **Per-connector assertions** pinning the actual key/value pairs, following the existing
  Exchange/Linear shape (including asserting *absence* for optional keys).
- **The missing base-class test**: `FileContentProviderBase` forwards `handle.Metadata` to
  `FileEntry.Metadata` and nothing tests it, though `GetFilesAsync_ETagIsForwardedFromHandle`
  exists for the sibling field.
- **Reserved-key enforcement tests** on `BuildMetadata`, and a `provider_id` test.

## 9. Documentation

`docs/guide/data-providers.md` documents metadata only in passing, per connector, and does not
mention Exchange's keys at all. It gains a Metadata section: the convention, a per-connector
key table, the reserved-key list, and the base-vs-entry precedence rule (entry wins) — none of
which is written down anywhere today.

## Recorded, not fixed

Five connectors narrow their API field selections so the interesting fields are never fetched.
Widening them means DTO changes plus re-recorded WireMock cassettes, so it is deliberately out
of scope. The exact strings a future phase would need to change:

- **Confluence** — `expand=body.storage,version` (`IConfluenceApi.cs:14,22`); misses `space`,
  `_links.webui`, `history.createdDate`, `version.by`.
- **Jira** — `fields=summary,description,status,priority,assignee,comment,updated`
  (`IJiraApi.cs:14`); misses `created`, `labels`, `issuetype`, `reporter`, `project`.
- **Asana** — `OptFields = "gid,name,notes,due_on,completed,assignee.name,modified_at"`
  (`AsanaDataProvider.cs:27-28`); misses `permalink_url`, `created_at`, `projects`, `tags`.
- **GoogleDrive** — `files(id, name, mimeType, md5Checksum)` (`GoogleDriveDataProvider.cs:85`,
  `:141`, `:156`); `BuildHandle` also discards the `File` object it was given.
- **Box** — `fields: ["id", "name", "type", "sha1"]` (`BoxDataProvider.cs:50`); misses
  `modified_at`, `created_by`, `path_collection`, `size`.

Also noted: `BuildMetadata` drops `baseMetadata.CreatedAt`, so every provider-ingested document
gets `DateTime.UtcNow` and a connector's real creation timestamp cannot reach
`DocumentMetadata.CreatedAt` — only a tag. Out of scope here; recorded so it is not
rediscovered as new.

## Out of scope

- Widening API field selections (above).
- Namespacing tag keys per connector. Keys stay unprefixed for usability; the reserved-key
  check covers the collisions that actually matter.
- Reworking `MetadataBehavior`'s `TryAdd` ordering beyond the reserved-key guard.
