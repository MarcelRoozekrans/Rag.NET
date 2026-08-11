# Milestone 4 — Release Readiness: audit

**Date:** 2026-08-11
**Verdict:** **PASS** — all six definition-of-done criteria verified.

Run because `docs/planning/MILESTONE.md` still declared Milestone 4 `active` while
`docs/planning/ROADMAP.md` recorded it `complete` and Milestone 5 already had two phases done.
Two sources of truth disagreeing, one silently stale — the same shape as the changelog that
claimed a release no tag ever matched, corrected earlier the same day.

The ROADMAP is the authoritative copy of the DoD, as `MILESTONE.md` itself states. Every
criterion below was checked by running something, not by reading its checkbox: the boxes were
already ticked, so re-reading them would have verified nothing.

## Criteria

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | All planned phases complete | PASS | 13 of 13 Milestone 4 phases carry `[status: complete]` in the ROADMAP: 4.0–4.12 |
| 2 | Full solution builds 0 warnings / 0 errors from a clean restore | PASS | Every `obj/` and `bin/` deleted, `dotnet restore` then `dotnet build -c Release`: **0 Error(s), 0 Warning(s)**, exit 0 |
| 3 | All test projects passing, and no test gated behind a condition nothing satisfies | PASS | **72 suites, 0 failures.** `TestGateTests` (18 cases) is in the run and green |
| 4 | Every `features.md` Done claim names code that exists | PASS | `FeatureClaimTests` in `RepoConventions.Tests`, green in the same run |
| 5 | No package declares `VerifiedBy=none` | PASS | 0 found across `src/*/*.csproj` — 62 `unit`, 9 `container` |
| 6 | CI pipeline builds, tests, and produces NuGet packages | PASS | `build-test`, `pack-validate`, `publish-nuget` all present in `ci.yml`; the 2026-08-11 dispatch succeeded and pushed 70 packages and 70 symbol packages to nuget.org with 0 errors |

## Two false failures, and why they are recorded rather than omitted

The first two attempts at this audit reported failures that were **artefacts of how the audit was
run**, not defects. Both are written down because a reader comparing this report against those
logs would otherwise conclude the verdict was massaged.

**A file-lock warning that looked like criterion 2 failing.** The first build reported
`1 Warning(s)`: `MSB3026 — could not copy apphost.exe … being used by another process`. The cause
was a `dotnet test` run started against the same tree while the build was still going, and then
killed; its processes still held the file. After `dotnet build-server shutdown` and a clean
rebuild: 0 warnings. The criterion was never in doubt; the measurement was.

**Package-validation failures that looked like criterion 3 failing.** `PackageValidation.Tests`
reads `artifacts/packages`, so its result depends on what was last packed there. It failed twice
for two different reasons of the same kind: first against stale `.nupkg` files from a feature
branch (`0.1.0-feat-redis-vector-store.1`) while GitVersion derived `0.1.0`, then against an
empty directory when the audit script's GitVersion call returned an empty string and
`dotnet pack -p:Version=""` produced nothing. Packed at the correctly derived `0.1.0`:
**22 of 22 green**.

That the suite is sensitive to local state is not a defect — it is the guard working. In CI,
`pack-validate` packs immediately before it tests, which is why the same suite passed on the
release run today.

## What this milestone shipped, verified beyond the checkboxes

- **70 packages published to nuget.org at 0.1.0**, all live and installable, pushed through
  Trusted Publishing (OIDC) rather than a stored key — on that mechanism's first ever execution.
- **`v0.1.0` tagged**, with a GitHub release, and GitVersion returning a stable `0.1.0` on the
  tagged commit rather than a prerelease.
- **The packaging surface now gates a merge.** `pack-validate` and `commitlint` were added to the
  `Main` ruleset's required checks on 2026-08-11, read back through the API rather than the
  settings page. Until then the only guard on 70 packages could go red without blocking anything.

## Follow-ups, none of which block this milestone

- **`NUGET_API_KEY` is now read by nothing** and should be deleted; a retired credential that
  still works is how these migrations stall. Recorded in `docs/reference/ci.md`.
- **The `Rag.NET` ID prefix is unreserved** — issue #159, deferred with the application drafted.
- **`release-please.yml` is dispatch-only**, for a reason that expired when 0.1.0 shipped: it was
  written so a release PR would not be proposed "months early". Worth switching to a push trigger.
