# NuGet Packaging & Publishing — Design (Phase 4.1)

**Date:** 2026-08-03
**Milestone:** 4 — Release Readiness

## 0. What this phase is, now that v1.0 moved

Milestone 4 was retitled on 2026-08-03: it is the shipping *work*, and the release itself is Phase
6.3, behind hardening. So 4.1 builds a packaging and publishing pipeline **and deliberately does not
publish anything**.

That decision is deliberate and it creates this design's central problem.

## 1. A push step that never runs is an inert path, and this project keeps finding defects in those

**The push is the one step the pipeline will never execute before Milestone 6.** This repository has
a documented history of exactly that shape:

- `nightly.yml` was rewritten in Phase 3.15 and **first executed on 2026-08-02** — it failed
  immediately, on a race no local run could reproduce.
- The OCR test sits behind `#if ENABLE_OCR`, which no workflow defines, so it is **not skipped — it
  is not compiled**, and appears in no test count.
- Three env-gated guards had secrets nothing set, reporting green by skipping.

Phase 4.0 built `TestGateTests` precisely because a gate nothing can satisfy is not a gate. **A
gated push step is the same shape**, and shipping one unexercised until the day it matters would
repeat a mistake this project has already paid for three times.

**So the design's rule is: everything except the credential and the endpoint is exercised on every
run.**

- `dotnet pack` runs for all packable projects, every push to `main`.
- The produced `.nupkg` files are **validated** — contents, metadata completeness, symbols, licence
  and README presence — as a build step that fails.
- The push mechanics run against a **local file feed** in CI, so `dotnet nuget push` itself is
  executed, its arguments are proven, and duplicate/versioning behaviour is observed.
- Only the final push to nuget.org is gated, and **the gate is recorded the way `TestGateTests`
  requires of every other gate**: named, with a stated condition, satisfiable by a documented
  procedure at 6.3.

**The residual is honest and stated: pushing to a local feed is not pushing to nuget.org.**
Authentication, API-key scoping, package-ID availability and the service's own validation are only
exercised for real once. That gap is recorded rather than papered over — it is the argument the
rejected alternative (publish prereleases now) was making, and it does not vanish because the
alternative was not chosen.

## 2. Versioning and release: GitVersion and release-please

The house convention in `MarcelRoozekrans/AdoNet.Async` is **GitVersion** (`GitVersion.yml`, a
`.config/dotnet-tools.json` entry, output parsed with `jq`) plus **release-please** for the release.
The roadmap already corrected an earlier entry that said MinVer; follow the convention rather than
re-deciding it.

**Conventional commits are already universal here**, enforced by habit across every phase, so
release-please has the input it needs. `.commitlintrc.yml` — routed to this phase — makes that
enforcement mechanical rather than cultural, which matters once a tool derives versions from it.

Version during Milestone 4 and 5 is a **prerelease**: nothing is published, but the pipeline must
produce a coherent version so the pack step is real. 6.3 decides the release version.

## 3. Seventy-one packages with no metadata

`Directory.Build.props` carries **no package metadata at all** — no authors, licence, repository
URL, tags, README or icon. Individual projects carry a `Description` and nothing else.

This is the phase's bulk work, and two things make it more than boilerplate:

**Warnings are errors here.** `dotnet pack` emits `NU5xxx` for missing licence, README, icon and
description. With the repository's warnings-as-errors setting, an incomplete package **fails the
build** — which is the right outcome, and means the metadata cannot be half-done.

**Shared metadata belongs in `Directory.Build.props`; per-package metadata does not.** Authors,
licence, repository URL, and the common tags are one definition. `Description` and package-specific
tags are per-project, and a package whose description is the generic one is a package nobody
described — worth a check rather than a template.

The repository has an **MIT LICENSE** already; the licence expression must match it rather than be
chosen afresh.

## 4. The debts routed here, and one that has moved

Seven were routed to 4.1 by earlier phases. They are not filler — several are the reason this phase
touches `ci.yml` at all:

- **`docs/reference/ci.md` is stale** — it counts "eleven cases" and omits the nine ablation cells
  that Phase 3.14 added to `BeirRunBudget`. This phase reworks `ci.yml`, so it must reread that page
  against the workflows.
- **The nightly downloads an ~87 MB cross-encoder that feeds no test** — every consumer is behind
  `RAGNET_BEIR_LONG_RUNS`, which that job never sets. Decide: run something that uses it, or stop
  provisioning it.
- **`.commitlintrc.yml` and `renovate.json`** — house furniture, and commitlint is load-bearing once
  release-please derives versions from commit messages.
- **Duplicate RAGAS test suites** in `tests/Rag.NET.Tests/Evaluation/` and
  `tests/Rag.NET.Evaluation.Tests/Ragas/`. Merge, or name one authoritative and delete the other.
- **`src/Rag.NET.PgVector` and `tests/Rag.NET.PgVector.Tests` are empty rename leftovers.** Delete
  them with the package inventory — one of them already broke a `dotnet run` by making a project
  name ambiguous.
- **`HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize`**, so setting it on Book, Legal
  or AcademicPaper templates does nothing. Routed here as public-API scrutiny: **an option a user
  can set that silently does nothing is a contract defect**, and packaging is when the public
  surface is inventoried.
- **`ENABLE_OCR`** — the symbol compiles out the *production* Tesseract engine as well as its test,
  so the shipped PDF parser has no real OCR in any default build. Decide how it is compiled in.

**And one that moved into this phase's path on 2026-08-03:** Milestone 4's DoD keeps "no test gated
behind a condition nothing satisfies", but two of the four unsatisfiable gates
(`RAGNET_DOCINTEL_ENDPOINT`/`_KEY`) were to be resolved by the recorded-responses phase — now 6.1,
*after* this milestone. **So Milestone 4 cannot close until those gates are satisfiable another
way**, and a documented local procedure suffices for `TestGateTests`. That lands here.

## 5. What this phase does not do

- **It does not publish.** Not to nuget.org, not to GitHub Packages. 6.3 publishes.
- **It does not reserve package IDs.** The names remain unclaimed for the duration, which is a real
  exposure on a public repository with an attractive prefix, and is the accepted cost of the choice
  in §0.
- **It does not decide the release version.** That is 6.3's, once hardening has run.
- **It does not fix `HierarchicalMerger`'s behaviour** — it decides whether the option is honoured
  or documented as ignored. Changing chunking behaviour is its own phase with its own measurements,
  as Phase 3.16 was.

## Out of scope

- Signing and SourceLink beyond what packaging validation requires to pass.
- Any change to `ci.yml`'s tier structure, which Phase 3.5 settled and Phase 4.0 matrixed.
