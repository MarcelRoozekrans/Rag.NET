# Dependency Pinning & Renovate — Design (Phase 4.8)

**Date:** 2026-08-04
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. What triggered this

On 2026-08-04, `main` went red with no commit pushed to it. CI run 30919869612 failed all three
jobs on one error:

```
QdrantVectorStore.cs(81,29): error CS0618:
'QdrantClient.SearchAsync(...)' is obsolete: 'Use QueryAsync instead.'
```

`Qdrant.Client` is referenced as `1.*`. It floated to 1.18.1 overnight, which marked `SearchAsync`
obsolete, and warnings-as-errors turned an upstream deprecation into a build failure. The previous
run hours earlier was green on identical source.

The breakage was the symptom. The defect it exposed is worse and is described in §1.

## 1. A floating reference publishes an accidental contract

**A floating `PackageReference` does not ship as a range.** It resolves at pack time and freezes
into the published nuspec as a concrete floor. Measured on the produced package:

```xml
<dependency id="Qdrant.Client" version="1.18.1" />   <!-- from Version="1.*" -->
```

NuGet reads `version="1.18.1"` as **">= 1.18.1"**. So the dependency contract that all 66 packages
publish is **decided by when `dotnet pack` happened to run**. Pack today and consumers need
≥ 1.18.1; packed last week, ≥ 1.17.x. Same source, different published promise, and a floor nobody
chose.

For a repository about to publish 66 packages this is the real cost, and it becomes permanent at
Phase 6.3.

**Second consequence: wildcards make Renovate useless.** With `1.*`, Renovate has nothing to
propose — the range already permits every 1.x. Renovate's value is turning a bump into a reviewable
PR carrying the changelog. Today the repository has neither determinism nor review: `renovate.json`
exists but the app is not enabled, and even if it were, wildcards would leave it nothing to do.
Pinning and Renovate only work as a pair.

## 2. This finishes an existing convention

The repository already pins where it matters most:

- **All six analyzers** in `Directory.Build.props` — `Meziantou.Analyzer 3.0.52`,
  `Roslynator.Analyzers 4.15.0`, `ErrorProne.NET.* 0.1.2`, `NetFabric.Hyperlinq.Analyzer 2.3.0`,
  `ZeroAlloc.Analyzers 1.5.0`.
- **The whole `ZeroAlloc.*` family** — 22 references at `1.1.3`, plus `1.5.3`, `1.7.1`, `1.2.0`,
  `1.2.1`, `1.1.0`.

**45 references are already exact. 119 float**, dominated by `Microsoft.Extensions.*` at `10.*`
(71 of them), and including 11 at `0.*` where semver permits breaking changes on every minor.

This phase extends the half that works to the half that just broke `main`.

## 3. Central Package Management

Add `Directory.Packages.props` at the repository root with `ManagePackageVersionsCentrally`,
holding one `<PackageVersion Include="…" Version="…" />` per dependency. Every
`<PackageReference>` across the ~66 projects loses its `Version` attribute and keeps only the id.

Pinning individually would mean 164 edit sites and 164 places to drift; central management makes
the pinned set reviewable in one file, and Renovate supports it natively.

**Versions are pinned at what restores today.** The floor therefore stays exactly what it is now —
identical to the accidental one, but chosen and stable across packs.

Two mechanical hazards planning must handle:

- **NU1008** — CPM errors if any `Version` attribute survives on a `PackageReference`. The sweep
  must be complete, and the build failing is how an incomplete sweep announces itself.
- **`PrivateAssets` / `ExcludeAssets` must be preserved verbatim** on analyzers and source
  generators. Those attributes are what keep six analyzer packages out of every consumer's
  dependency closure — a fact verified during Phase 4.1 and easy to lose in a mechanical edit.

## 4. The verification that makes this safe

**Pack before, pack after, diff every produced nuspec's dependency versions. They must be
byte-identical.**

That is the entire correctness argument, and it is deliberately not "the build passes". A green
build proves the code compiles against the pinned versions; it says nothing about whether a
*published dependency floor moved*. Since pinning at current-latest cannot change what resolves,
any difference in the nuspecs is an unintended change to the contract of a package that has not
shipped yet and cannot be corrected after 6.3.

**Plus a standing guard:** a test asserting **no `PackageReference` anywhere carries a floating
version**, so the convention cannot silently regress. It must be **proven to fail** by
reintroducing a `*` — this repository has shipped three guards that could not fail, and the rule
since is that a guard nobody has watched go red is not a guard.

## 5. Renovate

`renovate.json` gains:

- **Batched non-major updates** on a weekly schedule — one PR for all patch/minor bumps.
- **One PR per major**, because majors are where breakage lives, and Qdrant is the worked example:
  a major deserves its changelog read on its own.
- Existing `dependencies` label and `:semanticCommits` retained, so `commitlint` passes on
  Renovate's own commits.

**Enabling the Renovate GitHub App is the repository owner's action and cannot be done from here.**
So the config ships **inert**, exactly as Phase 4.1 recorded it, with a documented enable procedure
in `docs/reference/ci.md`.

The phase must record two different claims and not conflate them: **pinning is delivered and
provable; upgrade automation is configured and unexercised.** Only the first can be demonstrated by
this work.

## 6. What this buys, stated honestly

It does **not** prevent deprecations. `SearchAsync` would still have been marked obsolete in 1.18.1.

What changes is how it arrives: a Renovate PR whose CI goes red, reviewed and merged on the owner's
schedule — instead of `main` going red with no commit pushed, which is what happened today.

## 7. Out of scope

- **Version upgrades.** This phase changes no resolved version. If a pinned version is later found
  to be behind, that is a Renovate PR, not this work.
- **Treating `0.x` minors as majors in Renovate.** The four `0.x` dependencies
  (`Microsoft.ML.Tokenizers`, `PdfPig`, `ClosedXML`, `Pgvector`) get pinned like everything else;
  whether Renovate should treat their minors as breaking is a tuning question best answered after
  real PR volume is observed.
- **The `ZeroAlloc.ValueObjects` → `Hosting.Abstractions` closure root** routed by Phase 4.7. It is
  a dependency *graph* problem, not a version-pinning one.
