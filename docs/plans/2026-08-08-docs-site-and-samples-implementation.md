# The Documentation Site, and Samples — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the documentation site build, make it stay built, then write samples against a site that provably works — and retire the last `VerifiedBy: none` package.

**Architecture:** Repair the build (upgrade + lockfile + links), add `docs.yml` so nothing can break it silently again, then samples, then `Rag.NET.Security.AspNetCore`.

**Tech Stack:** Docusaurus 3.10.2, Node 24, npm, .NET 10, xUnit v3.

**Design:** `docs/plans/2026-08-08-docs-site-and-samples-design.md`

---

## Context

**The documentation site does not build.** `npm run build` fails, for two independent reasons:

- `@docusaurus/core` 3.7.0 declares `webpack: ^5.95.0`; that caret resolves to **5.109.2**, which tightened `ProgressPlugin`'s options schema. Docusaurus still passes `name`, `color`, `reporters`, `reporter`. **There is no lockfile**, so this arrives on any fresh install.
- **25 broken links across 7 pages**, including the landing page.

**No CI job has ever built the site.** That is why neither was noticed, and it is why `docs.yml` is Task 3 rather than an afterthought.

## Ground rules

- Warnings are errors on the .NET side. **No `#pragma`, `SuppressMessage`, `NoWarn`.** MA0051, MA0048, MA0061, **MA0006**, ERP022, EPC12/13, ZA0601. **MA0006 only surfaces under `-c Release`.**
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Subject under 100 characters.**
- **Never `git add -A`** — explicit paths. **Never stage `node_modules/`, `.docusaurus/` or `build/`** (Task 1 gitignores them; until then they are 1,415 untracked entries waiting for a careless `add`).
- **Never pipe build/test output through `head`/`tail`/`grep`.**
- **`git status` before committing** — a file watcher edits `.csproj`/`.slnx` concurrently.

**Baselines:** `Rag.NET.Tests` **1180**, `RepoConventions` **48 + 1 skip**, `PackageValidation` **20**.

**Run `Rag.NET.PackageValidation.Tests` in final verification.** Phase 4.6 shipped red because it was omitted; it is the only suite that checks the packed artefact rather than the source. It needs packages present — see Final verification.

---

## Task 1: Make the build reproducible

**Files:** `package.json`, `package-lock.json` (new), `.gitignore`

**Step 1 — upgrade.** `@docusaurus/core`, `preset-classic`, `theme-mermaid`, `module-type-aliases`, `tsconfig`, `types`: **3.7.0 → 3.10.2**. Measured: after this the server and client bundles both compile and the `ProgressPlugin` error is gone.

**Do not pin webpack down instead.** It would work today and rot identically tomorrow.

**Step 2 — commit `package-lock.json`.** This is what gives the upgrade a shelf life; without it the next fresh install picks new transitive versions again. **`npm ci` must work afterwards** — verify by deleting `node_modules` and running it.

**Step 3 — `.gitignore`:** add `node_modules/`, `.docusaurus/`, `build/`. **Do not ignore `package-lock.json`.**

**Step 4** — `npm run build`. It will still fail, on broken links. That is Task 2, and seeing that specific failure is how you know Task 1 worked.

`typescript` is pinned `~7.0.0` and resolves to **7.0.2** — the Go rewrite. The build failed before reaching it, so it is *unverified, not innocent*. If it causes trouble, report it rather than silently changing the pin.

---

## Task 2: Fix the 25 broken links

**Files:** `docs/index.md`, `docs/getting-started.md`, `docs/guide/{chunking,ingestion,post-retrieval,retrieval}.md`, `docs/reference/library-comparison-defaults.md`

Three mechanical patterns — all verified against where the files actually are:

| From | Links to | Reality | Count |
|---|---|---|---|
| `index.md`, `getting-started.md` | `architecture.md`, `chunking.md`, `retrieval.md`, `post-retrieval.md`, `observability.md`, `ingestion.md`, `data-providers.md`, `memory.md`, `vector-stores.md`, `evaluation.md`, `extending.md`, `mediator.md` | all in `docs/guide/` | 18 |
| `index.md`, `guide/*.md` | `benchmarks.md`, `oss-libraries.md` | in `docs/reference/` | 6 |
| `reference/library-comparison-defaults.md` | `../plans/2026-08-02-library-comparison-design.md` | **`plans/` is not part of the site** | 1 |

The first two are path prefixes. **The third is different** — that document is not published, so a relative link can never resolve. Either link to it on GitHub by absolute URL, or drop the link and keep the prose. **Say which and why.**

**Anchors must resolve too**, not just pages: `retrieval.md#metadata-filtering`, `benchmarks.md#redundancy-filter`, `benchmarks.md#hybrid-search-bm25-fallback`. Check the heading exists in the target.

**`onBrokenLinks` stays at its default.** Docusaurus offers `'warn'`, which makes the build pass with the links still broken. **Taking it is the one forbidden move in this task** — a green build over broken documentation is the defect this whole phase exists to remove.

**Verify:** `npm run build` succeeds. Report the count it reports, not the count in this table.

---

## Task 3: `docs.yml` — the guard

**Files:** `.github/workflows/docs.yml` (new)

**Build the site on every pull request.** That is the entire point: a broken link or a dependency drift becomes a red check on the change that caused it.

- `npm ci` (works after Task 1), then `npm run build`.
- Pin the Node version explicitly rather than taking the runner default — this phase exists because a floating dependency broke a build.
- Model job structure, permissions and concurrency on `ci.yml`; do not invent a new shape.

**Do not add deployment.** The design's §2: the site is configured for `rag-net.github.io/Rag.NET/`, the repository is `MarcelRoozekrans/Rag.NET`, and **Pages is enabled nowhere** (the API 404s). Deployment needs a decision this phase does not have. **Do not "fix" `organizationName` either** — replacing an obviously wrong value with a plausibly wrong one is worse.

**A workflow that cannot run is worse than none** — this repository has shipped inert workflows before. Confirm the triggers are ones that actually fire on a normal PR, and say how you confirmed.

---

## Task 4: Samples

**Files:** `samples/`

`samples/Rag.NET.Sample` is the only sample, for seventy packages.

**Read `docs/getting-started.md` and follow it.** The phase's charter is that this is when the docs get read end to end, and a sample is the honest way to do it: follow your own instructions and find where they lie. **Report every place the page and reality disagreed** — that finding is worth more than the sample.

Scope: enough samples to cover the main scenarios a reader would try first. **Prefer few and genuinely runnable over many and illustrative.** Each must build in the solution and be listed in `Rag.NET.slnx`.

`samples/` is excluded from packing (`PackageValidation`'s `ExactlyTheShippableSetIsPacked` expects **69**, and samples inflating that count is how it was breached before). **If your count changes, something is wrong — investigate rather than updating the constant.**

---

## Task 5: `Rag.NET.Security.AspNetCore` — the last `VerifiedBy: none`

**Files:** `src/Rag.NET.Security.AspNetCore/`, a new test project, `tests/Rag.NET.RepoConventions.Tests/PackageVerificationTests.cs`

The **last** package at `none`. Milestone 4's Definition of Done requires zero before release.

**The paired-guard trap, as in Phase 4.6:** `NoPackageIsVerifiedByNothing` requires every `none` to be listed in `PackagesAllowedToDeclareNone`; **`EveryPackageAllowedToDeclareNoneStillDeclaresNone` fails if a listed package stops declaring `none`.** Change the csproj and remove the ledger entry **in the same commit**.

**Expect more than a label change.** Phase 4.6 moved `Rag.NET.Mcp.Tool` off `none` and found three defects behind it — no pipeline registered, no transport registered, logging over its own protocol stream. **Run the thing before trusting it.** If you find defects, report them; do not quietly work around them.

Adding a test project changes `ExpectedPackageCount`? **No** — test projects are not packable. If the count moves, something else is wrong.

---

## Task 6: Documentation and ROADMAP

- Close the `docs.yml` entry that has been open since 2026-08-02, and Phase 4.5.
- Record **what was actually wrong**: the site did not build, for two independent reasons, and nothing could have reported it.
- **Renovate's "still inert" note is stale** — the app is enabled and opening PRs (`renovate/*` branches exist). Correct it.
- Record the deferred deployment decision with its evidence: the org/URL mismatch and Pages not being enabled.
- Record the **24 npm audit findings** (12 moderate, 12 high) as deliberate debt, with the reason — build-time tooling for a static site, not shipped code.

**Do not tick a Definition-of-Done box this phase did not make true.** If `Rag.NET.Security.AspNetCore` lands, the "no package declares `none`" box *does* become true — verify it rather than assuming, by running `PackageVerificationTests` and reading the skip.

---

## Final verification

```bash
npm ci && npm run build
dotnet build Rag.NET.slnx -c Release --no-incremental
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
```

Then, for `PackageValidation` — it reads packed artefacts, so pack first at the GitVersion-derived version (GitVersion needs the explicit repo path here):

```powershell
$v = dotnet dotnet-gitversion /output json /showvariable SemVer C:\Projects\Prive\Rag.NET
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$v"
```

```bash
dotnet test tests/Rag.NET.PackageValidation.Tests
```

State every count with arithmetic. **The deliverable is a documentation site that builds, a check that keeps it building, and no package left at `VerifiedBy: none`.**
