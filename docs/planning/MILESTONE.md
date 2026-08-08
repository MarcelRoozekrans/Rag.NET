# Milestone 4: Release Readiness

**Status:** active
**Started:** 2026-08-02

## Goal

Make Rag.NET shippable — CI, NuGet publishing, first-class configuration, logging, telemetry,
and runnable samples — and prove that what ships works, which the first half of this sentence
cannot do on its own: a green build has now been watched to coexist with four live defects.

Completed milestones are archived under `docs/planning/milestones/`. (Milestone 3 was archived
there on 2026-08-03, at Phase 4.1's close — this file should have been rewritten when the
milestone went active on 2026-08-02, and was not; Phase 4.0 closed in the ROADMAP alone.)

> **Replanned 2026-08-02** (`docs/plans/2026-08-02-milestone-4-replan-design.md`): verification
> is this milestone's dominant cost, not a footnote — Phase 4.0 measured **61 of 71 packages at
> `VerifiedBy=unit`**, exercised only against fakes.
>
> **Retitled 2026-08-03 — v1.0 is postponed until after hardening.** This milestone was "Release
> Readiness (v1.0)" and its DoD ended in the tag. Too many defects in this project's record were
> found by measuring something against reality for the first time, so the tag belongs after the
> work that finds them: **Milestone 6: Hardening & v1.0** now carries the recorded-responses
> phase (as Phase 6.1), the recording criterion, and `Release tagged v1.0`. This milestone keeps
> its number, its phases 4.0–4.6 and its remaining gates, and becomes the shipping-readiness
> *work* rather than the release itself. The ROADMAP's Milestone 4 section carries both notes in
> full.

## Definition of Done

Rewritten 2026-08-02 by the replan's §6 — the previous DoD was already fully satisfied while
four defects were live, so every criterion below can be false and something checks it. Amended
2026-08-03 at the v1.0 postponement: the recording criterion and `Release tagged v1.0` moved to
Milestone 6's DoD. The ROADMAP's Milestone 4 section is the authoritative copy; the two must
agree.

- [ ] All planned phases complete (9 of 12 as of 2026-08-06: 4.0, 4.1, 4.3, 4.4, 4.7, 4.8, 4.9,
      4.10, 4.11 — the phase list grew Phase 4.9, created and completed 2026-08-04 to fix the
      `BuildMetadata`/`CreatedAt` defect and correct the wrong "slot, not a phase" estimate that
      had routed it to 4.2, Phase 4.10, created the same day and completed 2026-08-05 — the
      connector-timestamp-threading work 4.9 priced but did not do — and Phase 4.11, created and
      completed 2026-08-06 to fix a `ChunkIndex` uniqueness defect found while documenting
      `IChunkingStrategy.ChunkAsync`, not by a test. Phase 4.3 (scoped/structured logging, PR #48,
      2026-08-05) and Phase 4.4 (OpenTelemetry tracing and metrics, 2026-08-06) closed together off
      the same design and implementation plan — 4.3's own ROADMAP/MILESTONE entries were not
      written when its PR merged, corrected here rather than left silently stale. Phases 4.2, 4.5
      and 4.6 remain pending, so this box stays open. **This count is known stale beyond this
      note**: Phase 4.2 closed 2026-08-08 as Parser Registration Ownership (see the ROADMAP entry —
      the original "Options Alignment & Validation" scope was re-pointed and only partly absorbed;
      the general `IOptions`/`ZeroAlloc.Validation` alignment remains its own open, unscheduled
      debt), and Phase 4.12 (SystemPrompt coverage, issue #56) also closed since this line was last
      updated. A full resync of this count belongs to whichever phase next closes this milestone's
      DoD, not to a documentation-only phase editing one line item)
- [ ] Full solution builds 0 warnings / 0 errors from a clean restore (true on every phase close
      so far, most recently 2026-08-04; the box is ticked at the milestone's close, from a clean
      restore on that day's tree)
- [ ] All test projects passing — **and no test is gated behind a condition nothing satisfies**
      (`TestGateTests`, Phase 4.0). **The gate half holds as of 2026-08-03** (Phase 4.1): both
      `KnownUnsatisfiable` ledgers are empty and every formerly-unsatisfiable gate is satisfiable
      by a fenced procedure in `docs/reference/ci.md` — `ENABLE_OCR` and `RAGNET_TESSDATA` by the
      `-p:EnableOcr=true` source-build procedure, **executed green on 2026-08-03** (the gated
      test's first run anywhere); `RAGNET_DOCINTEL_ENDPOINT`/`_KEY` by the `az` F0 free-tier
      provisioning procedure (written, deliberately not executed — satisfiable is the claim, not
      exercised; the live run is Phase 6.1's). The box stays open on the all-projects half, which
      is checked at the milestone's close. **Corrected 2026-08-04 (Phase 4.8):** the note that
      used to close this criterion — "Phase 4.1's own workflow changes have not yet had a genuine
      GitHub Actions run" — is no longer true; the last criterion below now cites the run that
      made it false. This box stays open regardless, because Phase 4.8's own tree has not itself
      been through Actions yet
- [x] **Every `features.md` Done claim names code that exists** (`FeatureClaimTests`, Phase 4.0;
      **holding as of 2026-08-03**: both false claims corrected at Milestone 3's close,
      `81163af`; `KnownFalseClaims` is empty)
- [ ] **No package declares `VerifiedBy=none`** (the ledger's release gate, Phase 4.0; **failing
      today, honestly**: `Rag.NET.Mcp.Tool` → 4.6, `Rag.NET.Security.AspNetCore` → 4.5)
- [x] CI pipeline builds, tests, and produces NuGet packages (the build-and-test half has been
      green since Phase 3.5; the pack half shipped in Phase 4.1 — `pack-validate` packs every
      package [all 70 at the time; **66** since Phase 4.7's decomposition, 2026-08-04, with
      `ExpectedPackageCount` moved by stated arithmetic], validates them as a failing test
      project and pushes them to a local feed twice on
      every push, with the nuget.org push gated to 6.3. **Ticked 2026-08-04 (Phase 4.8), on the
      evidence this box asked for rather than the wiring:** PR #18 — Phase 4.1's own branch — ran
      `ci.yml` for real and gated its own merge on it: `commitlint`, `pack-validate` and both
      `build-test` legs all green (run **30828032049**, 2026-08-03). Every push to `main` since
      has run the identical pipeline for real, including the case this repository's own record
      predicted would eventually happen: the Qdrant `SearchAsync` break went red on a genuine
      `build-test` run on `main` (**30919869612**, 2026-08-04, no commit involved) and the fix
      went green on the next one (**30926805555**). The pipeline has now executed, repeatedly,
      against real pushes. What it does **not** cover: Phase 4.8's own tree has never itself been
      through Actions — that gap moves to the all-projects criterion above]

## Phases

1. Phase 4.0 — Verification Ledger and Claim Agreement [complete — 2026-08-02] — three mechanical
   guards that make this DoD falsifiable, and the numbers they produced: `FeatureClaimTests`
   (all Done sections parse, 0 of 73 false positives, exactly two false claims found — both since
   corrected), `TestGateTests` (29 gating sites enumerated, 4 satisfiable nowhere at the time —
   all four since made satisfiable or closed by 4.1), and the `<VerifiedBy>` ledger (71 packages:
   `unit` 61, `container` 8, `recorded` 0, `live` 0, `none` 2). Full entry in the ROADMAP.
2. Phase 4.1 — NuGet Packaging & Publishing [complete — 2026-08-03] — the pipeline packs,
   validates and genuinely pushes all **70** packages on every push; only the nuget.org push is
   gated (to 6.3), recorded and pinned like every other gate. **The plan's own premise was
   falsified by its first measurement:** the design predicted missing licence/README/description
   would fail the build as `NU5xxx` under warnings-as-errors — measured: **the SDK enforces no
   package metadata at all** (missing licence/authors/URLs/tags emit nothing, a missing README is
   a codeless advisory, a missing description silently ships as the literal "Package
   Description"), so `Rag.NET.PackageValidation.Tests` is the only guard, not a second one.
   Before the phase: no licence, project URL, repository or tags in any nuspec, 71 missing-README
   advisories, three packability defects (Whisper natives colliding into the audio package,
   `Rag.NET.Mcp.Tool` silently unpackable under the Web SDK, samples and benchmarks packing into
   every solution pack). Versioning is GitVersion (measured: `0.1.0-preview.1495` on `main`, a
   `v1.0.0` tag derives a stable `1.0.0` with no config change, and GitVersion 6's
   `ContinuousDeployment` mode *strips* the prerelease label — the trap is recorded in
   `GitVersion.yml`); release-please is gated dispatch-only and genuinely unexercisable before
   6.3; commitlint lints PR ranges only (measured against all 1,506 commits: stock rules reject
   184, tuned rules 70, none newer than 2026-07-29); renovate is inert until the app is enabled.
   All eight routed debts closed — five moved whole to the ROADMAP's Closed list, three closed
   by annotation on entries held open for their other halves (the Azure `RAGNET_DOCINTEL_*`
   live run → 6.1, `docs.yml` → 4.5). Residuals recorded on the
   phase entry: the 6.3 push residual, the never-run workflow changes, the DOCINTEL
   satisfiable-but-never-run gap, feature-branch prerelease numbering, and the XML-documentation
   blocker this phase did **not** take up (recorded as a new debt, not absorbed).
3. Phase 4.7 — Package Decomposition, Consolidation & Per-Package READMEs [complete —
   2026-08-04; created mid-milestone out of Phase 4.1's residue ("70 packages a user cannot
   choose between"), numbered after 4.6, executed between 4.1 and 4.2] — **core's transitive
   closure fell 49 → 28, measured at every step** (`dotnet list package --include-transitive`;
   the `.nupkg` sizes were never the problem — the weight was transitive, and 31 of the 43
   packages a consumer downloaded served features behind an explicit opt-in). Three opt-in
   clusters extracted with their builder methods (`Rag.NET.Storage.Sqlite`,
   `Rag.NET.Resilience`, `Rag.NET.Caching` — the last a reference swap, since `HybridCache`
   lives in `Caching.Abstractions`), three satellite families merged (`Parsers.Office`,
   `DataProviders.Microsoft365`, chunking folded into `Rag.NET.Chunking`): **70 → 66 packages,
   measured by packing**, both shapes enforced from the shipped nuspecs by
   `DependencyClosureTests` (both guards proven red first). **One deliberate behaviour change**,
   owner-decided 2026-08-04: `UseCostBudgeting()` now defaults to `InMemoryCostLedger`, so
   spend limits reset on process restart where they previously persisted —
   `UseSqliteCostLedger()` restores persistence, and a registration warning makes the default
   visible. **One public-API addition the design said it would not make**:
   `IVectorStoreDecorator` in Abstractions, sparing every Memory consumer a measured 14-package
   resilience closure. Task 10 (Templates parsers) was **stopped** — dependency cycle,
   `Chunking.Templates` still ships MimeKit/CsvHelper/ClosedXML — and the tokenizer extraction
   **cancelled after measurement** (core hard-references `QueryTechniques`, which pulls the
   tokenizers independently); both recorded, the first routed. All 66 packages ship their own
   README behind `PackageReadmeTests`, the repository's first doc-snippet verification
   (reflection over every C# fence; semantics stay unchecked, full compilation recorded as
   later strengthening) — writing them surfaced five members the data-providers guide documents
   that do not exist (READMEs correct; the guide routed → 4.5). `docs/guide/choosing-packages.md`
   answers "what do I install?" with the SharePoint + Qdrant two-choices example. The Mcp.Tool
   19 MB question is explained by measurement (a `PackAsTool` package ships its dependency
   closure; now 1.87 MB) with the residual confirmation → 4.6. Full entry in the ROADMAP.
4. Phase 4.8 — Dependency Pinning & Renovate [complete — 2026-08-04; created out of `main` going
   red with no commit pushed to it, numbered after 4.7, executed last] — **99+1 = 100 packages
   pinned in a new `Directory.Packages.props`**, ending a defect where a floating
   `PackageReference` resolves at pack time and freezes into the published nuspec as a floor
   nobody chose: `Qdrant.Client 1.*` floated to 1.18.1 overnight, deprecated `SearchAsync`, and
   took `main` red with no commit involved (fixed separately, PR #20). **497 `PackageReference`
   entries stripped across 131 `.csproj`**, plus 6 more in `Directory.Build.props` the plan's own
   count missed — both re-verified here by diff. `PrivateAssets`/`ExcludeAssets` survived
   untouched (78 occurrences, byte-identical before and after). Zero `VersionOverride`. **The
   phase's actual evidence, re-run independently rather than taken on trust:** every produced
   nuspec's external dependency lines, diffed against a pre-edit baseline, came back
   byte-identical over 156 lines. The standing guard (`DependencyPinningTests`) found `Tesseract`
   had no central pin at all — it sits behind an OCR build flag no default restore resolves —
   confirmed by NU1010 and fixed. `renovate.json` gained batched-weekly non-major PRs and
   one-PR-per-major (still inert; the app is not enabled), documented in `docs/reference/ci.md`
   with the two claims — pinning delivered and provable, upgrade automation configured and
   unexercised — recorded separately. `RepoConventions` 33+1 → 36+1. Full entry in the ROADMAP.
5. Phase 4.9 — Provider Creation Time [complete — 2026-08-04; created out of the
   `BuildMetadata`-drops-`CreatedAt` debt open since Phase 2.2, numbered after 4.8, executed next]
   — `DocumentMetadata.CreatedAt` defaulted to `DateTime.UtcNow` and nothing on the
   provider-ingestion path set it, so every provider-ingested document scored as ingested-now
   under `TimeWeightedRetriever`: not a missing value, a wrong one asserted confidently. **The
   debt's own Phase 4.2 routing was wrong, and the evidence was already on file**: the design doc
   it cited already said a connector's real timestamp cannot reach `CreatedAt` — only a tag. Four
   measured reasons confirm it: `baseMetadata` is per-call, not per-document; no production
   caller sets it; `FileEntry` carries no timestamp field; `created_at` is reserved and a
   connector emitting it is blocked with `ReservedMetadataKeyException`. `CreatedAt` is now
   `DateTime?` with no default (breaking change to an unpublished type, no shim needed);
   `MetadataBehavior` writes `created_at` only when set; `BuildMetadata` now forwards
   `baseMetadata.CreatedAt` (a real but batch-level fix, stated as such, not oversold as a
   per-document one); `TimeWeightedOptions.FallbackMetadataKeys` — built, defaulted to `[]` —
   now defaults to `["updated_at", "published_at", "lastmod", "received_at"]`, verified against
   connector source (Asana, Jira, Notion, Zendesk tickets+articles, RSS, Sitemap, Exchange),
   correcting the design's claim that Linear is covered — it is not: `updatedAt` feeds only
   Linear's `ETag`/delta watermark, never a chunk tag. The absent-timestamp-neutral decay (`1.0`)
   the whole design rests on is now pinned by test, proven able to fail by mutation. Corrected two
   false statements in `docs/guide/retrieval.md` (`FallbackMetadataKeys` defaulting to `[]`,
   `CreatedAt` defaulting to `DateTime.UtcNow`); while already in `docs/guide/data-providers.md`,
   also fixed the five members Phase 4.7 found and routed there (Slack `ChannelId`, Gmail
   `UserName`, Confluence `SpaceKey`, GitLab/Bitbucket `Ref`), each re-verified against the option
   class rather than copied from the routed debt's table. **What this phase does not fix, priced
   into Phase 4.10 rather than left a slot a second time:** of 25 providers, 17 hold a real
   timestamp and discard it, 4 more (Confluence, Jira, Box, GoogleDrive) do not even fetch it, 4
   genuinely have none. `Rag.NET.Tests` 1151 → **1159**; `RepoConventions` unchanged 36+1;
   `DataProviders.Tests` **69**. Full entry in the ROADMAP.
6. Phase 4.10 — Connector Timestamp Threading [complete — 2026-08-05; created 2026-08-04 out of
   Phase 4.9's own measurement] — `DocumentMetadata` gains `UpdatedAt` beside `CreatedAt` (both
   `DateTime?`, neither backfilled from the other), threaded through `FileHandle`/`FileEntry` and
   `FileContentProviderBase` for the ~17 providers that held one and discarded it, plus Confluence,
   Box and GoogleDrive's DTO mappings widened for the 3 that did not even fetch it. `updated_at`
   joins `created_at` as a reserved key, and its five hand-writers (Asana, Jira, Notion, Zendesk
   Tickets, Zendesk Articles) were migrated to the typed field in the same commit that reserved it
   (`3a9fdb7`), proven red first by reinstating Asana's hand-written line.
   **Three planning documents were wrong in the same way, corrected here:** Phase 2.2's "Recorded,
   not fixed" section priced this widening as needing re-recorded WireMock cassettes; Phase 4.9's
   design repeated the price; this phase's own design repeated it again. **No connector touched
   here uses WireMock** — Confluence's fixtures are inline JSON literals, GoogleDrive's a fake HTTP
   handler, Box's tests call `ToHandle` directly with no HTTP layer at all. Corrected on Phase
   2.2's own entry too, not left to keep propagating. Also corrected: this phase's own design said
   eight `updated_at` hand-writers where it was five (the other three wrote `published_at`,
   `lastmod` and `received_at` — different keys, still unreserved); Jira never needed DTO work,
   already requesting `fields=…,updated`; Confluence needed no `expand` widening, since the default
   expand already returns `version.when`. **A house pattern surfaced twice**: EPS05 wants `in` on a
   `DateTime?` parameter, RCS1242 forbids `in` on that same non-readonly struct — Box and
   GoogleDrive both resolve it by passing the source object instead of two scalars. **One honest
   gap**: GoogleDrive's fourth field mask (delta pagination's second page) has no dedicated test,
   because nothing tests that path at all — recorded as debt, not implied covered. **What stays
   open**: GitHub, GitLab, WebCrawler and (after investigation) Bitbucket rank neutrally with no
   vendor timestamp to give — correct, not a gap; Slack's and Teams' `date` tags stay
   day-granularity, normalising them was out of scope. `docs/guide/retrieval.md`'s time-weighting
   section rewritten for the `UpdatedAt → CreatedAt → FallbackMetadataKeys → neutral` order;
   `docs/guide/data-providers.md` gains a Timestamps section and an eighth reserved key. Counts:
   `Rag.NET.Tests` 1159 → **1169**, `DataProviders.Tests` 70 → **71**, `RepoConventions` 36+1 →
   **37+1**, `Microsoft365.Tests` 70 → **74**, `Confluence.Tests` 20 → **21**, `Box.Tests` 13 →
   **15**, `GoogleDrive.Tests` 10 → **13**. Full entry in the ROADMAP.
7. Phase 4.11 — Chunk Index Collision Fix [complete — 2026-08-06; created the same day out of a
   documentation pass, not a test, numbered after 4.10, executed next] — `TextChunk.ChunkIndex`'s
   own documentation says it "must be unique within a document"; nothing enforced it.
   `ParseBehavior.ChunkPerSectionAsync` called `ChunkingStrategy.ChunkAsync` once per section, and
   every built-in strategy numbered chunks from a counter local to that one call, so indices
   restarted at 0 per section — the default path, since `RecursiveChunkingStrategy` implements
   `IChunkingStrategy`, not `IDocumentChunkingStrategy`. Colliding `(DocumentId, ChunkIndex)` keyed
   seven identity sites: `DeterministicChunkId.Derive` (Qdrant, Weaviate — **one chunk silently
   overwrote another at write time**), `MultiQueryBehavior`, `RrfMerger`, `DeepResearchRetriever`,
   `FederatedVectorStore` dedup (**unrelated chunks merged at read time**), `ParentChunkKeyHelper`
   and `RagPipelineReindexExtensions`. **Verified, not assumed, that the sibling
   `ChunkDocumentAsync` branch was unaffected**: all twelve `IDocumentChunkingStrategy`
   implementers in `src/` keep a single document-wide counter; none restarts per section. Fixed
   with one renumbering line in `ParseBehavior.ChunkPerSectionAsync`
   (`ctx.Chunks.Add(chunk with { ChunkIndex = documentChunkIndex++ })`) — deliberately not in any
   chunking strategy, since a strategy sees one section and cannot know its document offset, and
   `IChunkingStrategy` is a public extension point. Two pre-existing tests asserting chunk
   *instance* identity (not value) broke as a direct, reported-not-silently-fixed side effect of
   copying every chunk via `with`, and were left unmodified per the plan. New pinning tests at
   both corrupted consumers: `DeterministicChunkId.Derive` now proven to produce distinct GUIDs
   for distinct indices, and `RrfMerger.MergeMany` proven to keep two different chunks from
   different sections as two separate results. `PipelineIngestorChunkingValidationTests` — the
   existing test in the same area — validates `ChunkingOptions` rejection only, never chunk output,
   which is why it never caught this. **Existing data was already corrupt, not newly lost**: ids
   change with the renumbering, so re-ingestion is needed, but colliding indices already meant
   later-section chunks were overwriting or merging with earlier ones before this fix. Counts:
   `Rag.NET.Tests` 1172 → **1179** (7 new tests), **1177 passed, 2 failed** (the two identity tests
   above, left unmodified); `RepoConventions` unchanged 37+1. Full entry in the ROADMAP.
8. Phase 4.3 — Structured Logging Enrichment [complete — 2026-08-05, PR #48] — measurement moved
   the phase before any code did: structured logging was already ~92% done (140 `[LoggerMessage]`
   source-generated declarations, 12 structured templates, **zero** string interpolation), so
   there was no cleanup pass to run. Scoped logging did not exist at all (`BeginScope` appeared
   zero times). `PipelineIngestor.IngestAsync`, `PipelineRetriever.RetrieveAsync`, and
   `RagPipeline.AskAsync`/`AskStreamingAsync` each gained one scope carrying `document_id` or
   `query_hash` (the SHA-256 hash already used for the `query.hash` span tag, never the raw query
   text), proven with `FakeLogger<T>`. Every pre-existing `[LoggerMessage]` declaration across 13
   package families gained an explicit snake_case `EventName` (`ingest_failed`, not the
   PascalCase method-name default). **The trap this avoided**: `EventId.Id` is a deterministic
   hash of `EventId.Name`, so adding `EventName` without also pinning `EventId` would have
   silently renumbered every one of the 139 pre-existing event ids — anyone filtering logs on a
   numeric id would have gone dark with no error. Every declaration got an explicit
   `EventId = <the value the generator already produced>` alongside its new name, verified by
   rebuilding with `EmitCompilerGeneratedFiles` and diffing the generated `EventId(...)` calls
   before and after each commit — zero numeric changes. `Rag.NET.Tests` 1169 → 1172 (three scope
   tests). Full entry in the ROADMAP.
9. Phase 4.4 — OpenTelemetry Tracing & Metrics [complete — 2026-08-06] — planned jointly with 4.3
   off the same design and implementation plan (`docs/plans/2026-08-05-observability-*`). The
   2026-04-04 deferral of package-specific spans ("until evidence demands it") was overruled by
   the owner: that wording was written when the library was a fraction of its current size, and a
   user seeing slow retrieval could get only one generic `ragnet.retrieve` span with a
   `vector_store` tag holding a type name — unable to tell whether the store, the reranker, or
   graph traversal was the cost. `ZeroAlloc.Telemetry` (an in-house source-generated
   instrumentation library) was evaluated and rejected **with evidence**: it cannot set span tags
   at all, and this phase exists for the tags. Its probe still paid for itself — it validated
   cross-assembly `ActivitySource` name-sharing before the real mechanism was built, and measured
   `StartActivity` as zero-allocating when unobserved (bare 72 B, decorator 144 B, generated proxy
   144 B), confirming spans placed directly in existing methods are cheaper than any proxy. The
   shared `"Rag.NET"` `ActivitySource` moved to `src/Shared/RagTelemetrySource.cs`, linked (not
   referenced) into core and all nine newly-instrumented satellites: the six vector stores
   (`ragnet.vectorstore.{upsert,search,delete}`), both rerankers (`ragnet.rerank`), Graph,
   GraphRag, Raptor, and Security. `gen_ai.*` landed on the LLM surface pinned to **GenAI semconv
   v1.41.0** — the last tag before the spec moved to an as-yet-unreleased repository, every
   attribute `Development`-stability, `gen_ai.provider.name` used in place of the deprecated
   `gen_ai.system`. `top_k`/`vector_store` were renamed to `top.k`/`vector.store`, the two
   snake_case outliers. `Rag.NET.Telemetry` (`AddRagNetInstrumentation()`) registers the shared
   source, **both** meters (`"Rag.NET"` and the previously-undocumented `"Rag.NET.Evaluation"`
   `ShadowTelemetry` meter a hand-wired `AddMeter("Rag.NET")` would silently miss), and the
   `telemetry.distro.*` resource attributes — package count **66 → 67**. Three packages stayed
   deliberately uninstrumented: `Caching` (registration only; the cache logic lives in core),
   `RaptorRetrievalBehavior` (a no-op in its default mode), and `IQuerySanitiser` (runs before
   `ragnet.query` opens, so it has no core span to nest under) — a span restating its parent's
   cost is worse than none. `TestProjectTierTests` caught a real defect mid-phase: the new
   env-gated ONNX reranker telemetry test read secrets without declaring
   `<RequiresSecrets>`, which would have made it skip silently forever in CI nightly runs (fixed,
   `90afc456`). **Closing pass (this task)**: `docs/reference/opentelemetry.md`'s metrics table had
   already drifted once — it listed 8 of `RagTelemetry`'s 11 instruments, predating
   `ragnet.ratelimit.wait.duration`, `ragnet.llm.tokens`, and `ragnet.llm.cost` (`features.md` had
   the correct 11 all along). Fixed, documented the satellite spans/tags, both meters, resource
   attributes, and `AddRagNetInstrumentation()` as the recommended setup; added
   `RagTelemetryMetricsDocumentationTests` to `Rag.NET.RepoConventions.Tests` asserting the doc
   table against `RagTelemetry.cs` directly, proven red by deleting the `ragnet.llm.cost` row and
   reverted. **No sample Grafana dashboard shipped** — 4.4's own roadmap description promised one,
   but this environment has no Docker, Grafana, Prometheus, or `promtool` available to validate a
   dashboard JSON before committing it, and an unvalidated dashboard that looks authoritative is
   worse than none; recorded as debt rather than shipped untested (see the ROADMAP entry). Full
   entry in the ROADMAP.
10. Phase 4.2 — Parser Registration Ownership [complete, 2026-08-08] (retitled from "Options
    Alignment & Validation" — see the ROADMAP entry for the re-scoping and the debt this leaves
    open)
11. Phase 4.5 — Sample Applications [pending]
12. Phase 4.6 — Rag.NET CLI Tool [pending]

## Explicitly not in scope

- **The v1.0 tag, the recorded-responses work and the unit-only floor** — Milestone 6
  (Phases 6.1–6.3), created 2026-08-03 when v1.0 was postponed until after hardening.
- **Evaluation depth** (cost comparison, multi-hop, graded datasets) — Milestone 5.
