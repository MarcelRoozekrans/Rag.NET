# Benchmark Filter Guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dotnet test --filter` against a Microsoft.Testing.Platform project stops silently running every test in the assembly, and starts refusing with the command that does work.

**Architecture:** One new root `Directory.Build.targets` defines one target, hooked `BeforeTargets="InvokeTestingPlatform"` and conditioned on `VSTestTestCaseFilter` being non-empty. It raises `error RAGNET0001`. Two tests in `Rag.NET.RepoConventions.Tests` cover it: a behavioural one that invokes the target and asserts the error, and a structural one that asserts the hook attribute the behavioural test cannot reach.

**Tech Stack:** MSBuild (SDK 10.0.401), .NET 10, xunit v3, Microsoft.Testing.Platform.MSBuild 1.9.1.

**Spec:** `docs/plans/2026-09-10-benchmark-filter-guard-design.md` — read it first. Its §0 corrects the issue's arithmetic and its §3 records the decision to go repo-wide against the issue's own proposal.

## Global Constraints

- **Issue:** #529. **Phase:** 6.2.35.
- **Everything here was prototyped and measured on 2026-09-10 before this plan was written.** Every figure below is a real observation, not an estimate. Where a step says "expect X", X was seen.
- **Do not remove `TestingPlatformDotnetTestSupport`.** It fixes #275, where the VSTest adapter deadlocked 2 of 4 runs of `BeirGraphRagAnswerTests` before entering test code. Out of scope, and reopening it is not this phase's call.
- **Do not touch `ci.yml` or `nightly.yml`.** They select by project and pass no filter — verified, not assumed.
- **The condition must NOT check `TestingPlatformDotnetTestSupport`.** Hook `InvokeTestingPlatform`, which exists only when MTP is the runner, so the guard arms for any future MTP adopter however it was enabled. The *message* names the property conditionally; the *condition* does not.
- **`Directory.Build.targets` is new and repo-wide.** It is imported by all ~140 projects. It must contain nothing that fires outside the guarded condition — Task 1 Step 5's control is what proves that.
- **Conventional commits, header at most 100 characters.**
- **Test command:** `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`. Baseline before this phase: **95 passed, 2 pre-existing skips**.

---

### Task 1: The guard, and the behavioural test that proves it fires

**Files:**

- Create: `Directory.Build.targets` (repository root)
- Test: `tests/Rag.NET.RepoConventions.Tests/BuildGuardTests.cs` (new)

**Interfaces:**

- Produces: MSBuild target `RefuseVSTestFilterUnderTestingPlatform`, error code `RAGNET0001`.

- [x] **Step 1: Write the failing behavioural tests**

The harness is `dotnet msbuild` invoking the target **by name**. This was chosen over `dotnet test --filter` after measuring all three options:

| Harness | Time | Needs a prior build? |
|---|---|---|
| `dotnet test --filter` | **44.8 s** | no |
| `dotnet test --no-build --filter` | **0.95 s** | **yes** — unusable, a CI run of `RepoConventions` has not built the benchmark project |
| `dotnet msbuild -t:<target> -p:VSTestTestCaseFilter=X` | **0.535 s** | **no** |

Two tests, both against `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj`:

- `PassingAVSTestFilterToATestingPlatformProject_IsRefused` — run with `-p:VSTestTestCaseFilter=X`. Assert non-zero exit, and that stdout contains `RAGNET0001`.
- `WithNoFilter_TheGuardIsSilent` — run the same target with no `VSTestTestCaseFilter`. Assert `RAGNET0001` does **not** appear.

**The second test is not optional padding.** A guard that fires unconditionally would pass the first test and break every `dotnet test` in the repository.

Assert on the **error code**, not the message text — the message is prose and will be reworded; the code is the contract.

- [x] **Step 2: Run to verify they fail** — the target does not exist yet, so MSBuild reports the target as missing. Confirm the failure is "target not found" and not a harness bug: a test that fails for the wrong reason proves nothing in Step 5.

- [x] **Step 3: Write the guard**

```xml
<Project>

  <Target Name="RefuseVSTestFilterUnderTestingPlatform"
          BeforeTargets="InvokeTestingPlatform"
          Condition="'$(VSTestTestCaseFilter)' != ''">
    <PropertyGroup>
      <_RagNetFilterWhy>Microsoft.Testing.Platform is the test runner for this project</_RagNetFilterWhy>
      <_RagNetFilterWhy Condition="'$(TestingPlatformDotnetTestSupport)' == 'true'">$(_RagNetFilterWhy) (it sets TestingPlatformDotnetTestSupport)</_RagNetFilterWhy>
    </PropertyGroup>
    <Error Code="RAGNET0001"
           Text="'--filter' is silently ignored here: $(_RagNetFilterWhy), and it does not apply the VSTest filter property, so EVERY test in the assembly runs instead of the ones you asked for. Use the native xunit v3 runner, which honours -class, -method and -filter: $(TargetDir)$(AssemblyName).exe -class &lt;fully.qualified.ClassName&gt;" />
  </Target>

</Project>
```

Three things about this that are load-bearing:

- **`$(TargetDir)$(AssemblyName).exe` resolves at build time** into an absolute path, so the reader gets a copy-pasteable command rather than a pattern to reconstruct.
- **The two-line `_RagNetFilterWhy` is the §5 decision.** The first line is always true; the second fires only when the property is genuinely why. Measured both ways: on the benchmark project the message reads `...runner for this project (it sets TestingPlatformDotnetTestSupport), and...`; on a non-MTP project invoked directly it reads `...runner for this project, and...`.
- **A comment must say why the condition does not check `TestingPlatformDotnetTestSupport`.** Without it, the first reader will "tidy" the condition to match the message and silently un-arm the guard for future adopters.

- [x] **Step 4: Run to verify GREEN** — both Task 1 tests pass.

- [x] **Step 5: Run the three controls, and record the numbers**

These are the design's §4 and they are the reason a repo-wide file is safe. **Run all three; do not infer any of them.**

| | Command | Expect |
|---|---|---|
| A | `dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release --filter "FullyQualifiedName~BeirDeepResearchTests"` | `error RAGNET0001`, **no tests run** |
| B | `dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release` | **267 run** (149 passed, 118 skipped), no error |
| C | `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release --filter "FullyQualifiedName~PackageVerificationTests"` | **6 run** (5 passed, 1 skipped), no error |

**C is the decisive one.** It proves the new repo-wide file leaves the other sixty-odd VSTest-adapter projects alone *and* that `--filter` still narrows there. A guard that fired on everything would pass A and B.

- [x] **Step 6: Commit** — `feat(build): refuse a VSTest filter that Microsoft.Testing.Platform will ignore (#529)`

---

### Task 2: The structural test the behavioural one cannot replace

**Files:**

- Modify: `tests/Rag.NET.RepoConventions.Tests/BuildGuardTests.cs`

- [x] **Step 1: Write the failing test**

`TheGuardIsHookedToTheTestingPlatformRunner` — read `Directory.Build.targets` and assert the target carries `BeforeTargets="InvokeTestingPlatform"`.

**Why this is not redundant with Task 1.** The behavioural test invokes the target *by name*, so it proves the condition and the message but **cannot prove the hook wires up**. Deleting `BeforeTargets` would leave every Task 1 test green and the guard completely inert — the exact "reviewed decision with no test" shape 6.2.32 found in Weaviate's corrupt-blob posture, which stood unprotected for six weeks because nothing covered the path.

Assert on the attribute, not on file contents wholesale, so reformatting does not fail the test.

- [x] **Step 2: Run to verify it fails**, then make it pass, then run the whole `RepoConventions` suite — expect **98 passed, 2 pre-existing skips** (95 + 3 new).

- [x] **Step 3: Commit** — `test(build): pin the guard's hook, which the behavioural test cannot reach (#529)`

---

### Task 3: Documentation

**Files:**

- Modify: `docs/reference/ci.md`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj`

- [x] **Step 1: `docs/reference/ci.md`** — a short section where someone reaching for a filter will look. It must carry: that `--filter` is refused on this project and why; the native-runner recipe with `-class`, `-method` and `-filter`; and that the native runner **prints the per-test skip reason that `dotnet test` suppresses**, which is a genuine reason to prefer it beyond filtering.

**Do not use a fenced `csharp` block anywhere in this file.** `EveryDocsCodeExampleResolvesAgainstTheProducedPackages` compiles every fenced `csharp` block under `docs/` against the shipped packages, and it **scans the filesystem rather than git**. Shell recipes belong in `bash` or `text` fences.

- [x] **Step 2: The csproj comment** — the existing comment explains the #275 deadlock the property fixes and is **silent on what it costs**. Add the cost: `--filter` is not applied, and the guard in `Directory.Build.targets` now refuses it. Cross-reference #529.

- [x] **Step 3: Commit** — `docs(ci): say what TestingPlatformDotnetTestSupport costs, not only what it buys (#529)`

---

### Task 4: Mutation sweep

**Files:** nothing permanently — apply, test, revert.

- [x] **Step 1: Run each mutation, record the site AND the named catcher**

**Record the line mutated, not just the description.** 6.2.31's sweep was rendered unreproducible by naming a mutation without naming its site, and 6.2.34's sweep fixed that habit — keep it.

| # | mutation | site | expected catcher |
| --- | --- | --- | --- |
| 1 | delete the `Condition` so the guard always fires | the `Target` element | `WithNoFilter_TheGuardIsSilent` |
| 2 | change `Error` to `Warning` | the task element | `PassingAVSTestFilterToATestingPlatformProject_IsRefused` (exit code) |
| 3 | delete `BeforeTargets="InvokeTestingPlatform"` | the `Target` element | **`TheGuardIsHookedToTheTestingPlatformRunner` only** — Task 1's tests stay green, which is the whole argument for Task 2 |
| 4 | change the condition to `'$(TestingPlatformDotnetTestSupport)' == 'true'` | the `Target` element | **CAUGHT by `WithNoFilter_TheGuardIsSilent` — this row's prediction below was wrong** |

**Row 4's prediction was wrong, and the error is worth naming.** It was called "behaviourally equivalent today". It is not equivalent in any respect: the condition asks *was a filter passed*, the property asks *does this project use MTP*. Swapping them does not narrow or widen the trigger — it makes the guard fire on **every unfiltered run**, which the negative control caught at once. The reasoning conflated a coincidence of *scope* (one project) with equivalence of *meaning*, and they are unrelated. The original note follows, left as written.

~~**Row 4 is the point of this sweep.**~~ It is the "tidy" a future reader is most likely to make, because it makes the condition match the message. Today it is *behaviourally equivalent* — one project sets the property and it is the same project MTP runs — so **every test may stay green**. If it survives, that is not a missing test to write casually: the divergence only appears when a second project adopts MTP, which no test can simulate without inventing one. **Record the survivor and reason about it in the ROADMAP block rather than forcing a test that pins today's coincidence.** That is 6.2.34 row 7's lesson: a survivor that is unreachable is a finding, not a gap.

- [x] **Step 2: Verify the tree is clean** — `git status --short`, only files you meant to add.

---

### Task 5: Correct the issue, then let the merge close it

- [x] **Step 1: Post the measured numbers on #529** — 267 total, 149 passed, 118 skipped from a `--filter` naming one class, against the issue's "~70". Say the scope claim held and was checked. Do this **before** the PR merges, so the correction is on the issue rather than only in the roadmap.

- [x] **Step 2: Do not close it by hand** — the PR closes it.

---

### Task 6: Roadmap, review and PR

- [x] **Step 1: `docs/planning/ROADMAP.md`**, the Phase 6.2.35 block — record what the phase found, in its neighbours' style, including the sweep's row-4 result whichever way it goes. **Do not change the `[status: ...]` marker or add `**Completed:**`** — `complete-phase` does that after the merge.

- [x] **Step 2: Run the suites.** `Rag.NET.RepoConventions.Tests` and `Rag.NET.PackageValidation.Tests`. **Repack before the latter** — `dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$(dotnet dotnet-gitversion /output json /showvariable SemVer)"`, from PowerShell, because Git Bash cannot find `.git` for GitVersion. The version guard embeds the branch name, so a branch switch alone invalidates the artefacts; this failed four separate times on 2026-09-10 for four different stale reasons, none of them defects. **Do not background either run.**

- [x] **Step 3: Run `pre-push-review`.**

      **Verdict PASS** — `docs/pre-push-review-2026-09-10-1424.md`. 0 blockers, 1 warning, 1 info, both fixed before the PR. The warning: the test helper read one redirected stream to the end and then the other (deadlocks if the child fills the second buffer) and waited with no timeout — a hang-shaped risk inside the guard for a property that exists because of #275, a hang. Fixed per `CliProcessTests.RunAsync`. All four mutation rows re-run afterwards; all four still caught.

- [x] **Step 4: Open the PR.** — **#540**, 2026-09-10: https://github.com/MarcelRoozekrans/Rag.NET/pull/540

       Title: `feat(build): refuse a VSTest filter that Microsoft.Testing.Platform will ignore (#529)`. Not breaking for any library consumer — this is a build-time guard on a test project. It **is** breaking for anyone with `dotnet test --filter` on that project in a script or shell history, which is the intent; say so plainly in the body.

- [x] **Step 5: Stop.** The merge is the operator's.
