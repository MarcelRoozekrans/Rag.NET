# Connector Timestamp Threading — Design (Phase 4.10)

**Date:** 2026-08-05
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. What this finishes

Phase 4.9 stopped `DocumentMetadata.CreatedAt` being fabricated. It is now `DateTime?` with no
default, `MetadataBehavior` writes `created_at` only when a value exists, and
`TimeWeightedRetriever` treats absence as neutral (decay `1.0`) — so provider-ingested documents
rank **neutrally rather than wrongly**.

That was honest but incomplete, and 4.9 said so: **17 of 25 providers hold a real timestamp and
discard it**, folded into an opaque `ETag` or an un-promoted tag. This phase gives them a typed
channel.

## 1. The measurement that shapes the design

Phase 4.9 recorded the split as "17 discard / 4 do not fetch / 4 have none". Re-measuring
per-connector, with SDK assemblies reflected rather than vendor docs trusted, gives a finer and
more consequential picture:

| Category | Count | Providers |
|---|---|---|
| **Both created and modified** | 9 | AzureBlob, Box, GoogleDrive, ExchangeMail, MicrosoftTeams, OneDrive, SharePoint, RSS (Atom), LocalFiles |
| **Modified only** | 9 | Asana, Confluence, Dropbox, Jira, Linear, Notion, Sitemap, Zendesk Tickets, Zendesk Articles |
| **Created only** | 3 | Airtable, Gmail, Slack |
| **Neither** | 3 | GitHub, GitLab, WebCrawler |
| **Unclear** | 1 | Bitbucket |

**12 of 25 connectors have exactly one of the two concepts and no honest way to populate the
other.** That is what decides the design.

### Two corrections to Phase 4.9's record

1. **Jira does not belong in the "does not even fetch it" group.** `IJiraApi.cs:14` requests
   `fields=…,updated` explicitly, and `JiraDataProvider.cs:174,195` uses it as both the ETag and
   the `updated_at` tag. The 4.9 design doc contradicts itself — its §3 correctly lists Jira among
   connectors already writing the tag. **Only three need DTO widening: Confluence, Box,
   GoogleDrive.**
2. **The "17 discard a real timestamp" framing assumes that timestamp is always the wrong kind.**
   For Airtable, Gmail and Slack the available value is *creation*-shaped, and belongs in
   `CreatedAt` as-is.

## 2. Two typed fields

`DocumentMetadata` gains **`UpdatedAt`** beside `CreatedAt` — both `DateTime?`, neither defaulted.

**Named `UpdatedAt`, not `ModifiedAt`**, because the emitted tag is `updated_at`, eight connectors
already speak that vocabulary, and it is already in `FallbackMetadataKeys`. A property emitting a
differently-named tag would be a small permanent inconsistency.

**Neither field is ever populated from the other.** The 12 single-concept connectors leave the
other empty. This is the whole point: 4.9 removed a fabricated `CreatedAt`, and squeezing a
modified time into it would restore that defect under a new name.

**RSS/Atom settles the question on its own** — the wire format names `published` and `updated` as
separate elements, so a single field cannot represent an Atom entry at all without picking a side.

`DocumentMetadata` is a `sealed class` built everywhere by object initialiser, with exactly one
production construction site (`RagPipelineExtensions.cs:322-329`) and no exhaustive matching over
its shape, so the addition is safe.

## 3. The typed channel, and the path that is easy to miss

`FileHandle` and `FileEntry` each gain `CreatedAt` and `UpdatedAt`, threaded through
`FileContentProviderBase.GetFilesAsync` — a one-line copy beside the existing `ETag`/`Metadata`
pass-through.

**`RssDataProvider`, `SitemapDataProvider` and `WebCrawlerDataProvider` implement
`IFileContentProvider` directly and never touch `FileHandle`.** The field must reach `FileEntry`
through that second path independently. Missing it fails silently — those connectors would simply
never set a timestamp, which looks exactly like a connector that has none.

## 4. The tag becomes an output, not an input

`MetadataBehavior` writes `updated_at` from `UpdatedAt`, conditionally, exactly as it already
writes `created_at` from `CreatedAt`. `ReservedMetadataKeys` reserves `updated_at`, and the eight
connectors that hand-write it stop.

The benefit is more than tidiness: those eight each format the value themselves today.
Centralising it yields **one ISO-8601 format instead of eight**, and makes the day-granularity
values in Slack and Teams visible rather than hidden behind per-connector formatting.

`lastmod`, `published_at` and `received_at` **stay** as connector-specific tags — they carry
meaning beyond recency, and removing them would break user filters for no gain.
`FallbackMetadataKeys` therefore becomes a **compatibility mechanism** rather than the primary
path.

## 5. Ranking

`TimeWeightedRetriever` resolves in order:

```
UpdatedAt  →  CreatedAt  →  FallbackMetadataKeys  →  absent: neutral 1.0
```

Freshness is a last-changed question, and **18 of the 22 connectors with any signal have a
modified-shaped one**. A 2019 wiki page edited yesterday *is* current information.

The neutral-on-absence behaviour carries the whole design and was pinned by Phase 4.9
(`AbsentTimestampAndNoFallbackMatch_ScoresExactlyBaseScore`), so no retriever architecture
changes.

## 6. Connector work

- **9 populate both** — AzureBlob, Box, GoogleDrive, Exchange, Teams, OneDrive, SharePoint,
  RSS/Atom, LocalFiles
- **9 populate `UpdatedAt`** — Asana, Confluence, Dropbox, Jira, Linear, Notion, Sitemap,
  Zendesk ×2
- **3 populate `CreatedAt`** — Airtable, Gmail, Slack
- **3 stay empty, honestly** — GitHub, GitLab, WebCrawler
- **Bitbucket** — investigate; leave unset if unconfirmed. An unset field is a truthful "unknown".

**Three need DTO widening and re-recorded WireMock cassettes: Confluence, Box, GoogleDrive** — the
same three Phase 2.2 declined to pay this cost for, so it is one cost seen twice, not two costs.

## 7. The risk this phase must manage

**Reserving `updated_at` while any connector still writes it by hand throws
`ReservedMetadataKeyException` at runtime, not compile time.** The reservation and all eight
migrations must land in one change, and each of the eight needs a test — otherwise the failure
surfaces only when that specific connector runs, which for several means only against a live
service.

This is the one part of the phase that can fail after a green build.

## 8. Out of scope

- **Normalising Slack's and Teams' day-granularity timestamps** into full ones. That changes what
  those connectors report and belongs with whoever fixes their fetch. Recorded, not silently
  upgraded.
- **Removing `lastmod` / `published_at` / `received_at` tags** — see §4.
- **Any change to `created_at`'s reservation**, which stays as Phase 4.9 left it.
