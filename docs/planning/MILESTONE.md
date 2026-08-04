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

- [ ] All planned phases complete (4 of 9 as of 2026-08-04: 4.0, 4.1, 4.7, 4.8 — the phase list
      grew Phase 4.8, created and completed 2026-08-04, out of the Qdrant break)
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
5. Phase 4.2 — Options Alignment & Validation [pending]
6. Phase 4.3 — Structured Logging Enrichment [pending]
7. Phase 4.4 — OpenTelemetry Tracing & Metrics [pending]
8. Phase 4.5 — Sample Applications [pending]
9. Phase 4.6 — Rag.NET CLI Tool [pending]

## Explicitly not in scope

- **The v1.0 tag, the recorded-responses work and the unit-only floor** — Milestone 6
  (Phases 6.1–6.3), created 2026-08-03 when v1.0 was postponed until after hardening.
- **Evaluation depth** (cost comparison, multi-hop, graded datasets) — Milestone 5.
