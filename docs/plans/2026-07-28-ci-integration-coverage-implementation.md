# CI Integration Coverage Implementation Plan (Phase 3.5)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A GitHub Actions pipeline that builds the solution and runs every test project in the right tier — fast and Docker suites gating each PR, the Ollama suite nightly and opt-in.

**Architecture:** Test projects declare their own needs (`RequiresDocker`, `RequiresLlm`); the workflows select on those properties, and a conventions test makes a wrong or missing declaration fail loudly rather than silently removing a suite from CI.

**Tech Stack:** GitHub Actions, .NET 10, Testcontainers, xUnit v3.

**Design:** `docs/plans/2026-07-28-ci-integration-coverage-design.md`. Read it first — especially §2 (tiers), §3 (why the obvious drift test is wrong both ways) and §4 (the two deliberate divergences).

---

## Before you write any YAML: read the real thing

The design's table of house conventions was transcribed from a **model's summary** of
`MarcelRoozekrans/AdoNet.Async`, not from the file. Action versions in particular are exactly the
sort of detail a summary gets wrong — the summary says `actions/checkout@v7`, and the public latest
is v4.

**Fetch these and match them verbatim:**
- `https://raw.githubusercontent.com/MarcelRoozekrans/AdoNet.Async/main/.github/workflows/ci.yml`
- `https://raw.githubusercontent.com/MarcelRoozekrans/AdoNet.Async/main/.github/workflows/docs.yml` (for shape only; `docs.yml` is out of scope here)

Copy the runner, action names **and versions**, `setup-dotnet` configuration, step ordering and
naming style exactly. Where this plan and that file disagree, **the file wins** — and tell me.

---

## The tiers, concretely

**Docker tier — 9 projects.** Verified by two signals: a direct `Testcontainers` package reference,
or use of a container fixture from the shared `tests/Rag.NET.Testing` library.

Direct reference:
`Rag.NET.Ingestion.AzureServiceBus.Tests`, `Rag.NET.VectorStores.Chroma.Tests`,
`Rag.NET.VectorStores.Pinecone.Tests`, `Rag.NET.VectorStores.Weaviate.Tests`,
`Rag.NET.VectorStores.Qdrant.Tests`, `Rag.NET.VectorStores.PgVector.Tests`,
`Rag.NET.VectorStores.AzureAISearch.Tests`

Via `PgVectorFixture` / `QdrantFixture`:
`Rag.NET.VectorStores.IntegrationTests`, `Rag.NET.Security.IntegrationTests`

**LLM tier — 1 project.** `Rag.NET.E2ETests`, which uses `OllamaFixture`. It needs Docker *and* pulls
`nomic-embed-text` plus `llama3.2:1b`.

**Fast tier — everything else (54).**

*(Counts corrected 2026-07-29: 64 test projects, not 66; fast tier 54, not ~56. 54 + 10 declaring
`RequiresDocker`, of which 1 also declares `RequiresLlm`, = 64.)*

**Env-gated tests need no tier — but they do need a job.** `RAGNET_TESSDATA`, `RAGNET_DOCINTEL_*`
and `RAGNET_ONNX_*` tests already `Assert.Skip` when the variable is absent, so their projects sit in
whichever tier they belong to and simply skip. The tier claim stands. The sentence that followed it —
*"nightly supplies the secrets so they actually execute"* — did **not**.

*(Corrected 2026-07-29.)* All three env-gated projects — `Rag.NET.Parsers.Pdf.Tests`,
`Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` and `Rag.NET.Chunking.IntegrationTests` — are
**fast-tier** projects, and nightly ran only the projects declaring `RequiresLlm`: `Rag.NET.E2ETests`
alone, which reads no `RAGNET_*` variable. The secrets were set on a job that could not reach a
single test that wanted them, so those code paths ran nowhere and nothing failed. Closed by a third
declaration, `<RequiresSecrets>true</RequiresSecrets>`, and an `env-gated` job in `nightly.yml` that
selects on it. It **gates** — these suites are deterministic, unlike the LLM tier — with the caveat
that when the secrets are absent every test skips and the job passes; see the comment in the file.

`RequiresSecrets` is an overlay, not a fourth tier: a project is in exactly one tier and may appear
in more than one workflow, so `ci.yml`'s partition arithmetic is unaffected.

**Do not trust these lists.** Re-derive them from the repository — that is what Task A2 exists to
enforce, and if the lists here are wrong the conventions test should catch it and you should tell me.

---

## Conventions
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A` or `git add .`** — explicit paths. `.lucent/*` is expected dirty; leave it.
- Warnings are errors (`Directory.Build.props`), so CI needs no extra strictness flag.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- **No new `#pragma` or `SuppressMessage`.**

Verify after each task: `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)**.

Baselines: `Rag.NET.Tests` **1308**, `Rag.NET.Diagnostics.Tests` **96**, `Rag.NET.Evaluation.Tests` **262**, `Rag.NET.Api.Tests` **63**, `Rag.NET.DataProviders.Tests` **69**, `Rag.NET.Security.Tests` **100**.

---

## Part A: make the repository declare itself

### Task A1: add the properties

**Files:** the 10 csprojs listed above.

Add to each Docker project:
```xml
<PropertyGroup>
  <!-- Starts a Testcontainers container. CI runs these in the Docker tier, which is
       Linux-only: the Windows runners have no Linux Docker daemon. -->
  <RequiresDocker>true</RequiresDocker>
</PropertyGroup>
```

`Rag.NET.E2ETests` gets both:
```xml
<RequiresDocker>true</RequiresDocker>
<!-- Pulls nomic-embed-text and llama3.2:1b — about 2 GB per run — and its assertions are
     model output, measured at roughly 1 failure in 11 runs in Phase 2.1. Nightly and opt-in,
     never a required check. -->
<RequiresLlm>true</RequiresLlm>
```

These are plain MSBuild properties nothing consumes at build time, so the build must be unchanged.
**Verify:** `dotnet build Rag.NET.slnx` → 0/0, and every baseline count above unchanged.

**Commit:** `build: let test projects declare whether they need Docker or a model`

### Task A2: the conventions test

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/` (project + `Rag.NET.slnx` entry)
- Create: `TestProjectTierTests.cs`

A new project rather than an existing one: it tests the *repository*, not a library, and future
conventions (the sidebar gap recorded for 4.5, for instance) get a home. It needs the repo root at
runtime — walk up from `AppContext.BaseDirectory` until `Rag.NET.slnx` is found, and fail with a
clear message if it is not, rather than silently testing nothing.

**Three tests, and the third is the one that matters:**

```csharp
[Fact]
public void EveryTestProjectDeclaresExactlyOneTier()
{
    // A project in no tier runs nowhere. That is the failure this phase exists to prevent,
    // and it is invisible: CI stays green because nothing ran.
    foreach (var project in TestProjects())
    {
        var docker = Declares(project, "RequiresDocker");
        var llm = Declares(project, "RequiresLlm");

        Assert.False(llm && !docker, $"{project} declares RequiresLlm without RequiresDocker.");
    }
}

[Fact]
public void EveryProjectThatStartsAContainerDeclaresRequiresDocker()
{
    // Both directions. The obvious version of this test — grep the csproj for Testcontainers —
    // is wrong twice over: ten projects start containers, but three of them do it through
    // Rag.NET.Testing's fixtures and name Testcontainers nowhere; and one project references
    // Rag.NET.Testing for WireMock cassettes while starting no container at all.
    foreach (var project in TestProjects())
    {
        var starts = ReferencesTestcontainers(project) || UsesAContainerFixture(project);
        var declares = Declares(project, "RequiresDocker");

        Assert.Equal(starts, declares);   // message names the project and both sides
    }
}

[Fact]
public void TheWorkflowSelectsOnThePropertyNotAHardcodedList()
{
    // Guards the mechanism itself: if someone replaces the property query with a list of
    // project names, drift becomes silent again and this whole test file becomes decorative.
    var ci = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));

    Assert.Contains("RequiresDocker", ci, StringComparison.Ordinal);
}
```

`UsesAContainerFixture` scans the project's `.cs` files for `PgVectorFixture`, `QdrantFixture` or
`OllamaFixture`. Keep the fixture names in one array so adding a fixture is one edit.

**Verify by mutation, both directions:** remove `RequiresDocker` from one project and confirm test 2
fails naming it; add it to a project that starts no container and confirm test 2 fails the other
way. Report both messages.

**Commit:** `test: fail when a test project's tier declaration drifts from what it does`

---

## Part B: the workflows

### Task B1: `ci.yml`

**Files:** Create `.github/workflows/ci.yml`

Shape, after matching the house file's exact action versions and step style:

- **Triggers:** push and pull_request on `main`.
- **Concurrency:** group by workflow + ref, `cancel-in-progress: true`. A divergence from the house
  file, earned: without it three pushes queue three complete matrices including Docker.
- **Job `build-test`** on `ubuntu-latest`:
  1. checkout, setup-dotnet 10.0.x
  2. **cache NuGet** (`~/.nuget/packages`, keyed on the hash of every `*.csproj` — the repo has no
     lock files). The second divergence, earned by a cold restore across this graph.
  3. `dotnet restore Rag.NET.slnx`
  4. `dotnet build Rag.NET.slnx -c Release --no-restore`
  5. **fast tier:** every test project *without* `RequiresDocker`, `dotnet test --no-build -c Release`
  6. **docker tier:** every project with `RequiresDocker` and without `RequiresLlm`

Select by reading the property, not a list. `dotnet msbuild <proj> -getProperty:RequiresDocker` is
exact but is 64 process launches; a `grep -l` over `tests/*/*.csproj` is one command and obviously
correct given the property is written literally. **Use grep, and let Task A2's third test guard that
the workflow still selects on the property.**

Run each project separately so a failure names the project. Do not `continue-on-error` — the whole
point is that these gate.

**Note:** `Directory.Build.props` carries an MSB3492 workaround whose comment says *"`dotnet test`
from a completely empty `obj/` still requires `dotnet build` first"*. CI starts empty every time, so
the explicit `dotnet build` step before any `dotnet test --no-build` is **load-bearing**, not an
optimisation.

**Commit:** `ci: build and test every project in its tier on each push`

### Task B2: `nightly.yml`

**Files:** Create `.github/workflows/nightly.yml`

- **Triggers:** `schedule` (a quiet hour, UTC), `workflow_dispatch`, and `pull_request` with
  `types: [labeled]`.
- **Guard:** the PR path runs only when the label is `run-llm`
  (`if: github.event.label.name == 'run-llm'`).
- **Job `llm`** on `ubuntu-latest`: same setup, then every project with `RequiresLlm`.
- **`continue-on-error: true`** — deliberate, and the reason belongs in a comment in the file: these
  assertions are model output, measured at about 1 failure in 11 runs. Opting a PR in says *show me*,
  not *gate me on a coin flip*, and a required check that fails on noise teaches people to re-run
  instead of read.
- Nightly also sets the `RAGNET_*` secrets so the env-gated tests execute rather than skip. Use
  `secrets.*` with no fallback: absent secrets leave the tests skipping, which is the current
  behaviour and is safe.

**Do not add this workflow to branch protection.** Note that in the file itself, because the reason
lives in a design doc nobody will read at the moment they are tempted.

**Commit:** `ci: nightly and opt-in LLM tier, reporting rather than gating`

---

## Part C: documentation and roadmap

### Task C1

**Files:**
- `docs/` — a short contributing/CI note: the three tiers, how to opt a PR in with the `run-llm`
  label, and that a new Testcontainers suite must declare `RequiresDocker` or the conventions test
  fails.
- `docs/planning/ROADMAP.md` — **correct Phase 4.1**: it says "MinVer versioning"; the house
  convention is **GitVersion + release-please**. Narrow 4.1 to packaging, versioning and publishing,
  and note that 3.5 delivered the CI half and that `pack-push` belongs in the existing `ci.yml`.
- `docs/planning/ROADMAP.md` — record as scheduled debt: **`docs.yml`** (a Docusaurus site exists and
  nothing publishes it), **`.commitlintrc.yml`** and **`renovate.json`** — house furniture this repo
  lacks. Put them in Milestone 4 with the other release-readiness work.

Do **not** flip 3.5 to complete — that happens after the whole-phase review.

**Commit:** `docs: record the CI tiers and correct Phase 4.1's versioning tooling`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. Every baseline count above, unchanged, plus the new conventions project's count.
3. Both Task A2 mutations.
4. **YAML is not compiled, so read it back.** Confirm every referenced project path exists, the
   property queries return the expected project counts (9 Docker, 1 LLM, 54 fast), and the tier
   sums to the total number of test projects with none counted twice.
5. `docs/planning/ROADMAP.md` and `MILESTONE.md` flip to complete **after** the whole-phase review —
   both files, per the `73472b4` precedent.

**The real verification is a green run on a pull request**, which cannot happen until this branch is
pushed. Say so in the report rather than implying the workflows are proven.
