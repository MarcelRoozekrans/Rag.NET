# Windows CI Coverage — Design (Phase 4.0b)

**Date:** 2026-08-03
**Milestone:** 4 — Release Readiness
**Motivated by:** closing Milestone 3 on a criterion that was false on the development machine

## 0. Why this exists

Milestone 3's Definition of Done required "all test projects passing". It was ticked on a green
nightly, and `Rag.NET.Benchmarks.Quality.Tests` was **failing on Windows at the time** — 1–2
failures in every one of four consecutive local runs.

The nightly runs on Linux and could not see it. **"All tests passing" was read as "CI is green",
and CI is one operating system.**

**Every job in both workflows runs on `ubuntu-latest`.** Nothing has ever run on Windows in CI —
while the primary development machine *is* Windows.

The defect that exposed this was not exotic: NTFS refuses to rename a directory while any handle is
open beneath it, and an on-access virus scanner holds one on just-written files. It was latent in
**three classes** — `BeirDatasetCache`, `EmbeddingCache`, `HypotheticalCache` — and every one of
them publishes by rename, which is the standard safe-write idiom this repository uses deliberately.
POSIX `rename()` has no such restriction, so Linux will never report it.

**This is a permanent asymmetry, not a one-off.** File locking, path separators, path length, line
endings and case sensitivity all differ, and a library published as a NuGet package will be consumed
on Windows by people who are not us.

## 1. Matrix the fast tier over Linux and Windows

`ci.yml`'s fast tier — every test project with no `<Requires*>` declaration, which is the bulk of
the suite — runs on **both** `ubuntu-latest` and `windows-latest`.

**The failing tests were in the fast tier**, so this exact change would have caught the defect on
the push that introduced it, before any milestone was closed on it.

The repository is public, so GitHub-hosted Windows runners cost nothing. The price is wall-clock,
and the two legs run in parallel.

## 2. The Docker tier stays Linux-only, and says so

The Docker tier uses Testcontainers with Linux images — pgvector, Qdrant, Weaviate, Chroma, the
Service Bus emulator. Windows runners can host Linux containers only under nested virtualisation,
which is slow and flaky enough to convert a gating job into a source of noise.

**A gating job that fails for reasons unrelated to the change is worse than no coverage**, because
the first thing people learn is to re-run it. The tier stays on Linux, and the workflow states why
rather than leaving it looking like an oversight.

## 3. The nightly stays Linux-only, deliberately

The nightly's `env-gated` job provisions two ONNX models and runs BEIR measurements for ~19 minutes.
Matrixing it would double that and provision the models twice, to re-measure numbers whose whole
purpose is comparability with figures already published from one machine.

**The Windows exposure it would add is already covered by §1**, because the file-handling code lives
in projects that are in the fast tier. Restating the reason here matters: the exclusion is a
judgement about coverage overlap, not an omission.

## 4. What this changes for branch protection

The fast tier currently runs inside one job. Matrixing it produces **one check per OS**, with names
that include the matrix value — so the existing required check will no longer match.

**Branch protection must be updated to require both**, or the change silently reduces gating from
one required check to none. That is a repository setting, not a file in this branch, so it is
called out explicitly here and in the phase's report rather than assumed.

**Do not add Windows and leave only the Linux check required.** A non-required Windows leg that goes
red and is ignored is the same failure as having no Windows leg, with more noise.

## 5. What this does not cover

- **macOS.** No evidence of need, and no development or deployment target uses it today. Adding a
  third leg for symmetry would be cost without a question behind it.
- **The `llm` job.** Advisory by design and Ollama-container-based; Linux-only for the same reason
  as the Docker tier.
- **The env-gated nightly** — §3.
- **Windows-specific behaviour nobody has written a test for.** A matrix runs the tests that exist.
  It would not have found the rename hazard if `BeirDatasetCacheTests` had not existed; what it
  does is stop such a test from passing on one platform and failing unseen on another.

## 6. The expected first result

**The Windows leg may well go red on its first run**, and that is the point rather than a problem.
Three publish-by-rename sites were fixed only after the defect surfaced; nothing has ever run the
other ~50 fast-tier projects on Windows in CI.

**A red first run is a finding, and its contents are this phase's most valuable output.** The phase
is not complete when the matrix exists — it is complete when the Windows leg is green, or when
whatever it finds is fixed or recorded with an owner.

## Out of scope

- Changing any test to accommodate a platform difference without understanding it. A test that fails
  on Windows is evidence until diagnosed, not a nuisance to be conditioned away.
- Adding `[SkipOnWindows]`-style conditioning as a first response. If a test cannot run on a
  platform, that needs a stated reason, exactly as `TestGateTests` requires of every other gate.
