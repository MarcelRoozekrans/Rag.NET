# A Filter That Filters Nothing — design for Phase 6.2.35

**Issue:** #529. **Phase:** 6.2.35. **Written:** 2026-09-10.

## 0. What the issue got wrong, before anything else

The issue says one cell means "all ~70". **Measured 2026-09-10: it is 267.**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release \
  --filter "FullyQualifiedName~BeirDeepResearchTests"

warning MTP0001: VSTest-specific properties are set but will be ignored ...
                 The following properties are set: VSTestTestCaseFilter;
Passed! - Failed: 0, Passed: 149, Skipped: 118, Total: 267
```

Two figures matter and neither is in the issue. **267 tests run instead of the one class asked
for** — nearly four times the estimate. And **149 of them execute for real with no environment
variables set at all**; only 118 skip. The unprovisioned run is not a no-op, and the provisioned one
is the ten-plus minutes the issue describes, with BEIR corpora, ONNX replay and graph construction
inside it.

**The issue's scope claim, unlike its arithmetic, holds.** Exactly one project sets
`TestingPlatformDotnetTestSupport`, and nothing sets it in `Directory.Build.props`. This was checked
rather than assumed, because 6.2.32 found that #521 named three vector stores when there were six
sites across five components — **an issue this project filed itself, about its own code**.

## 1. The mechanism, traced to the line

`Microsoft.Testing.Platform.MSBuild.targets` (1.9.1) defines:

```xml
<Target Name="InvokeTestingPlatform" DependsOnTargets="_ValidateVSTestProperties">
...
<Target Name="_ValidateVSTestProperties">
  <PropertyGroup>
    <_VSTestPropertiesFound Condition=" '$(VSTestTestCaseFilter)' != '' ">...VSTestTestCaseFilter; </_VSTestPropertiesFound>
  </PropertyGroup>
  <Warning Code="MTP0001" Condition=" '$(_VSTestPropertiesFound)' != '' " ... />
</Target>
```

`dotnet test --filter X` sets the MSBuild property `VSTestTestCaseFilter`. Under
`TestingPlatformDotnetTestSupport`, the runner is `InvokeTestingPlatform`, which does not read that
property. It warns, and proceeds to run everything.

**So the platform already detects the exact condition and already declines to act on it.** The whole
defect is that its response is a `Warning` where the consequence is a wrong answer. **This phase
does not detect anything the platform does not already detect** — it changes the severity, at the
one place where the severity is wrong.

## 2. Why this is a milestone-6 defect and not a papercut

`STATE.md`, 2026-09-07, on four of five phases in a row:

> One defect shape accounts for four of the five. Code that succeeds while doing nothing: a
> swallowed exception, a duplicate id that returns, a rebuilder that never calls BM25, a `Use*`
> nobody invoked. **None of them failed; all of them lied**, and every one was found by running
> something rather than by reading. **Where a guard is cheap, prefer a throw to a tolerant return.**

A filter that filters nothing is precisely that shape, one layer up — in the tooling rather than the
library. It succeeds. It prints a green `Passed!`. It answers a question nobody asked.

**And it has already produced a wrong finding.** Per #529, during #495 the instrumentation sampled
the first cache lookups in the process and captured **self-query** prompts while the reader believed
they were deep-research ones — "an apparent dramatic difference between two checkouts that was
entirely test execution order". That is the same failure class as the performance-measurement traps
this project has hit before: **the measurement was not wrong, the attribution was.**

Nothing shipped has been affected. `ci.yml` and `nightly.yml` select by project and pass no filter,
so automation never takes this path. **The harm is confined to local developer time and to findings
derived from local runs** — which is to say, to the evidence this milestone is built out of.

## 3. Decision — the guard is repo-wide and self-arming, not local

**Decided 2026-09-10 against the issue's own proposal.** #529 suggests an MSBuild target "in that
project". The guard instead lives in a new root `Directory.Build.targets`, hooked on
`BeforeTargets="InvokeTestingPlatform"`.

The reasoning is 6.2.32's, applied to build files rather than to code. That phase moved the throwing
deserialiser into `MetadataSerializer` so that **"a seventh site cannot be written the swallowing
way"** — the point was not to fix six call sites but to make the tolerant shape unwritable. Here:
`InvokeTestingPlatform` exists only when Microsoft.Testing.Platform is the runner, so a target hooked
to it **arms itself for any project that adopts MTP later**, including one that enables it by a route
other than `TestingPlatformDotnetTestSupport`. A guard in the one csproj would have to be remembered
and copied; this one cannot be forgotten because nobody has to know it exists.

**`Directory.Build.targets` does not exist in this repository yet.** Creating it is the one genuinely
repo-wide consequence of this phase and the thing to be careful about — it is imported by every
project, so it must contain nothing that fires outside the guarded condition. Control C below is what
demonstrates that.

**Severity is `Error`, not `Warning`, and the reason is specific:** MTP0001 is *already* a warning,
and it is already being scrolled past — that is how this survived long enough to corrupt an
intermediate finding during #495. **A second warning beside an ignored warning is not a fix.** There
is also no legitimate case to preserve: passing `--filter` to this project can never be honoured, so
refusing it takes nothing away from anyone.

## 4. Proven, not proposed

The guard was prototyped and run before this document was written. Three cases, all measured
2026-09-10.

| | Command | Result |
|---|---|---|
| **A** — the defect | `dotnet test <MTP project> --filter "~BeirDeepResearchTests"` | **`error RAGNET0001`**, build fails, **no tests run** |
| **B** — same project, no filter | `dotnet test <MTP project>` | **267 run normally**, no error — the guard is silent when it should be |
| **C** — filtered non-MTP project | `dotnet test RepoConventions.Tests --filter "~PackageVerificationTests"` | **6 of 97 run** — the filter genuinely narrows, guard does not fire |

**Control C is the one that matters.** It proves both halves at once: the new repo-wide file does not
affect the sixty-odd projects that use the VSTest adapter, *and* `--filter` still narrows correctly
there. A guard that fires on everything would have passed A and B.

The message resolves `$(TargetDir)$(AssemblyName).exe` at build time, so it hands the reader an
absolute, copy-pasteable path rather than a pattern to reconstruct:

```
error RAGNET0001: '--filter' is silently ignored by Rag.NET.Benchmarks.Quality.IntegrationTests:
  ... EVERY test in the assembly runs instead. Use the native xunit v3 runner, which honours
  -class, -method and -filter:
  C:\Projects\Prive\Rag.NET\tests\...\Rag.NET.Benchmarks.Quality.IntegrationTests.exe
    -class <fully.qualified.ClassName>
```

**A guard that only says "no" is worse than one that says "use this instead".** The escape hatch was
verified too: the native runner with `-class` ran **1 test** where `--filter` ran 267, and it prints
the per-test skip *reason*, which `dotnet test` suppresses.

## 5. The open design question this phase must answer

**One wording problem is known and unresolved.** The prototype's message asserts the project "sets
`TestingPlatformDotnetTestSupport`". The *condition* does not check that property — it hooks
`InvokeTestingPlatform`, deliberately, so the guard arms for any MTP project however MTP was enabled.
So the message can state a cause that is not the actual trigger. Today the two coincide because
exactly one project sets exactly that property; the moment they diverge, the error explains the wrong
thing.

Two candidate resolutions, to settle before implementation:

- **Name the trigger, not the cause** — say Microsoft.Testing.Platform is the runner and the filter
  is a VSTest property it does not apply, without naming the opt-in property. Always true; slightly
  less helpful for the one project that exists today.
- **Condition the wording** — emit the property name only when `TestingPlatformDotnetTestSupport`
  is actually set. More precise, more MSBuild.

## 6. Testability, and the thing that makes it necessary

**The guard must be covered by a test, and this milestone is why.** 6.2.32's finding was that
Weaviate's throw-on-corrupt-metadata posture had stood by deliberate review decision since
2026-07-25 with **nothing covering that path for six weeks** — "any refactor could have reverted that
decision silently and every suite would have stayed green". A build-file guard is *more* exposed to
that than code, not less: nobody reads `Directory.Build.targets` twice.

The proposed cover is a `RepoConventions` assertion, that being where this repository already
polices env-var gates, the feature allowlist and the package allowlist. Shape to settle in the plan:
assert that `Directory.Build.targets` exists and defines a target hooked to `InvokeTestingPlatform`
that raises an `Error`. That is a structural assertion, not a behavioural one — it cannot prove the
guard fires, only that it has not been deleted.

**Whether the behavioural half is worth buying is a real question, deliberately left open here.**
Proving the guard actually errors means invoking MSBuild from a test, which is slow and awkward.
6.2.22 established the precedent for declining such a purchase *and writing down why* — it kept the
empty-component rule and left it untested on purpose, because deleting it was invisible through every
public surface. The reverse applies here: deleting this guard is invisible through every surface
*except* a run that then silently does the wrong thing, which is exactly the invisibility the phase
exists to remove.

## 7. Scope

In:

1. **`Directory.Build.targets`** — new root file, the guard, `Error`, hooked to `InvokeTestingPlatform`.
2. **A `RepoConventions` test** so the guard cannot be silently deleted (§6).
3. **Docs** — `docs/reference/ci.md` gains the native-runner recipe where someone reaching for a
   filter will look, and the csproj comment beside `TestingPlatformDotnetTestSupport` gains **what
   the property costs**, not only what it buys. The existing comment explains the #275 deadlock fix
   and is silent on the side effect.
4. **Correct #529 before closing it** — post the measured 267 / 149, so the tracker does not keep a
   figure that is wrong by 4×.

Out:

- **Removing `TestingPlatformDotnetTestSupport`.** It fixes #275's deadlock, where the VSTest adapter
  hung 2 of 4 runs of `BeirGraphRagAnswerTests` before entering test code. Not reopened.
- **Making `--filter` work under MTP.** That is upstream's to solve; the repository's job is to stop
  lying about it.
- **Anything in `ci.yml` / `nightly.yml`.** They pass no filter. Verified, not assumed.

## 8. Verifiability

**Fully verifiable locally, with no account and no container.** That is worth stating plainly: it is
the first phase since 6.2.30 for which nothing is account-blocked, and it stands in deliberate
contrast to 6.2.34, which shipped with a documented `<VerifiedByReason>` gap. Every claim in §4 was
produced by a command on this machine and is reproducible by re-running it.

## 9. Consequences

- **A new repo-wide build file exists.** Every future contributor inherits it. The cost is a file
  nobody reads; the benefit is a trap nobody can fall into.
- **`dotnet test --filter` on the benchmark project becomes a hard failure.** Anyone with that in a
  shell history or a script gets an error with the replacement command in it.
- **The 267 figure enters the record.** Future estimates of benchmark-run cost should use it rather
  than the issue's ~70.
