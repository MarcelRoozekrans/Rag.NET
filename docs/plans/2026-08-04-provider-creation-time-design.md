# Provider Creation Time — Design (Phase 4.9)

**Date:** 2026-08-04
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. Why this is its own phase

The defect was routed to Phase 4.2 with the note *"the fix is one copied property plus a test: a
slot, not a phase."* **That estimate is wrong**, and the evidence against it was already in the
repository: `docs/plans/2026-07-26-connector-metadata-design.md:237-240` — the document the
roadmap entry itself cites — states plainly that *"a connector's real creation timestamp cannot
reach `DocumentMetadata.CreatedAt` — only a tag."*

Pulling it out of 4.2 also stops a correctness bug queueing behind API-design work. 4.2 had
accumulated six routings and its own goal; this one is the only item in it that is simply *wrong*
rather than *unfinished*.

## 1. The defect

`src/Rag.NET.Abstractions/Models/DocumentMetadata.cs:22` defaults `CreatedAt` to
`DateTime.UtcNow`. Nothing on the provider-ingestion path sets it —
`src/Rag.NET/DataProviders/RagPipelineExtensions.cs` builds a `DocumentMetadata` copying
`ContentType` but not `CreatedAt`. `MetadataBehavior.cs:20` then writes that value into every
chunk as `created_at`, and `TimeWeightedRetriever` ranks on it.

**So every provider-ingested document claims to have been created at ingestion time.** A 2019
Confluence page and a document added this morning score identically on recency.

This is not a missing value. It is a **wrong one, asserted confidently** — which is why it reads
as working.

### Why the one-line fix is not a fix

- **`baseMetadata` is per-call, not per-document** (`RagPipelineExtensions.cs:67-76`). Copying its
  `CreatedAt` would stamp every document in an ingestion run with one identical value — not each
  document's real creation time.
- **No production caller sets `CreatedAt` at all.** Only tests, plus container-propagation code
  that forwards whatever the parent already had.
- **`FileEntry` and `FileHandle` carry no timestamp field**, so a connector has no typed channel
  to supply one.
- **The tag channel is closed**: `created_at` is reserved, and a connector emitting it gets
  `ReservedMetadataKeyException` (`RagPipelineExtensions.cs:312-313`).

## 2. Stop fabricating

`DocumentMetadata.CreatedAt` becomes `DateTime?` with **no default**. `MetadataBehavior` writes the
`created_at` tag only when a value exists.

The correctness argument rests on a property that already holds: **`TimeWeightedRetriever` handles
absence correctly today.** `ResolveTimestamp` (`TimeWeightedRetriever.cs:70-92`) returns `null` for
a missing key, `ComputeDecay` returns a neutral `1.0`, and ranking is simply undistorted. Removing
the fabrication therefore needs *no new retriever logic* — it needs less of it.

Put plainly: **an honestly absent timestamp already ranks better than the fabricated one.**

`BuildMetadata` also copies `baseMetadata?.CreatedAt`, so the batch-level override becomes real
rather than silently dropped. It is not the fix — it is one line that stops a second, smaller lie.

### The cost

This is a **breaking change to a public model**: consumers reading `.CreatedAt` as non-null will not
compile. Nothing is published and no package ID is reserved, so it costs a recompile now and would
need `[Obsolete]` shims after Phase 6.3 — the same window that made Phases 4.7 and 4.8 cheap.

## 3. Use the timestamps connectors already emit

`TimeWeightedOptions.FallbackMetadataKeys` exists precisely for this and **defaults to `[]`** — the
mechanism is built and wired to nothing.

Default it to:

```csharp
["updated_at", "published_at", "lastmod"]
```

**Asana, Jira, Notion, Zendesk (tickets and articles), RSS, Sitemap and Exchange already write
these tags today.** With no connector change at all, time-weighted retrieval starts ranking those
sources by their real timestamps instead of neutrally.

### Why `date` is excluded

Gmail, Slack and Teams emit `date`. It is deliberately **not** a default:

- **Mixed granularity** — Gmail's is a full timestamp; Slack's and Teams' are day-only.
- **Ambiguous meaning** — `date` is generic enough that a user's own metadata may mean something
  else entirely by it (an invoice date, a due date). Silently ranking recency on that would be
  exactly the confident-but-wrong behaviour this phase removes.

It is documented as a one-line opt-in instead. The three chosen keys unambiguously mean *when this
content last changed*.

## 4. The gap this does not close, stated rather than hidden

**17 of 25 providers hold a real timestamp and discard it** — `AzureBlob`, `OneDrive`,
`SharePoint`, `Dropbox`, `Linear`, `Gmail`, `Teams`, `Slack` and others fold it into an opaque
`ETag` or an un-promoted tag. Four more (`Confluence`, `Jira`, `Box`, `GoogleDrive`) do not even
request it from the API.

Closing that needs a typed field on `FileEntry`/`FileHandle`, threading through
`FileContentProviderBase` and `BuildMetadata`, ~17 connector changes, and for four of them DTO
changes with **re-recorded WireMock cassettes**. That is a phase, and it gets scheduled as one —
the same design doc already priced it as out of scope once, and calling it a "slot" is how this
defect survived being recorded twice.

After this phase those connectors rank **neutrally rather than wrongly**. Better, and honest about
being incomplete.

## 5. Testing

**The gap that let this survive: no test asserts what `CreatedAt` becomes after provider
ingestion.** `IngestFromProviderTests` only checks that a connector emitting a `created_at` tag
throws; `MetadataBehaviorCreatedAtTests` and `TimeWeightedRetrieverTests` test the two ends in
isolation. The path between them is untested, which is precisely where the defect lives.

- **End-to-end through provider ingestion** — written first, failing against today's code. This is
  the test whose absence is the story.
- `MetadataBehavior` omits `created_at` when null, rather than writing `"null"` or throwing.
- Fallback resolution order, including a document carrying several candidate tags.
- `TimeWeightedRetriever` returns a neutral `1.0` for an absent timestamp — pinning the property
  the whole design rests on, so a future change cannot quietly remove it.

Existing tests assuming a non-null `DateTime` (`MetadataBehaviorCreatedAtTests`,
`TimeWeightedRetrieverTests`) are updated as a deliberate consequence, not adjusted until green.

## 6. Out of scope

- **Populating connector timestamps** — its own phase, per §4.
- **Any change to `ReservedMetadataKeys`.** `created_at` stays reserved: the eventual fix routes
  real values through a typed field, not the tag channel, so the guard stays useful.
- Adding a separate `ModifiedAt` field. Most connectors expose *modified* rather than *created*,
  which is a real modelling question — but it belongs with the connector work, where the values
  actually arrive.
