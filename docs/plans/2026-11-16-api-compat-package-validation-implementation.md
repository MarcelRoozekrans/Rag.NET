# API compatibility check in pack-validate — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pack-validate` and `publish-nuget` fail on any public-API break against each package's
last stable release on nuget.org. An intentional break passes only with a per-major suppression
file and a `!` in the PR title.

**Architecture:** A bash script looks up each package's baseline on nuget.org and writes a
generated MSBuild file. `Directory.Build.targets` imports that file when a property names it,
which turns on the SDK's package validation per package with a per-major suppression file. A
second bash script, run by its own small workflow, ties suppression-file changes to a breaking
PR title. Both scripts are executed by xunit tests, as `CommitMessageHookTests` does for the
commit hook.

**Tech Stack:** bash with curl, grep and sed; no jq in the scripts, so they also run in Git Bash
on Windows. MSBuild, the .NET SDK's package validation, GitHub Actions, xunit v3.

**Spec:** `docs/plans/2026-11-16-api-compat-package-validation-design.md`

## Global Constraints

- Baseline: the highest **stable** version on nuget.org that sorts strictly below the version
  being packed. A stable `S` is below a packing version `P` exactly when `S`'s
  `major.minor.patch` is numerically lower than `P`'s. That holds whether `P` is a prerelease or not.
- "No baseline" only when the id has no stable version at all: an HTTP 404, or a list with no
  stable version. An id that has stable versions, none of them below `P`, fails. Any other HTTP
  failure fails.
- Suppression file: `CompatibilitySuppressions.<major>.x.xml` beside the `.csproj`, where
  `<major>` is the baseline's major. `ApiCompatSuppressionFile` is an **item**; set as a property it
  is silently ignored.
- Property that names the generated file: `RagNetPackageBaselines`. Generated file:
  `artifacts/package-baselines.props`. Pass it to MSBuild as an **absolute** path; a relative one
  resolves per project.
- Without `RagNetPackageBaselines`, builds behave exactly as today.
- PR title rule: `^[a-z]+(\([^)]*\))?!: ` is breaking. It is required when a
  `CompatibilitySuppressions*.xml` file is added, modified, renamed or copied. A removal needs no
  `!`. The hook errors on a default-named `CompatibilitySuppressions.xml` while validating.
- No strict mode. `pack-validate` stays unconditional, and `WorkflowWiringTests` asserts that.
- Commits: conventional, header at most 100 characters including a later ` (#NNNN)`, no nested
  parentheses in bodies, and no Co-Authored-By trailer or session links. Commit with
  `-c user.name="Marcel Roozekrans" -c user.email=marcel@roozekrans.nl`.
- On this host, run tests with `DOTNET_USE_POLLING_FILE_WATCHER=1`.

## Review Focus

1. **A transient network error during lookup.** The step must fail, not skip the package. Covered
   by Task 1's 500 test.
2. **A package added in the same PR.** It is not on nuget.org, so it is skipped and named, and
   `pack-validate` stays green. Covered by Task 1's 404 test and Task 3's produced-package
   cross-check.
3. **Re-running the release on a tag** whose version is already on nuget.org. The baseline is the
   predecessor, not the same version, and not a failure. Covered by Task 1's equal-version test.
4. **An id discovery that misses a project,** for example one that sets `<PackageId>`. The package
   would ship unchecked. Covered by Task 3's cross-check that every produced `.nupkg` is either
   validated or listed as unpublished.
5. **A PR that only edits its title to add `!`.** The title check must re-run on `edited`. Covered
   by Task 4's wiring test.

---

### Task 1: Baseline lookup script

**Files:**
- Create: `.github/scripts/package-baselines.sh`
- Test: `tests/Rag.NET.RepoConventions.Tests/PackageBaselineScriptTests.cs`

**Interfaces:**
- Produces: `bash .github/scripts/package-baselines.sh <packing-version> <output-props> <source-root>`.
  It reads package ids from stdin, one per line. The environment variable `NUGET_FLAT_CONTAINER`
  defaults to `https://api.nuget.org/v3-flatcontainer`; tests point it at a loopback server.
  It exits 0 on success and non-zero on failure, with `::error::` lines naming the cause. The
  output file is an MSBuild `<Project>` with, per validated package:

  ```xml
  <PropertyGroup Condition="'$(PackageId)' == 'Rag.NET.Abstractions'">
    <PackageValidationBaselineVersion>1.9.2</PackageValidationBaselineVersion>
  </PropertyGroup>
  ```

  It also writes one `<RagNetUnpublishedPackage Include="<id>" />` item per skipped id. For each
  `<source-root>/*/CompatibilitySuppressions.<N>.x.xml` with `N` below the highest baseline
  major, it prints `::warning::` naming the file for deletion.

- [ ] **Step 1: Write the failing tests.** Serve fixtures from an `HttpListener` on loopback,
  following `StalledBodyServer` in `Rag.NET.DataProviders.Web.Tests` for the shape. Run the
  script through bash, as `CommitMessageHookTests.FindShell` does. Read both streams
  asynchronously and bound the wait, as `BuildGuardTests.RunTarget` does. Ids are lowercased in
  the URL: `<base>/<lowercase-id>/index.json`. Tests:
  - `PicksTheHighestStableVersionBelowThePackingVersion`: versions `["1.9.0","1.9.2","1.10.0-preview.1"]`
    with packing version `1.9.3-pr.4` gives `1.9.2`.
  - `APublishedVersionEqualToThePackingVersionIsNotItsOwnBaseline`: `["1.9.1","1.9.2"]` with
    `1.9.2` gives `1.9.1`.
  - `AStableReleaseIsValidatedAgainstItsPredecessorAcrossAMajor`: `["1.9.2"]` with `2.0.0` gives
    `1.9.2`.
  - `ANotFoundIdIsListedAsUnpublishedAndSucceeds`: a 404 exits 0. The output has
    `RagNetUnpublishedPackage Include="Rag.NET.New"` and no baseline for it, and stdout names the id.
  - `OnlyPrereleasesCountsAsUnpublished`: `["0.1.0-preview.3"]` is listed as unpublished.
  - `AnIdWithOnlyHigherStableVersionsFails`: `["2.0.0"]` with `1.9.3` exits non-zero, and the
    output names the id.
  - `AServerErrorFailsTheLookup`: a 500 exits non-zero.
  - `AnOlderMajorSuppressionFileIsReportedForDeletion`: with a temp source root holding
    `Foo/CompatibilitySuppressions.1.x.xml` and a baseline of `2.0.0`, stdout contains
    `::warning::` and the file's path. A `CompatibilitySuppressions.2.x.xml` is not reported.

- [ ] **Step 2: Run the tests and confirm they fail.**
  `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`.
  Expect the new tests to fail because the script does not exist.

- [ ] **Step 3: Implement the script.** `set -euo pipefail`. Use `curl -sS -o <tmp> -w '%{http_code}'`
  and branch on the code. Extract the versions with `grep -o '"[0-9][^"]*"'`. Stable means no `-`.
  Compare `major.minor.patch` numerically per field, not with `sort -V` across prerelease labels.
  Write a file header comment saying the file is generated, with its source and the packing version.

- [ ] **Step 4: Run the tests and confirm they pass.** Same command. Expect all of them to pass.

- [ ] **Step 5: Commit.** `feat(ci): look up each package's API baseline on nuget.org`

### Task 2: MSBuild hook

**Files:**
- Modify: `Directory.Build.targets`, adding an import and a property and item group at the top
  with a comment block in the file's style.
- Test: `tests/Rag.NET.RepoConventions.Tests/PackageValidationHookTests.cs`

**Interfaces:**
- Consumes: the generated file shape from Task 1.
- Produces: `-p:RagNetPackageBaselines=<absolute path>`. When that property is set and the
  project's `PackageValidationBaselineVersion` is non-empty:
  - `EnablePackageValidation=true`;
  - `ApiCompatSuppressionOutputFile=$(MSBuildProjectDirectory)/CompatibilitySuppressions.<major>.x.xml`;
  - an `ApiCompatSuppressionFile` item with the same path, only when that file exists.

  The major is `$(PackageValidationBaselineVersion.Split('.')[0])`.

- [ ] **Step 1: Write the failing tests.** Evaluate with
  `dotnet msbuild <project> -getProperty:EnablePackageValidation -getProperty:ApiCompatSuppressionOutputFile -getItem:ApiCompatSuppressionFile -p:RagNetPackageBaselines=<temp file>`.
  The output is JSON, parsed with `System.Text.Json`. Use the process-running shape of
  `BuildGuardTests`. The project under test is a throwaway project the test writes under its own
  output directory, with a fixed `PackageId`. Choose the location so the root
  `Directory.Build.targets` is imported, and assert that it was, so the test cannot pass vacuously.
  Tests:
  - `WithABaselineValidationIsOnAndTheSuppressionFileIsPerMajor`: a baseline of `1.9.2` gives
    `EnablePackageValidation` `true` and an output file ending in `CompatibilitySuppressions.1.x.xml`.
  - `TheSuppressionItemIsAddedOnlyWhenTheFileExists`: no item without the file; with the file,
    exactly one item.
  - `AnOlderMajorFileIsNotPassedToTheSdk`: a `1.x` file present with a baseline of `2.0.0` gives
    no item.
  - `WithoutThePropertyNothingChanges`: `EnablePackageValidation` is empty.
  - `APackageWithoutABaselineIsNotValidated`: the generated file lists another id, so
    `EnablePackageValidation` is empty.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement the hook in `Directory.Build.targets`.** Put the `Import` with
  `Condition="'$(RagNetPackageBaselines)' != ''"` first, then the property group and item group.
  The comment says why the suppression file goes per major, citing the design, and that
  `ApiCompatSuppressionFile` must be an item.

- [ ] **Step 4: Run the tests and confirm they pass.**

- [ ] **Step 5: Spot-check against the real package, without committing anything.** Run
  `printf 'Rag.NET.Abstractions\n' | bash .github/scripts/package-baselines.sh 1.9.3 "$PWD/artifacts/package-baselines.props" src`.
  Then run
  `dotnet pack src/Rag.NET.Abstractions -c Release -o /tmp/pv -p:Version=1.9.3 -p:RagNetPackageBaselines="$PWD/artifacts/package-baselines.props"`.
  Expect exit 0. Rename `IEmbeddingVersionStore`, then repack with `-p:RunAnalyzers=false` and
  expect exit 1 with CP0001. Revert the rename.

- [ ] **Step 6: Commit.** `feat(build): validate packages against the looked-up baseline`

### Task 3: Wire both pack jobs

**Files:**
- Modify: `.github/workflows/ci.yml`, in the `pack-validate` and `publish-nuget` jobs.
- Modify: `tests/Rag.NET.PackageValidation.Tests/WorkflowWiringTests.cs`, including `PinnedPackCommand`.
- Modify: `tests/Rag.NET.PackageValidation.Tests/ProducedPackageTests.cs`

**Interfaces:**
- Consumes: the script from Task 1 and the property from Task 2.
- Produces: a new pinned pack command:
  `dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$PACKAGE_VERSION" -p:RagNetPackageBaselines="$PWD/artifacts/package-baselines.props"`.

- [ ] **Step 1: Update the tests first.**
  - Change `PinnedPackCommand` to the new command. Its existing count of 2 then fails against
    the current `ci.yml`.
  - Add `BothPackJobsLookUpBaselinesBeforePacking`: the lookup command
    `bash .github/scripts/package-baselines.sh "$PACKAGE_VERSION" "$PWD/artifacts/package-baselines.props" src`
    appears twice, and in each job it comes before that job's pack command. Delimit the jobs as
    `ThePackValidateJobRunsUnconditionally` does.
  - In `ProducedPackageTests`, add `EveryProducedPackageWasValidatedOrIsUnpublished`. It reads
    `artifacts/package-baselines.props` with `XDocument`. Every id among
    `artifacts/packages/*.nupkg` must have either a baseline `PropertyGroup` or a
    `RagNetUnpublishedPackage` item. It uses the class's existing skip gate on
    `artifacts/packages`. When the packages exist, a missing props file is a failure, not a skip.

- [ ] **Step 2: Run `tests/Rag.NET.PackageValidation.Tests` and confirm the wiring tests fail.**

- [ ] **Step 3: Edit `ci.yml`.** In both jobs, after the derive step (and, in `publish-nuget`,
  after "Refuse to publish a prerelease"), add a step "Look up the API baselines".
  - It lists the packable project ids with
    `dotnet msbuild <csproj> -getProperty:IsPackable -getProperty:PackageId` over `src/*/*.csproj`.
    Parse with jq, the house style in `ci.yml`, and keep the ids where `IsPackable` is `true`.
  - It pipes the ids into the lookup command, character-identical in both jobs.
  - Then change both pack steps to the new pinned command.
  - Update the comments that say "character-identical" and the one above `PinnedPackCommand`, so
    they mention the baselines.

- [ ] **Step 4: Rehearse the pack-validate sequence locally.** Run the id listing, the lookup,
  the pinned pack with `PACKAGE_VERSION=1.9.3-local.1`, and
  `dotnet test tests/Rag.NET.PackageValidation.Tests -c Release`. Expect the lookup to report 74
  baselines and 0 unpublished, the pack to exit 0 with 74 `.nupkg` files, and all tests to pass.
  The only exception is `EveryPackageCarriesTheVersionGitVersionDerives`, which is known to fail
  locally because of the hand-picked version.

- [ ] **Step 5: Commit.** `feat(ci): check the public API of every package before it ships`. The
  body says this closes the gap from #846.

### Task 4: Breaking-title check for suppression files

**Files:**
- Create: `.github/scripts/check-breaking-title.sh`
- Create: `.github/workflows/api-compat-title.yml`
- Test: `tests/Rag.NET.RepoConventions.Tests/BreakingTitleCheckTests.cs`

**Interfaces:**
- Produces: `bash .github/scripts/check-breaking-title.sh "<pr title>"`. It reads
  `<status>\t<filename>` lines from stdin, using the status values of the GitHub pulls/files API:
  `added`, `modified`, `removed`, `renamed`. It exits non-zero with `::error::` naming the files
  when the rule in Global Constraints is broken.

- [ ] **Step 1: Write the failing tests.** Run the script through bash as in Task 1.
  - `ASuppressionAddedUnderANonBreakingTitleFails`: `added` with `feat(core): x` fails, and the
    output names the file.
  - `ASuppressionAddedUnderABreakingTitlePasses`: `feat(core)!: x` and `feat!: x` both pass.
  - `ARemovedSuppressionNeedsNoBreakingTitle`: `removed` with `chore: drop stale suppressions`
    passes.
  - `AModifiedOrRenamedSuppressionCountsAsAChange`: either fails under `fix: x`.
  - `UnrelatedFilesNeverRequireIt`: `added src/Foo/Bar.cs` under `fix: x` passes.
  - `TheWorkflowRerunsWhenOnlyTheTitleChanges`: parse `api-compat-title.yml` as text with the
    comments stripped. It triggers on `pull_request` with `edited`, `opened`, `synchronize` and
    `reopened`, and runs this script. Its permissions are only `contents: read` and
    `pull-requests: read`. The title reaches the script through an `env:` variable, never as an
    inline `${{ }}` in `run:`.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement the script and the workflow.** The workflow runs
  `gh api "repos/$REPO/pulls/$PR/files" --paginate --jq '.[] | "\(.status)\t\(.filename)"'`
  and pipes it into the script, with `GH_TOKEN: ${{ github.token }}`.

- [ ] **Step 4: Run the tests and confirm they pass.**

- [ ] **Step 5: Commit.** `feat(ci): require a breaking PR title for API compat suppressions`

### Task 5: Docs

**Files:**
- Modify: `docs/reference/ci.md`, adding a section on the API compatibility check.
- Modify: `CONTRIBUTING.md`, adding an intentional-break recipe.

- [ ] **Step 1: Write the `ci.md` section.** Cover:
  - what is compared and against which baseline;
  - why there is a lookup and not a pin;
  - that unpublished packages are skipped and named;
  - that the release packs the same way;
  - the per-major file and the warning;
  - the title workflow.

  Link the design.

- [ ] **Step 2: Write the `CONTRIBUTING.md` recipe.** The exact commands:
  1. the id listing and lookup from Task 3;
  2. `dotnet pack <project> -c Release -p:Version=<next> -p:RagNetPackageBaselines=$PWD/artifacts/package-baselines.props -p:ApiCompatGenerateSuppressionFile=true`;
  3. commit the generated `CompatibilitySuppressions.<major>.x.xml`;
  4. title the PR `feat!:` or `fix!:`;
  5. after the major ships, delete the files that `pack-validate` warns about.

- [ ] **Step 3: Run the docs guards.** `tests/Rag.NET.RepoConventions.Tests` includes the
  documentation guards, and `npm run build` checks the site if `ci.md` is in the docs site.
  Expect both to pass.

- [ ] **Step 4: Commit.** `docs(ci): explain the API compatibility check and intentional breaks`

### Task 6: Whole-branch verification

- [ ] **Step 1: Run every test project.** Enumerate them; don't list them by hand. Exclude
  E2ETests. The known local failures stay excluded and are named in the report:
  `Rag.NET.VectorStores.AzureAISearch.Tests`, whose emulator does not start on this host, and
  `EveryPackageCarriesTheVersionGitVersionDerives`.
- [ ] **Step 2: Run the mutation checks, without committing anything.**
  1. Delete the lookup step from `pack-validate`: Task 3's wiring test fails.
  2. Set the property as `<ApiCompatSuppressionFile>` instead of an item: Task 2's item test fails.
  3. Drop `edited` from the title workflow: Task 4's wiring test fails.

  Revert each one afterwards.

## Checks

- build: `dotnet build Rag.NET.slnx -c Release`
- tests: `dotnet test Rag.NET.slnx -c Release`
