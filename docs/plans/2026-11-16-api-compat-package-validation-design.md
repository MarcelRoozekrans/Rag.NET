# API compatibility check in pack-validate — design

**Issue:** #846. **Status:** approved 2026-11-16, after a builder check. **Size:** a project, small: one CI step, one MSBuild hook,
one PR check, docs and guards.

## Intent

Rag.NET publishes 74 packages under semver, and release-please picks the bump from commit types.
Nothing checks the public API, so a breaking change committed as `feat:` or `fix:` ships as a
minor or patch release. The next major is already queued (#937, #944), so a check is needed to
keep breaks visible until then and to tell an intentional break from an accidental one.

## Goals

- `pack-validate` fails when a package's public API breaks against the last release published on
  nuget.org: a removed or changed type or member, on either target framework.
- An intentional break passes only with a committed suppression file and a PR title that carries
  the conventional-commit `!`, so the version bump and the API break agree.
- A package that has never been published is skipped without anyone maintaining a list.
- Suppressions never turn `main` red after a major release, and an unneeded suppression within
  one major still fails the pack.

## Non-goals

- Strict mode, which also flags additions. Additions are minor-compatible and need no gate.
- Validating local `dotnet pack` runs by default. Locally the check is opt-in, through the same
  script CI uses.
- Making `pack-validate` a required check. That is the separate branch-protection decision the
  `ci.yml` comments already track.

## Anti-goals

- A gate that turns `main` red because a release is still publishing to nuget.org.
- A gate that passes without checking anything. If the baseline lookup fails, for example on a
  network error, the step fails. It must never skip silently.

## Approaches

Baseline source, decided by the operator on 2026-11-16:

| Approach | Trade-off |
|---|---|
| **nuget.org, per package (chosen)** | For each package id, the highest stable version on nuget.org below the version being packed. New packages skip themselves. During publish lag it validates against the previous release, so one commit is checked a bit more weakly and nothing goes red. Needs network, which `pack` already needs for restore. |
| Last git tag | No nuget query. `main` goes red when a commit lands while the tag is still publishing. Needs a manual opt-out per new package. |
| Pinned in a props file | Simplest. It lags or races the same way, and it also needs a manual opt-out per new package. |

Intentional breaks, decided by the operator on 2026-11-16:

| Approach | Trade-off |
|---|---|
| **Suppression file plus an enforced `!` in the PR title (chosen)** | The SDK's own `CompatibilitySuppressions.xml` per project, plus a PR check that ties it to a breaking-change title. |
| Suppression file only | Less machinery. A `fix:` with a suppression could still ship as a patch. |
| No suppressions | A break needs the check switched off in the PR. That is a workaround. |

After a major release, decided by the operator on 2026-11-16. A `feat!` PR's suppressions are
written against 1.x. The 2.0.0 release still packs against 1.x, so it passes. Once 2.0.0 is on
nuget.org, every 1.x suppression is "unnecessary" against the new baseline.

| Approach | Trade-off |
|---|---|
| **One suppression file per baseline major (chosen)** | `CompatibilitySuppressions.<major>.x.xml`; only the file for the current baseline's major is passed to the SDK. Older files are inert, and `pack-validate` warns with their names. `main` never goes red over a release. |
| SDK default file name, red until cleanup | Simplest. `main` shows red after every major until a cleanup PR. |
| Delete in the release PR, skip the check on majors | A manual step on every major, and the release itself goes unchecked. |

## What was verified on 2026-11-16

These ran on `Rag.NET.Abstractions` with SDK 11.0.100 against nuget.org's 1.9.2:

- `-p:EnablePackageValidation=true -p:PackageValidationBaselineVersion=1.9.2` passes on current
  `main`. Renaming a public interface fails the pack with CP0001 on both `net10.0` and `net11.0`.
- `-p:ApiCompatGenerateSuppressionFile=true` writes `CompatibilitySuppressions.xml` beside the
  `.csproj`, and the next pack passes with it.
- Once the break is reverted, the leftover suppression fails the pack with "Unnecessary
  suppressions found" and exit code 1. Stale suppressions are therefore an error by default.
- The `.nupkg` is written before validation runs, so a failed validation still leaves a package on
  disk. The step's exit code is what counts, not the file.
- All 74 packable projects under `src/` are on nuget.org at 1.9.2.
- A full `dotnet pack Rag.NET.slnx` with validation against 1.9.2 passes with no errors or
  warnings. All 74 baselines were restored, so the check ran for every package, not just one.
  No per-package exceptions are needed today.
- The imported-file route works: a file imported at the top of `Directory.Build.targets`, setting
  `PackageValidationBaselineVersion` and `EnablePackageValidation` under a `$(PackageId)`
  condition, catches the break. `ApiCompatSuppressionFile` is an **item**, not a property: setting
  it as a property is silently ignored. With the item set to `CompatibilitySuppressions.1.x.xml`
  when that file exists, and `ApiCompatSuppressionOutputFile` set to the same path:
  - generation writes the 1.x file;
  - the break passes with it;
  - the break still fails when the 2.x file is selected instead, so the 1.x file is ignored;
  - with no break, the 1.x file is inert while 2.x is selected;
  - it fails as "unnecessary" while 1.x is selected.

## Design

1. **Baseline lookup.** A script, run in the `pack-validate` job after "Derive the package version",
   lists the packable projects and asks nuget.org's flat-container index, once per id, for its
   versions. For each package it picks the highest stable version that sorts strictly below the
   version being packed, by semver precedence. On a release-tag dispatch that packs 1.9.2, the
   baseline is 1.9.1, the real predecessor. Package ids are read with
   `dotnet msbuild -getProperty:PackageId`, not derived from file names. "No baseline" is allowed
   only when the id has no published stable version at all, which is a 404 or an empty list.
   An id with stable versions, none of them below the version being packed, fails the step. It
   writes a generated MSBuild file under `artifacts/` that sets `PackageValidationBaselineVersion` for each package that has one. A
   package with no published stable version is left out and logged by name; a retired id that
   comes back as a project shows up there. Any HTTP failure other than a 404 fails the step.
2. **MSBuild hook.** `Directory.Build.targets` imports the generated file when a property points
   to it, for example `-p:RagNetPackageBaselines=<path>`, and sets `EnablePackageValidation` for
   each package that received a baseline. Suppressions use the file for the baseline's major:
   `CompatibilitySuppressions.<major>.x.xml` beside the `.csproj`. The hook adds it as an
   `ApiCompatSuppressionFile` item when it exists, and always sets `ApiCompatSuppressionOutputFile`
   to the same path, so `-p:ApiCompatGenerateSuppressionFile=true` writes the right file. The lookup
   step prints a `::warning::` for each suppression file whose major is below the current
   baseline's major, naming it for deletion. Without the property nothing changes, so local
   builds, tests and the matrix legs behave as they do today.
3. **Both pack commands.** `pack-validate` and `publish-nuget` pack with a character-identical
   command, which `WorkflowWiringTests` pins. Both run the lookup and pass the property, so the
   release packs exactly as `pack-validate` checked it. The pins are updated to match.
4. **PR title check.** A small workflow of its own, triggered by `pull_request` with `opened`,
   `edited`, `synchronize` and `reopened`, so a title fix re-runs it without re-running the
   matrix. It reads the PR's own file list from the GitHub API, not a local diff. It fails when a
   `CompatibilitySuppressions*.xml` file (the per-major names and the SDK's default name) is changed in
   any way except a removal and the PR title has no `!` before its colon. The hook also fails
   the build when the default-named file exists, because the SDK would otherwise fall back to it
   for every major. A deleted suppression file, the cleanup after a major, needs no `!`. The workflow
   carries no secrets and only reads the PR. It does not need to be a required check:
   `pack-validate` is not one either, and the operator merges.
5. **Docs.** `docs/reference/ci.md` describes the check. `CONTRIBUTING.md` gives the
   intentional-break recipe: run the script locally, regenerate the suppressions, title the PR
   `feat!:`, and delete the older-major suppression files once `pack-validate` warns about them.
6. **Guards.** The repo's convention tests pin each new piece, such as the step and the property
   wiring, the way they pin the existing pack steps. Mutation-check each: for example, a renamed
   public type must fail `pack-validate`. That run is local, not committed.

## Left to the builder

- Whether the script is bash with jq, the house style in `ci.yml`, or a small C# file.
- The generated file's exact shape and the property name.
- Where the convention tests go: `Rag.NET.PackageValidation.Tests` or `Rag.NET.RepoConventions.Tests`.
