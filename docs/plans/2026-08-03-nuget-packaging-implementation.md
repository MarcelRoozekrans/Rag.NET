# NuGet Packaging & Publishing Implementation Plan (Phase 4.1)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A packaging and publishing pipeline that packs, validates and push-tests 71 packages on every push to `main`, with only the nuget.org push gated until Phase 6.3.

**Architecture:** Everything except the credential and the endpoint runs every time — `dotnet pack` for all packable projects, `.nupkg` validation as a failing build step, and `dotnet nuget push` genuinely executed against a **local file feed**. The nuget.org push is the single gated step, recorded to the standard `TestGateTests` demands of every other gate.

**Tech Stack:** .NET 10, GitHub Actions, GitVersion, release-please.

**Design:** `docs/plans/2026-08-03-nuget-packaging-design.md`. Read §1 first — it is why this plan is shaped the way it is.

---

## The rule this plan exists to enforce

**A push step that never runs is an inert path, and this repository has paid for that shape three times:**

- `nightly.yml` was rewritten in Phase 3.15 and **first executed on 2026-08-02** — it failed immediately, on a race no local run could reproduce.
- The OCR test behind `#if ENABLE_OCR` is **not skipped, it is not compiled**, and appears in no test count.
- Three env-gated guards had secrets nothing ever set, reporting green by skipping.

Phase 4.0 built `TestGateTests` because **a gate nothing can satisfy is not a gate**. A gated push is the same shape.

**So: if you find yourself writing a step that cannot run before Milestone 6, stop and check it against §1.** The only thing allowed to be untested is the nuget.org credential and endpoint.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0048, MA0023, ZA0601, HLQ001, EPC12/EPC13, ERP022, MA0060, MA0025, and the rest. **No `#pragma` or `SuppressMessage`.**
- **`dotnet pack` emits `NU5xxx` for missing metadata, and warnings are errors — so an incomplete package fails the build.** That is the intended behaviour; do not relax it.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- **`Rag.NET.Benchmarks.Quality.Tests` runs with `--logger trx`; never pipe its output through `head`/`tail`/`grep`.** Undiagnosed flake, name lost three times, twice to piping.
- **The `->` recompile line prints on this machine even when nothing recompiled** — check stack-trace line numbers for staleness instead.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A`** — explicit paths.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.

**Baselines:** `Rag.NET.Tests` **1342**, `Rag.NET.Evaluation.Tests` **382**, `Rag.NET.Benchmarks.Quality.Tests` **163**, `Rag.NET.RepoConventions.Tests` **30** (29 + 1 by-design skip).

**Repository shape:** 72 directories under `src/`, of which **71 are packages** — `src/Rag.NET.PgVector` is an empty rename leftover this phase deletes (Task 7). `Directory.Build.props` has **no package metadata at all**; individual projects carry only a `Description`.

---

## Task 1: shared package metadata

**Files:** `Directory.Build.props`

Authors, licence expression, repository URL, project URL, common tags, README and icon if one exists — one definition for all packages.

**The licence must match the repository's actual `LICENSE` (MIT).** Read it; do not choose afresh.

**Do not put `Description` here.** A shared description means 71 packages described identically, which is worse than the current state — it looks complete and says nothing.

**Step 1:** try `dotnet pack Rag.NET.slnx -c Release` **before** adding anything, and record the `NU5xxx` warnings you get. That list is the specification for this task, and it is the honest measure of how much was missing.

**Commit:** `build: shared NuGet metadata for every package`

---

## Task 2: per-package descriptions

**Files:** all 71 packable `src/*/*.csproj`

Every package needs a `Description` that says what *that* package is. Several already have one; audit them.

**A package whose description is generic is a package nobody described.** Check for duplicates across projects and for descriptions that would fit any package in the repository — report both counts.

Package-specific `PackageTags` where they add anything; skip them where they do not rather than padding.

**Commit:** `build: describe each package as itself`

---

## Task 3: pack and validate, as a failing build step

**Files:** `.github/workflows/ci.yml`

`dotnet pack` for all packable projects on every push, then **validate the produced `.nupkg` files** — licence present, README present, description non-empty and not the shared boilerplate, symbols produced, no package accidentally empty.

**Validation must fail the build, not warn.** A warning nobody reads is the shape this project keeps finding.

`NU5xxx` under warnings-as-errors already covers part of this; write the checks that NuGet does not make, and say in your report which are which so nobody re-implements what the SDK already enforces.

**Commit:** `ci: pack every package and validate what it produced`

---

## Task 4: exercise `dotnet nuget push` against a local feed

**Files:** `.github/workflows/ci.yml`

**This is the task the design exists for.** Create a local directory feed in the workflow, `dotnet nuget push` every produced package to it, and assert the packages arrive.

This proves the command, its arguments, the glob that selects packages, and duplicate handling — everything except authentication and the endpoint.

**Then push the same packages a second time** and confirm the pipeline's chosen duplicate behaviour is what you intended, rather than discovering it at 6.3 against a feed that never forgets.

**Commit:** `ci: push to a local feed so the push path is not untested`

---

## Task 5: gate the real push, and record the gate properly

**Files:** `.github/workflows/ci.yml`, `docs/reference/ci.md`

The nuget.org push step exists, is wired, and does not run before Phase 6.3.

**Record the gate the way `TestGateTests` requires of every other gate in this repository:** named, with its condition stated, and satisfiable by a documented procedure — because "satisfiable nowhere" is what that test fails on, and this phase is not entitled to an exemption its own tooling denies everyone else.

**Check whether `TestGateTests` covers workflow gates as well as test gates.** If it does, this gate must satisfy it. If it does not, say so — a gate outside the guard that checks gates is worth knowing about, and possibly worth extending the guard to cover.

**Record the residual honestly** in `ci.md`: pushing to a local feed is not pushing to nuget.org. Authentication, API-key scoping and package-ID availability are exercised for real exactly once, at 6.3.

**Commit:** `ci: gate the nuget.org push, and record the gate`

---

## Task 6: versioning and release tooling

**Files:** `GitVersion.yml`, `.config/dotnet-tools.json`, `.commitlintrc.yml`, `renovate.json`, `.github/workflows/`

**GitVersion, not MinVer** — the roadmap already corrected an entry that said otherwise; follow the house convention in `MarcelRoozekrans/AdoNet.Async` (`GitVersion.yml`, a `.config/dotnet-tools.json` entry, output parsed with `jq`) plus **release-please**.

Version during Milestones 4 and 5 is a **prerelease**. Nothing publishes, but the version must be coherent or the pack step is not real.

**`.commitlintrc.yml` and `renovate.json`** are routed here. Commitlint is load-bearing once release-please derives versions from commit messages — conventional commits are universal in this repository by habit, and this makes it mechanical.

**Commit:** `build: GitVersion, release-please, commitlint and renovate`

---

## Task 7: the routed debts

Seven were routed here, plus one that arrived on 2026-08-03. **Each must be closed or explicitly re-owned — none may be silently dropped.**

1. **`docs/reference/ci.md` is stale** — counts "eleven cases", omits the nine ablation cells Phase 3.14 added to `BeirRunBudget`. This phase reworks `ci.yml`, so reread the page against the workflows.
2. **The nightly downloads an ~87 MB cross-encoder that feeds no test** — every consumer is behind `RAGNET_BEIR_LONG_RUNS`, which that job never sets. Decide: run something that uses it, or stop provisioning it.
3. **Duplicate RAGAS suites** in `tests/Rag.NET.Tests/Evaluation/` and `tests/Rag.NET.Evaluation.Tests/Ragas/`. Merge, or name one authoritative and delete the other.
4. **`src/Rag.NET.PgVector` and `tests/Rag.NET.PgVector.Tests`** are empty rename leftovers. Delete them — one already broke a `dotnet run` by making a project name ambiguous.
5. **`HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize`**, so setting it on Book, Legal or AcademicPaper templates does nothing. **Decide: honour the option, or document it as ignored. Do not change chunking behaviour here** — that is its own phase with its own measurements, as 3.16 was.
6. **`ENABLE_OCR`** compiles out the *production* Tesseract engine as well as its test, so the shipped PDF parser has no real OCR in any default build. Decide how it is compiled in.
7. **`.commitlintrc.yml` / `renovate.json`** — done in Task 6; confirm and close the entry.
8. **`RAGNET_DOCINTEL_ENDPOINT` / `_KEY`** — Milestone 4's DoD keeps "no test gated behind a condition nothing satisfies", but these two were to be resolved by the recorded-responses phase, now 6.1 and *after* this milestone. **Milestone 4 cannot close until they are satisfiable another way**; a documented local procedure suffices for `TestGateTests`.

**Commit:** one per debt, or grouped where they touch the same file — say which and why.

---

## Task 8: close the phase

**Files:** `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Flip 4.1 to complete in **both files in the same commit** — 3.10 and 3.7 both shipped with `MILESTONE.md` left at `[pending]`.

Record what the pack step found: the `NU5xxx` list from Task 1 is a measurement of how much metadata was missing, and it belongs in the entry.

**Check Milestone 4's DoD against reality** — do not tick a box this phase did not make true. In particular, "CI pipeline builds, tests, and produces NuGet packages" becomes true here; "no test gated behind a condition nothing satisfies" depends on debt 8.

**Commit:** `docs(planning): close phase 4.1`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. `dotnet pack Rag.NET.slnx -c Release` → **0 warnings**, 71 packages produced.
3. Baselines hold.
4. No new `#pragma`, `SuppressMessage`, or `KnownFalseClaims` entry.
5. `git status` clean — no `.nupkg` committed.

**Report:** every commit hash, verbatim build and pack output, **the `NU5xxx` list from before Task 1** as the measure of what was missing, the local-feed push result including the duplicate-push behaviour, what you did with each of the eight debts, whether `TestGateTests` covers workflow gates, and everything this plan got wrong.

That last item is not a formality. Every phase in this project has had a plan asserting something the code did not do — 3.16's specified a mathematically impossible assertion, 3.15's design was wrong about which side truncation starved, 4.0's mis-stated the gating-site count, and 3.8's omitted the replay bridge its own design depended on. All four were caught by an agent checking the claim against the code rather than trusting it.
