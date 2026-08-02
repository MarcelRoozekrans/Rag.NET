# Phase 4.0 — Verification Ledger and Claim Agreement Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Milestone 4's Definition of Done falsifiable — three mechanical guards that fail when a documented claim, a test gate, or a package's verification status is not what the repository says it is.

**Architecture:** Three tests in `Rag.NET.RepoConventions.Tests`, which already enforces repo-wide conventions and already fails a test project that does not declare its tier. Extend that pattern rather than inventing one.

**Tech Stack:** .NET 10, xUnit v3.

**Design:** `docs/plans/2026-08-02-milestone-4-replan-design.md`. Read §0 and §7 first — §0 is why this phase exists, and §7 is what it deliberately does not fix.

---

## This phase builds nothing and ships nothing

**Its output is a count of what fails.** Every other phase in this project adds capability; this one adds the ability to notice that capability is missing.

**Expect the tests to fail on first run. That is success, not a problem to work around.** If a guard passes immediately, suspect it before celebrating it — this milestone has found seven instances of a check that agreed with the thing it was supposed to catch.

**Do not fix what the guards find.** Record it. Fixing is later phases' work, and the count of findings is what tells us how the rest of Milestone 4 should be planned.

---

## What is already known, before you start

I ran the substance of guard (a) by hand. `features.md` names three `Rag.NET.*` packages that do not exist under `src/`:

| Named | Reality |
|---|---|
| `Rag.NET.Telemetry` | **Does not exist.** Marked `✅ Done` with `.UseTelemetry()`, `gen_ai.*` conventions and named metrics, none of which exist. The known ghost. |
| `Rag.NET.Cli` | Does not exist **yet** — it is Phase 4.6's deliverable. Legitimately future. |
| `Rag.NET.Parsers.CSharp` | Does not exist. `Rag.NET.Chunking.CSharp` does. Probably a wrong name, **verify rather than assume**. |

**The guard must distinguish "false" from "not yet".** A package named in a section marked `✅ Done` must exist. A package named in a section marked planned or in a future-phase context may not. Getting this distinction wrong in either direction makes the test useless: too strict and it fails on legitimate roadmap text, too loose and it misses the OTel shape.

Also measured: **54 rows are marked `✅ Done`, but only 11 carry a `**Package:**` line.** A check keyed solely on `**Package:**` therefore covers a fifth of the claims. Task 2 widens it.

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matches file), MA0006, MA0008, MA0009, MA0132, MA0140, ZA0601 (no `GroupBy`/`OrderBy`/`ToList` in a loop), ZA0501, EPS05/EPS06, EPC12/EPC13, HLQ001/HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060, MA0025. **No `#pragma` or `SuppressMessage`.**
- No central package management; inline floating versions.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A` or `git add .`** — explicit paths.
- `dotnet build Rag.NET.slnx` → **0 Warning(s), 0 Error(s)** after each task.
- **Timestamp trap:** build without `--no-build` and confirm from the log that projects recompiled.

**Baselines:** `Rag.NET.RepoConventions.Tests` **9**, `Rag.NET.Tests` **1342**, `Rag.NET.Benchmarks.Quality.Tests` **129**, `Rag.NET.Reranking.Onnx.Tests` **18**.

**Repository shape:** 72 packages under `src/`, 69 test projects under `tests/`, 54 `✅ Done` rows and 107 matrix rows in `docs/reference/features.md`, 29 test gating sites.

---

## Task 1: every package named in a Done section must exist

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/FeatureClaimTests.cs`
- Read: `docs/reference/features.md`

Parse `features.md`. For each section marked `**Status:** ✅ Done`, collect the `**Package:**` value if present, and assert the named directory exists under `src/`.

**The failure message must name the feature section, the package, and where it was claimed** — a bare "package not found" sends the reader hunting through 1,100 lines.

**Step 1: write it and run it.** Expect at least `Rag.NET.Telemetry` to fail. Report exactly which sections fail and what each names.

**Step 2: classify each failure**, and this is the part needing judgement rather than code:
- **A genuinely false claim** — the section says Done, the code does not exist. Record it; do not fix `features.md` here.
- **A wrong name** — the thing exists under a different name (`Rag.NET.Parsers.CSharp` vs `Rag.NET.Chunking.CSharp`). Verify which, and record.
- **Legitimately future** — named in a planned context. If your parser flagged it, the parser is wrong: tighten it so `✅ Done` is what gates the assertion.

**Do not make the test pass by relaxing it.** If it fails on three sections and two are real, the test is right and the docs are wrong. Leave it failing, mark it with `Assert.Skip` **only if** you cannot otherwise commit a green suite, and say so loudly in your report.

**Commit:** `test(conventions): a Done feature must name a package that exists`

---

## Task 2: widen the check beyond the 11 sections with a Package line

**Files:**
- Modify: `tests/Rag.NET.RepoConventions.Tests/FeatureClaimTests.cs`

Only 11 of 54 Done sections declare a package. The other 43 describe their feature in prose with backticked identifiers — type names, method names, options classes.

Extract backticked identifiers that look like code (`Rag.NET.*` package names, PascalCase type names, `.MethodName()` calls) from each Done section and assert they resolve — a type in a shipped assembly, a public member, or a package directory.

**Reflection over the built assemblies is the reliable resolver**; the test project can reference them or load them from the build output. Text-matching against source files will produce false positives on comments and prose.

**This will have a false-positive rate and you must measure it.** Prose contains backticked things that are not code — configuration values, file names, JSON keys, English words in backticks. **Report the raw failure count and how many are genuine**, then narrow the extraction until the false positives are gone *without* narrowing away the true ones.

**If you cannot get the false-positive rate to near zero, say so and stop at the Task 1 scope.** A guard nobody trusts gets suppressed within a month, and a suppressed guard is worse than none because it looks like coverage. Report which of the 54 sections end up genuinely checked.

**Commit:** `test(conventions): resolve the identifiers a Done feature names`

---

## Task 3: no test may be gated behind a condition nothing satisfies

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/TestGateTests.cs`

29 gating sites exist: 17 `Assert.SkipWhen`, 9 `Assert.SkipUnless`, 2 `[Fact(Skip=...)]`, 1 `#if ENABLE_OCR`.

Four are known unsatisfiable: three env-gated guards whose secrets no workflow sets, and the OCR test behind `#if ENABLE_OCR`, which no workflow defines — so it is **not skipped, it is not compiled**, and does not appear in any test count.

Assert that every gate is satisfiable **somewhere**, and that where it is satisfiable is written down. Concretely: collect the environment variables the gates read, and require each to be set by `ci.yml`, by `nightly.yml`, or listed in a documented local-procedure file that this test also reads.

**The `#if` case needs different handling from the env-var cases** — a compile-time gate cannot be detected by looking at compiled tests, because the test is not there. Scan the source for conditional-compilation symbols and check each is defined by some build configuration or documented.

**Expect this to fail on at least four.** Report every gate, its condition, and where (if anywhere) that condition is satisfiable.

**Commit:** `test(conventions): a test gate nothing can satisfy is not a test`

---

## Task 4: the VerifiedBy ledger — the test first

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs`

Assert every package under `src/` declares `<VerifiedBy>` in its `.csproj`, with one of: `unit`, `container`, `recorded`, `live`, `none`.

This extends the convention `ci.yml` already selects on — `<RequiresDocker>`, `<RequiresSecrets>`, `<RequiresLlm>` — and which `TestProjectTierTests` already enforces for test projects. **Read `TestProjectTierTests.cs` first and follow its shape**; it solves the same problem for the other half of the repository.

Two assertions, and the distinction matters:

- **Every package declares a value.** Fails now, for all 72.
- **No package declares `none`.** This is the **release** gate, not the declaration gate. It belongs in the Definition of Done, and it must NOT fail the build today — `none` is an honest declaration of a real state, and forcing it to be untrue is how the ledger becomes fiction.

Implement the first as a test. Express the second as a test too, but one that reports rather than fails — a count of `none` packages, printed, with the DoD carrying the release requirement. **Say clearly in your report which of the two you made hard.**

**Commit:** `test(conventions): every package declares how it has been verified`

---

## Task 5: declare all 72, honestly

**Files:**
- Modify: all 72 `src/Rag.NET*/…csproj`

Add `<VerifiedBy>` to each. **This is judgement work, not mechanical work, and the value of the ledger is entirely in the honesty of the values.**

Guidance:
- `unit` — tested with fakes and fixtures only. **This is not a failure state**; it is the accurate description of most packages, and it is what late chunking was for five phases.
- `container` — exercised against a real dependency in Docker (the vector stores with Testcontainers).
- `recorded` — exercised against a recorded real response. **Nothing qualifies yet**; recordings are a later phase.
- `live` — exercised against the real service.
- `none` — no meaningful test at all.

**Do not inflate.** A package with one trivial smoke test is `unit`, and a package whose tests all skip in every environment is closer to `none` than to whatever its tests claim. When uncertain between two values, choose the lower one and say why in your report.

**Report the distribution.** The count at each level is this phase's headline number and will shape how the rest of Milestone 4 is planned.

**Commit:** `chore(conventions): declare verification status for all 72 packages`

---

## Task 6: apply the new DoD and the debt owners

**Files:**
- Modify: `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Apply §6 of the design — the Definition of Done, with the three new criteria — and §5's debt owner table, giving every Milestone 3 leftover an owning phase instead of a milestone-as-deadline.

Add Phase 4.0 to the phase list, marked complete, with what it found: the failing feature claims, the unsatisfiable gates, and the `VerifiedBy` distribution.

**Note this branch is stacked on `chore/milestone-3-audit`** (PR #8), which edits the same two files. If that has merged, rebase first. If it has not, keep your edits additive and expect to rebase.

**Commit:** `docs(planning): Milestone 4's definition of done becomes falsifiable`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. Existing baselines hold; `RepoConventions` rises from 9 by however many tests you added.
3. No new `#pragma` or `SuppressMessage`.
4. `git status` clean.

**Report:**
- **The count of failing feature claims**, with each classified as false / wrong-name / future.
- **Every unsatisfiable gate**, with its condition.
- **The `VerifiedBy` distribution across all 72 packages.**
- Which guards you made hard-failing and which report-only, and why.
- The false-positive rate you measured in Task 2, and what you narrowed.
- Everything this plan got wrong.

That last item is not a formality. Every phase in this milestone has had a plan asserting something the code did not do — Phase 3.16's plan specified a mathematically impossible assertion, and its design was wrong about `DeterministicChunkId`. Both were caught by an agent checking the claim against the code instead of trusting it.
