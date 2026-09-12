# The Migration a Dependency Bump Was Hiding — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** opt every test project into Microsoft.Testing.Platform, prove the same tests still run, document the local workflow that changes, and adopt the xunit v4 bump that was blocked on it.

**Architecture:** One property in `tests/Directory.Build.props`. Everything else is verification and documentation. The xunit v4 bump is a separate final commit so it can be dropped without losing the migration.

**Tech Stack:** MSBuild, Microsoft.Testing.Platform, xunit v3 → v4.

**Spec:** `docs/plans/2026-09-12-mtp-migration-design.md` — read it first. Its §3 is the part that affects people rather than machines.

## Global Constraints

- **Issue:** #314. **Phase:** 6.2.41.
- **Conventional commits, header at most 100 characters.**
- **Keep `Microsoft.NET.Test.Sdk` referenced in every project.** Design §2: `ci.yml`'s partition guard counts with `grep -l 'Microsoft.NET.Test.Sdk' tests/*/*.csproj`, so removing it drops projects from the census **while the guard still passes**, because `all` shrinks alongside `fast` and `docker`. The already-migrated project keeps it deliberately and says so in a comment.
- **Do not touch `Directory.Build.targets`.** Its comment explicitly forbids the tidy a migrator is most likely to attempt — rewriting the condition to test `TestingPlatformDotnetTestSupport` — and it is already correct for the post-migration world.
- **Do not rewrite any test.** No xunit API broke. A diff touching a `[Fact]` body means the phase went wrong.
- **The suites are literal commands in this plan, never "the affected suites".** That phrasing failed twice in one week: 6.2.39 skipped `pack-validate` by reasoning a markdown change was safe, and 6.2.40 asserted a test project did not exist. `STATE.md` records the rule — a constraint expressed as a rule gets reasoned around; expressed as a command in a step, it gets executed.

## Commands this plan runs, in full

```bash
# Build once; every test run below is --no-build, as CI does it.
dotnet build Rag.NET.slnx -c Release

# Every test project, one at a time, capturing its count.
for p in tests/*/*.csproj; do
  case "$p" in */Rag.NET.Testing/*) continue;; esac        # shared helper, not a test project
  grep -q 'Microsoft.NET.Test.Sdk' "$p" || continue         # matches ci.yml's own census rule
  printf '%s ' "$(basename "$(dirname "$p")")"
  dotnet test "$p" --no-build -c Release 2>&1 | grep -oE "Passed!.*|Failed!.*|error .*" | tail -1
done
```

Docker must be running for the 12 `RequiresDocker` projects. ~~The three `RequiresLlm` projects pull~~
**Measured 2026-09-12: exactly one project declares `<RequiresLlm>true</RequiresLlm>` —
`Rag.NET.E2ETests`.** The plan said three, from `grep -l "RequiresLlm"`, which also matches the two
projects that merely mention the string in a comment. It pulls ~2 GB of models and is nightly-only —
**skip it and say so in the record** rather than pretending the sweep was total. So the sweep covers
**77 of 78**, not 75.

---

### Task 0: Capture the baseline the migration will be judged against

**Files:** none.

**Nothing in the repository asserts that the same number of tests ran after a runner change** (design
§6). This task is the only thing standing between "the migration works" and "the migration is green".

- [x] **Step 1: Start Docker**, or the 12 Docker-tier projects report a failure that is about the
  daemon rather than the runner.

- [x] **Step 2: Build, then run the loop above and save the output**

```bash
dotnet build Rag.NET.slnx -c Release
# then the loop, redirected:
#   ... > /tmp/mtp-before.txt
```

Put the file somewhere outside the repository — a scratchpad, not `docs/`.

- [x] **Step 3: Record the totals in this file**

Number of projects run, total passed, total skipped, and the `RequiresLlm` project named as
deliberately excluded. **A per-project list is what Task 2 compares against**, so keep the file.

**MEASURED 2026-09-12, on `feat/6241-mtp-migration` before any change, VSTest runner, Docker up:**

| | |
|---|---|
| projects run | **77** of 78 |
| total tests | **5336** |
| skipped | **138** |
| failed | **0** |
| excluded | `Rag.NET.E2ETests` (`RequiresLlm`, nightly-only, ~2 GB of models) |

Per-project output kept at `<scratchpad>/mtp-before.txt`. Every line parsed to a count — no project
produced unrecognised output, which matters because an unparsed line is indistinguishable from a
project that ran nothing.

---

### Task 1: The migration

**Files:** Modify `tests/Directory.Build.props`.

- [x] **Step 1: Add the property**

```xml
  <PropertyGroup>
    <NoWarn>$(NoWarn);MA0004;HLQ005;HLQ012</NoWarn>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
```

Add a comment recording **why**, not what: that the .NET 10 SDK no longer supports the VSTest bridge
for Microsoft.Testing.Platform, that this is what unblocked #314, and that
`Microsoft.NET.Test.Sdk` stays referenced everywhere because both workflows select projects by it.

- [x] **Step 2: Confirm the property actually reaches a project**

Design §4 flags this as an assumption worth checking rather than trusting — the existing precedent
sets it in a `.csproj`, and MSBuild evaluation order is exactly where "should be fine" goes wrong.

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -getProperty:TestingPlatformDotnetTestSupport
```

Expected: `true`. **If it prints blank, stop** — the property is not being imported and nothing below
means anything.

*Measured: `true`.* **But the first attempt failed for an unrelated reason worth recording**: the
explanatory comment contained `‑‑filter`, and **an XML comment cannot contain a double hyphen** —
`error MSB4024`, which fails the whole props file and therefore every test project at once. 6.2.35
hit exactly this ("awkward in a file whose subject is a flag spelled with one") and the lesson did
not transfer, because it was recorded in that phase's notes rather than anywhere this file's author
would look. The comment now names the flag without spelling it.

- [x] **Step 3: Check the already-migrated project**

`Rag.NET.Benchmarks.Quality.IntegrationTests` now sets it twice. **Leave its local setting in place:**
the property is redundant but the comment beside it is the only record of why
`Microsoft.NET.Test.Sdk` stays referenced, and deleting the line to tidy a duplicate would take the
reasoning with it. Add a line noting the shared props file now covers it.

- [x] **Step 4: Commit**

```bash
git add tests/Directory.Build.props tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj
git commit -m "build(tests): opt every test project into Microsoft.Testing.Platform (#314)"
```

---

### Task 2: Prove the tests still run

**Files:** none.

- [x] **Step 1: Rebuild and re-run the full loop**

```bash
dotnet build Rag.NET.slnx -c Release
# the loop again, redirected to /tmp/mtp-after.txt
```

- [x] **Step 2: Diff the two runs**

```bash
diff /tmp/mtp-before.txt /tmp/mtp-after.txt
```

**Expect differences in wording** — MTP's output format is not VSTest's, so the `Passed!` line may
change shape. **Do not expect differences in counts.** Compare project by project:

- a project reporting **fewer tests** than before is the failure this task exists for,
- a project reporting **nothing** is worse, and is what `ci.yml`'s assembly pre-check was built to
  catch under VSTest semantics,
- a project **failing** is a real incompatibility and stops the phase.

- [x] **Step 3: Record the comparison in this file**

Totals before and after, and any project whose count moved, with the reason. **If every count matches,
say so explicitly** — a phase that claims a migration preserved coverage should show the arithmetic.

**MEASURED 2026-09-12. Every count matches exactly.**

| | before (VSTest) | after (MTP) |
|---|---|---|
| projects run | 77 | **77** |
| total tests | 5336 | **5336** |
| skipped | 138 | **138** |
| failed | 0 | **0** |
| projects whose (passed, skipped, total) changed | — | **0** |

**The output format changed and the counts did not**, which is the distinction this task exists to
make. MTP prints `Passed! - Failed: 0, Passed: 5, …` against VSTest's
`Passed!  - Failed:     0, Passed:     5, …`, and reports the framework as `net10.0|x64` rather than
`net10.0`. A comparison keyed on the text would have reported 77 differences; one keyed on the parsed
triple reports none.

`RepoConventions` — the suite holding the tier invariants and `BuildGuardTests`, which covers the
`RAGNET0001` guard whose blast radius this phase widens from one project to all — **101 passed / 2
pre-existing skips, unchanged.**

- [x] **Step 4: Run the guards that know about tiers**

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests --no-build -c Release
```

This is the suite asserting the tier-marker invariants. It also contains `BuildGuardTests`, which
covers the `RAGNET0001` filter guard — **the guard whose blast radius this phase widens from one
project to all of them.**

---

### Task 3: The workflow change that lands on people

**Files:** Modify whichever contributor-facing document explains how to run tests. Find it rather than
assuming:

```bash
grep -rln "dotnet test" --include=*.md . | grep -vE "docs/plans/|docs/planning/|pre-push-review|node_modules"
```

**TASK 3 IS LARGER THAN THE DESIGN THOUGHT, AND PART OF IT IS ALREADY BROKEN.**

`docs/reference/ci.md` is the contributor-facing document (there is no `CONTRIBUTING.md`, and
`README.md` never says `dotnet test`). It contains **five documented `dotnet test … --filter`
commands** — lines ~128, ~213, ~312, ~333, ~354.

**Three of them target `Rag.NET.Benchmarks.Quality.IntegrationTests`**, which has been MTP since
#275 — and the same document says so two hundred lines further down:

> `Rag.NET.Benchmarks.Quality.IntegrationTests` sets
> `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`, so `dotnet test` routes
> through Microsoft.Testing.Platform in-process.

So 6.2.35 shipped a guard that **invalidated three commands in its own repository's CI reference and
did not update them**. They should be erroring with `RAGNET0001` today, before this phase changes
anything. Verify that empirically in Step 1 rather than inferring it — if they do *not* error, the
guard has a gap and that is a bigger finding than this phase.

The remaining two (lines ~128, ~213) target VSTest projects and work today; **this phase is what
breaks them.**

So Task 3 covers three states, not one: commands already broken and undocumented as such, commands
this phase breaks, and the general workflow note.

~~All five need rewriting to the native-runner form.~~ **THERE ARE THIRTEEN, NOT FIVE.** The count of
five came from a `grep … | head -6` read as a complete list — the third truncated-grep error in this
session, and the second in this phase. Enumerated properly:

| Target | Commands | State before this phase |
|---|---|---|
| `Benchmarks.Quality.IntegrationTests` | **11** | **already broken** since #275 made it MTP |
| `Parsers.Pdf.Tests`, `Parsers.Audio.Tests` | **2** | broken *by* this phase |

(`benchmarks.md` carries two more `--filter` flags, but they belong to BenchmarkDotNet, a different
tool, and are untouched.)

**So 6.2.35 invalidated eleven documented commands, not three, and it went unnoticed for a
fortnight.** Verified empirically rather than inferred: with the migration temporarily reverted, a
documented command against that project still fails with `RAGNET0001`.

**The operator chose to fix all thirteen here** over fixing only the two this phase breaks, on the
grounds that shipping a migration while knowingly leaving broken documented commands is the failure
this phase is criticising 6.2.35 for.

**One capability is genuinely gone and is documented rather than papered over.** Three commands used
`--filter "DisplayName~X&DisplayName~<dataset>"` to run a *single BEIR dataset*. Measured 2026-09-12:
neither `-method` wildcards nor the query filter language addresses a theory data row — both select
the theory and run **all** its rows — and the harness reads no dataset-selecting environment
variable. The converted commands say inline that they now run every dataset, because for BEIR that is
a large cost difference and silently changing it would be worse than the filter being gone.

- [x] **Step 1: Establish what now fails, by running it**

```bash
dotnet test tests/Rag.NET.Tests --no-build -c Release --filter "FullyQualifiedName~EnsembleBehaviorTests"
```

Expected: `error RAGNET0001`, naming the native runner and `-class`. **Capture the exact message** —
the documentation quotes it, because a reader who hits it searches for what they saw.

Before this phase that command worked on 77 of 78 projects. After it, it works on none. **That is the
migration's felt cost and the reason this task exists.**

- [x] **Step 2: Document the replacement**

State plainly: `--filter` no longer works with `dotnet test` anywhere in this repository, because
Microsoft.Testing.Platform ignores the VSTest filter property and the repository refuses the silent
version rather than allowing it. Give the native-runner form the guard already prints, with a real
worked example against a real test class.

- [x] **Step 3: Commit**

```bash
git add <the documents changed>
git commit -m "docs(testing): --filter no longer works with dotnet test; use the native runner"
```

---

### Task 4: Adopt the xunit v4 bump

**Files:** Modify `Directory.Packages.props`.

**A separate commit, deliberately, and last.** Design §4 asked for this choice to be made and
justified: the migration and the bump are independent changes with independent failure modes, and if
the bump misbehaves the migration commit still stands and this one can be dropped. Bundling them
would make a revert throw away the part that worked. The bump is also the whole point — migrating
without it leaves #314 open and the value unrealised — so it belongs in this phase rather than a
successor.

- [ ] **Step 1: Take #314's change**

```
xunit.runner.visualstudio  3.1.5 → 4.0.0
xunit.v3                   3.2.2 → 4.0.0
xunit.v3.extensibility.core 3.2.2 → 4.0.0
```

**Check whether `xunit.runner.visualstudio` is still needed at all.** It is the VSTest adapter, and
this phase just stopped using VSTest. If nothing references it, removing it is cleaner than bumping
it — but verify by building, not by reasoning.

- [ ] **Step 2: Build and re-run the full loop a third time**

Same commands, `/tmp/mtp-after-v4.txt`. Compare against Task 2's output. **Any count change here is
xunit v4's doing, not the migration's**, which is exactly why the commits are separate.

- [ ] **Step 3: Commit**

```bash
git add Directory.Packages.props
git commit -m "chore(deps): update xunit-dotnet monorepo to v4 (#314)"
```

---

### Task 5: Review and PR

- [ ] **Step 1: Run every command in the "Commands this plan runs" block one final time**, plus:

```bash
dotnet build Rag.NET.slnx -c Release
dotnet test tests/Rag.NET.RepoConventions.Tests --no-build -c Release
```

and the packaging guard, which invokes `dotnet test` and therefore is affected by a runner change:

```powershell
Remove-Item -Recurse -Force artifacts/packages
$v = dotnet dotnet-gitversion /output json /showvariable SemVer
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$v"
```

```bash
dotnet test tests/Rag.NET.PackageValidation.Tests --no-build -c Release
```

- [ ] **Step 2: `docs/planning/ROADMAP.md`**, the Phase 6.2.41 block — record the before/after totals,
  whether `xunit.runner.visualstudio` survived, and anything the migration broke. **Do not change the
  `[status: ...]` marker.**

- [ ] **Step 3: Close #314 by superseding it.** This phase adopts its change, so comment there pointing
  at the PR and close it — a Renovate PR that has been red for a month should not merge; its content
  ships here with the migration that makes it possible.

- [ ] **Step 4: Run `pre-push-review`.** Record the verdict and report path.

- [ ] **Step 5: Open the PR.** Lead with the developer-facing change, not the property. Record the
  number here.

---

## Self-review

**Spec coverage** — design §5's four in-scope items: (1) opt in → Task 1. (2) verify every project
still runs → Tasks 0 and 2. (3) document the workflow change → Task 3. (4) adopt the bump and close
#314 → Tasks 4 and 5 Step 3. Out-of-scope items are enforced by Global Constraints: no test rewrites,
no touching the filter guard, no tier changes.

**The design's open question is answered** — §4 asked whether the bump belongs in this phase. It does,
as the final separate commit, for the reason stated in Task 4.

**Placeholder scan** — Task 3's file list is a `grep` rather than a named file, because the
contributor-facing testing documentation has not been located and guessing a path is how 6.2.40's plan
claimed a test project did not exist.

**Known weakness** — one `RequiresLlm` project (`Rag.NET.E2ETests`) is excluded from every sweep,
because it pulls ~2 GB of models and is nightly-only. So the phase ships having verified **77 of 78**
projects under MTP, and the record must say so rather than claiming a complete sweep. The nightly run after merge is
what covers the remainder, and it is worth watching rather than assuming.
