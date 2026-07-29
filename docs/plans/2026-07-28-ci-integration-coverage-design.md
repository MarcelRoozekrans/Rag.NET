# CI Integration Coverage — Design (Phase 3.5)

**Date:** 2026-07-28
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.5
**Covers:** the CI half of Milestone 3's Definition of Done

## There is no CI at all

Verified rather than assumed: no `.github` directory, no workflow files, no `azure-pipelines.yml`,
nothing. Every test in this repository has only ever run on a developer's machine.

That resolves a scope collision. Phase 3.5's goal is *"run the Testcontainers-based suites in CI"*,
and Phase 4.1 is where *"GitHub Actions CI (build + test)"* was scheduled — so 3.5 presupposed
4.1's work. Milestone 3's Definition of Done requires **"Integration/vector-store suites run in CI
(Dockerized)"**, which cannot be met without CI existing, so 3.5 builds it and **Phase 4.1 narrows
to packaging, versioning and publishing on top of a pipeline that already works.** Two phases
quietly both owning a deliverable is how one of them ends up skipped.

## 1. Follow the house conventions

`MarcelRoozekrans/AdoNet.Async` establishes the pattern, and it is worth matching rather than
inventing:

| | Convention |
|---|---|
| Workflows | `ci.yml`, `docs.yml`, `release-please.yml`, `release.yml` |
| Runner | `ubuntu-latest`, `actions/checkout@v7`, `setup-dotnet` 10.0.x, Release configuration |
| Versioning | **GitVersion** (`GitVersion.yml`, `.config/dotnet-tools.json`, parsed with `jq`) |
| Releases | **release-please** (config + manifest) |
| Commits | `.commitlintrc.yml` — conventional commits, which this repo already writes |
| Dependencies | `renovate.json` |
| Analyzers | The same six packages Rag.NET already uses |

`ci.yml` there holds `build-test` and a conditional `pack-push` in one file, the latter gated on
push-to-main. Rag.NET's `pack-push` belongs to 4.1 but should land in the same file when it does.

**A correction this surfaced:** Phase 4.1's roadmap entry says *"MinVer versioning"*. The house
convention is GitVersion plus release-please — different tools, different configuration. That entry
was written before anyone looked at how these repositories are actually set up.

## 2. Three tiers, because the suites are not alike

64 test projects, and they have genuinely different requirements.

**Fast** — no Docker, no network, no secrets. The large majority. Gates every push and PR.

**Docker** — Testcontainers. Deterministic, and GitHub's `ubuntu-latest` has a Docker daemon, so
these gate every push and PR too. Linux-only: the Windows runners have no Linux Docker daemon, so
Testcontainers cannot work there.

**LLM and env-gated** — nightly by default, **opt-in per pull request**. *(Corrected 2026-07-29:
these two were run together here and they should not be. The LLM tier never blocks, for the reasons
below. The env-gated suites do block, because they are deterministic — a Tesseract or Document
Intelligence regression is a real failure, not a model choosing different words. They are separate
jobs in `nightly.yml`; see the end of this section.)* Two
independent reasons it is not automatic on every PR, either sufficient on its own:

- `OllamaFixture` runs `ollama pull nomic-embed-text` **and** `ollama pull llama3.2:1b` — roughly
  two gigabytes of model download per run.
- Phase 2.1 measured `Assert.Contains("Python", answer)` against live Ollama failing about **1 run
  in 11**. That is not a defect; it is a model producing different words. On every PR it produces
  red builds that mean nothing, and a gate people learn to re-run instead of read has stopped being
  a gate.

But *"never on a PR"* is too blunt for the case that matters: someone has just changed the answer
engine, or a retrieval behavior, and wants to know before merging. So the tier also runs **on
demand**:

- **a `run-llm` label on the pull request** — the ergonomic path, decided per PR by whoever knows
  the change touched the LLM path. *(The env-gated job added later gets its own `run-secrets`
  label rather than sharing this one: the two cost very different amounts, and a PR touching the
  PDF parser has no use for a two-gigabyte model download.)*;
- **`workflow_dispatch`** — ad-hoc, off any branch.

**It reports but never blocks, even when requested.** Not a required status check. At roughly
1-in-11 non-determinism a required check would still stop merges on noise, which is the failure
this tiering exists to avoid; opting in says *show me*, not *gate me on a coin flip*. The result is
there to read exactly when someone asked for it, and it can never wedge a merge.

Env-gated suites (`RAGNET_DOCINTEL_*`, `RAGNET_TESSDATA`, `RAGNET_ONNX_*`) already skip cleanly via
`Assert.Skip`, so they are safe wherever they run; nightly and label runs are where secrets can
exist.

**What this section originally left implicit, and got wrong.** *(Corrected 2026-07-29.)* Saying
env-gated tests are "safe wherever they run" is true and was taken to mean they needed nothing
further. It does not follow. All three env-gated projects are **fast-tier** projects, and the nightly
workflow selected only projects declaring `RequiresLlm` — `Rag.NET.E2ETests`, which reads no
`RAGNET_*` variable. The secrets therefore reached no test at all, and those code paths were
exercised by no automated run. Skipping cleanly is not the same as running.

Closed the same way the rest of the phase works: a third self-declaration,
`<RequiresSecrets>true</RequiresSecrets>`, and an `env-gated` job in `nightly.yml` that selects on
it. It is an **overlay, not a fourth tier** — those projects stay in the fast tier and run there on
every push, so the tier partition is untouched; they simply appear in a second workflow as well.
That job **gates**, and the one thing to be honest about is what a green tick means when the secrets
are absent: every test skips and it passes. A step prints which variables are present so the log can
tell the two apart. Absence is deliberately not a failure — it would break every fork.

## 3. Projects declare their own need

Selection is `<RequiresDocker>true</RequiresDocker>` in the test csproj, which CI reads.

The alternative — a list of project names in the workflow — fails silently in the direction that
matters. Add a Testcontainers suite, forget the list, and it simply never runs again with nothing
anywhere to notice. A self-declaring property inverts that: forget the property and the project
lands in the fast tier, where it fails loudly for want of a Docker daemon.

### The drift test, and why the obvious version is wrong

A test asserts that every project which starts a container declares the property. The naive
implementation — *does the csproj mention Testcontainers* — is wrong **in both directions**:

- **Misses three projects.** `Rag.NET.Testing` is a shared fixture library holding `PgVectorFixture`,
  `QdrantFixture` and `OllamaFixture`. Seven test projects reference it, but only three of them —
  `Rag.NET.VectorStores.IntegrationTests`, `Rag.NET.Security.IntegrationTests` and
  `Rag.NET.E2ETests` — actually use a container fixture. Those three start containers without naming
  Testcontainers anywhere in their own csproj.

  (Corrected 2026-07-29. This said *seven*, which is the count of projects that **reference**
  `Rag.NET.Testing` — a different quantity, and precisely the conflation the next bullet warns
  about. Referencing the fixture library is not the signal; using a container fixture from it is.)
- **Falsely flags others.** Referencing `Rag.NET.Testing` does not mean needing Docker.
  `Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests` uses it for WireMock cassettes and
  env-gated live tests, and starts no container at all.

So the signal is **whether a project starts a container**: a direct Testcontainers reference, or use
of one of the named container fixture types. Anything matching must declare the property; anything
declaring it must match. Both directions, or the guard only catches the half that was already
obvious.

## 4. Where this deliberately diverges from AdoNet.Async

That repository's `ci.yml` has no caching, no concurrency control and no explicit permissions. For a
library with 40 tests that is the right amount of machinery.

Rag.NET has 64 test projects and a Docker tier, so two additions earn their place:

- **NuGet caching.** A cold restore across this dependency graph is minutes, on every run.
- **`cancel-in-progress` concurrency.** Without it, three pushes to a branch queue three complete
  matrices including Docker, and the first two are already irrelevant.

Everything else — runner, action versions, configuration, job shape — stays identical, so the two
repositories read the same way.

## 5. Build once, test per project

`dotnet build Rag.NET.slnx` once, then `dotnet test <project> --no-build` per project. Rebuilding 64
projects per test invocation would dominate the run.

Warnings are already errors in `Directory.Build.props`, so a warning fails the build with no CI
configuration at all — the gate exists already and simply needs somewhere to run.

## 6. Testing

The CI configuration itself is not unit-testable, so what gets tested is what can be:

- **The drift test** above, in both directions.
- **A tier-partition test**: every test project lands in exactly one tier — none omitted, none in
  two. A project that exists and runs nowhere is the failure this whole phase is about.

The workflows themselves are verified by running them, which is what a first green build on a pull
request is for.

## Out of scope

- **`pack-push`, GitVersion, release-please** — Phase 4.1, in the same `ci.yml` when they land.
- **`docs.yml`** — Rag.NET has a Docusaurus site (`sidebars.ts`, `src/css/custom.css`, the `docs/`
  tree) and nothing publishes it. That is the same shape of gap as the missing CI and deserves its
  own scheduling rather than being absorbed here.
- **`.commitlintrc.yml` and `renovate.json`** — house furniture this repository lacks; recorded for
  Milestone 4 rather than added under a test-coverage phase.
- **Making the LLM tests deterministic.** They are non-deterministic because a model writes the
  answer. Nightly, opt-in and advisory is the honest handling; asserting less about the text would
  be a separate decision about what those tests are for.
- **Making the LLM tier a required check.** Deliberately not, even when opted into — see §2. The
  moment a 1-in-11 failure can block a merge, the tiering has bought nothing.
