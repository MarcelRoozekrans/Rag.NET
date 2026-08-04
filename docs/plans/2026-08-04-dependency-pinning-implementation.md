# Dependency Pinning & Renovate Implementation Plan (Phase 4.8)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Pin every dependency version through Central Package Management so the floor each of the 66 packages publishes is chosen rather than decided by pack timing, and configure Renovate to propose upgrades as reviewable PRs.

**Architecture:** One `Directory.Packages.props` holds a `<PackageVersion>` per dependency, pinned at what restores today. Every `<PackageReference>` across 131 projects loses its `Version` attribute. Correctness is proven by diffing the produced nuspecs before and after — not by the build passing.

**Tech Stack:** .NET 10, Central Package Management (`ManagePackageVersionsCentrally`), `dotnet pack`, Renovate.

**Design:** `docs/plans/2026-08-04-dependency-pinning-design.md`

---

## Measured scope — corrections to the design

The design said "~120 references across ~66 projects". That was `src/` alone. **Measured repo-wide:**

- **497 `PackageReference` entries carrying a `Version` attribute, across 131 `.csproj` files** (`src/`, `tests/`, `benchmarks/`, `samples/`).
- **100 distinct package+version pairs**, of which 4 packages appear at **more than one version** — CPM permits only one, so each needs a deliberate decision (Task 2).
- **Zero multi-line `<PackageReference>` elements** — every one has `Include` and `Version` on the same line, so a line-oriented edit is safe. Verify this still holds before relying on it.

## Ground rules for every task

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`, `TreatWarningsAsErrors=false`.** MA0051, MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A`** — explicit paths only. No `.nupkg` committed; `artifacts/` is gitignored.
- **Never pipe build or test output through `head`/`tail`/`grep`** — that hid two real compile errors on a recent branch here.
- **An IDE/file watcher on this machine edits `.csproj` files concurrently** — it has stripped `ProjectReference` lines twice. **Run `git status` before every commit** and stage explicit paths.
- **Do not pass an arbitrary `-p:Version` when packing** — `EveryPackageCarriesTheVersionGitVersionDerives` re-derives it and rejects invented values.

**Baselines:** `Rag.NET.Tests` 1151, `PackageValidation` 20, `RepoConventions` 33 + 1 skip, `Storage.Sqlite.Tests` 78, `Resilience.Tests` 95, `Caching.Tests` 2, `Parsers.Office.Tests` 19, `DataProviders.Microsoft365.Tests` 70, `VectorStores.Qdrant.Tests` 14 (Docker tier).

**Branch:** `feature/pin-dependency-versions`, cut from the Qdrant fix so the build is green. It stacks on PR #20.

---

## Task 1: Capture the baseline — BEFORE touching anything

**If this task is not done first, the phase has no way to prove it changed nothing.** There is no recovering the baseline afterwards.

**Files:**
- Create: `docs/plans/2026-08-04-nuspec-baseline.txt`

**Step 1: Pack the current tree.**

```bash
dotnet pack Rag.NET.slnx -c Release -o artifacts/baseline
ls artifacts/baseline/*.nupkg | wc -l      # expect 66
```

**Step 2: Extract every dependency declaration from every nuspec**, sorted deterministically:

```bash
for f in artifacts/baseline/*.nupkg; do
  pkg=$(basename "$f" .nupkg)
  unzip -p "$f" '*.nuspec' | grep -o '<dependency id="[^"]*" version="[^"]*"' \
    | sed "s|^|${pkg%%.[0-9]*} |"
done | sort > docs/plans/2026-08-04-nuspec-baseline.txt
wc -l docs/plans/2026-08-04-nuspec-baseline.txt
```

**Step 3: Sanity-check the baseline is real.** It must contain `Qdrant.Client` at a concrete version and `Microsoft.Data.Sqlite` at `10.0.10`. If the file is empty or tiny, the extraction is wrong — **fix it now**, because a broken baseline will "match" anything later and the phase's only correctness test becomes theatre.

**Step 4: Commit it.**

```bash
git add docs/plans/2026-08-04-nuspec-baseline.txt
git commit -m "docs(plans): capture the shipped dependency floors before pinning"
```

**Report:** the line count, and the two spot-checked values.

---

## Task 2: Resolve the four version conflicts, deliberately

CPM permits **one version per package**. Four packages currently appear at several. Each needs a decision recorded before Task 3 encodes it.

**Measured today:**

| Package | Versions in use | Where | Decision |
|---|---|---|---|
| `Microsoft.Data.Sqlite` | `10.*`, `10.0.10` | **`src/Rag.NET.Graph`, `src/Rag.NET.Security`** (`10.*`), `src/Rag.NET.Storage.Sqlite` (`10.0.10`) | Pin **10.0.10** — `10.*` already resolves there, so no shipped floor moves |
| `Microsoft.Extensions.DependencyInjection` | `9.*`, `10.0.5`, `10.0.10`, `10.*` | tests + samples only | Pin at what `10.*` resolves to |
| `Microsoft.Extensions.Logging` | `10.*`, `10.0.10` | tests only | Same |
| `Microsoft.Extensions.AI.OpenAI` | `9.*`, `10.*` | `samples/Rag.NET.Sample` (`9.*`), benchmarks + `tests/Rag.NET.Testing` (`10.*`) | Pin at what `10.*` resolves to |

**Only `Microsoft.Data.Sqlite` touches `src/`, and there it is a no-op.** The 9.x/10.x splits live entirely in non-packable projects, so unifying them cannot move a published floor — but it *can* change how a test or sample behaves.

**Step 1: Verify the table before acting.** Re-run the conflict scan; if it disagrees with the table, the table is stale and the scan wins:

```bash
grep -rho 'PackageReference Include="[^"]*" Version="[^"]*"' --include=*.csproj src/ tests/ benchmarks/ samples/ \
  | sed 's/PackageReference Include="//;s/" Version="/|/;s/"$//' | sort -u \
  | awk -F'|' '{c[$1]++; v[$1]=v[$1]" "$2} END {for(k in c) if(c[k]>1) print k" ->"v[k]}'
```

**Step 2: Record the decisions** in `docs/plans/2026-08-04-nuspec-baseline.txt` as a trailing comment block, or a sibling note — whichever reads better.

**Step 3: If unifying a 9.x project to 10.x breaks its tests, stop and report.** `VersionOverride` on that single `PackageReference` preserves the old version under CPM and is the correct escape hatch — but it is a smell, so use it only when a test genuinely fails, and record each one as debt.

---

## Task 3: Create `Directory.Packages.props`

**Files:**
- Create: `Directory.Packages.props` (repository root)

**Step 1: Generate the pinned set from what actually restores**, not from the `.csproj` text — a `10.*` must become the concrete version NuGet chose:

```bash
dotnet restore Rag.NET.slnx
```

Then derive each package's resolved version (`dotnet list <proj> package` per project, or read `obj/project.assets.json`). **Pin at the resolved version.** This is what makes the nuspec diff in Task 5 come out empty.

**Step 2: Write the file:**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Azure.Identity" Version="…" />
    <!-- … ~96 entries, alphabetical … -->
  </ItemGroup>
</Project>
```

Keep it **alphabetical** — it is now the single review surface for every dependency in the repository, and Renovate will edit it constantly.

**Leave `CentralPackageTransitivePinningEnabled` off.** Turning it on pins transitive dependencies too, which would change the shipped nuspecs — precisely what Task 5 forbids.

**Step 3: Do not build yet.** The tree is inconsistent until Task 4 removes the `Version` attributes; NU1008 is expected.

---

## Task 4: Strip every `Version` attribute

**Files:** all 131 `.csproj` under `src/`, `tests/`, `benchmarks/`, `samples/`.

**Step 1: Confirm the single-line assumption still holds** (it did at planning time — zero multi-line elements):

```bash
grep -rA1 '<PackageReference Include="[^"]*">$' --include=*.csproj src/ tests/ benchmarks/ samples/ | grep -c 'Version='
```

Expected `0`. **If non-zero, a line-oriented edit will corrupt those files — handle them by hand.**

**Step 2: Remove the attribute**, leaving everything else — `PrivateAssets`, `ExcludeAssets`, `IncludeAssets` — untouched:

```
<PackageReference Include="Foo" Version="1.2.3" />   →   <PackageReference Include="Foo" />
```

**`PrivateAssets` and `ExcludeAssets` must survive verbatim.** Those six attributes on the analyzers are the only reason six analyzer packages stay out of every consumer's dependency closure — verified in Phase 4.1, and easy to destroy with a careless regex.

**Step 3: Build.**

```bash
dotnet build Rag.NET.slnx -c Release
```

**0 warnings, 0 errors.** NU1008 means a `Version` attribute survived somewhere — find it; the error names the project.

**Step 4: Run the suites** at the baselines listed above.

**Step 5: `git status`, then commit explicit paths.** Expect ~132 files. The watcher may have touched others — **do not stage anything you did not change.**

---

## Task 5: The correctness test — the nuspec diff

**This is the task the phase exists to pass.** A green build proves the code compiles against pinned versions; it says nothing about whether a *published dependency floor moved*.

**Step 1: Repack and extract, identically to Task 1:**

```bash
dotnet pack Rag.NET.slnx -c Release -o artifacts/after
for f in artifacts/after/*.nupkg; do
  pkg=$(basename "$f" .nupkg)
  unzip -p "$f" '*.nuspec' | grep -o '<dependency id="[^"]*" version="[^"]*"' \
    | sed "s|^|${pkg%%.[0-9]*} |"
done | sort > /tmp/nuspec-after.txt
```

**Step 2: Diff against the baseline.**

```bash
diff docs/plans/2026-08-04-nuspec-baseline.txt /tmp/nuspec-after.txt
```

**Expected: no output.** Every one of the 66 packages must declare exactly the dependency versions it declared before.

**Step 3: If the diff is non-empty, do not adjust the baseline.** Each differing line is a published contract that moved. Investigate, fix the pin, and re-verify. **Report every difference you had to reconcile and why** — a silently-edited baseline turns this phase's only real test into theatre.

**Step 4: Report the diff output verbatim, even when empty.**

---

## Task 6: The standing guard

**Files:**
- Create: `tests/Rag.NET.RepoConventions.Tests/DependencyPinningTests.cs`

**Step 1: Write the test.** Assert that **no `.csproj` in the repository carries a `Version` attribute on a `PackageReference`**, and that every `PackageReference` id has a corresponding `<PackageVersion>` in `Directory.Packages.props`. Read the raw project files — `RepoConventions` tests read source, not compiled output, by design.

Consider also asserting **no `<PackageVersion>` carries a floating `*`**, which is the actual regression this phase prevents.

**Step 2: Prove it fails.** Reintroduce `Version="1.*"` on one `PackageReference`, run, confirm **red**, revert. **Report the test name that went red.** This repository has shipped three guards that could not fail; the standing rule is that a guard nobody has watched go red is not a guard.

**Step 3:** `RepoConventions` goes 34 → 35 or 36. State the number.

---

## Task 7: Renovate

**Files:**
- Modify: `renovate.json`
- Modify: `docs/reference/ci.md`

**Step 1: Configure** batched non-major updates on a weekly schedule, and one PR per major. Keep the existing `dependencies` label and `:semanticCommits` — Renovate's own commits must pass `commitlint`.

**Step 2: Document the enable procedure** in `docs/reference/ci.md`. **Enabling the Renovate GitHub App is the repository owner's action and cannot be done from here**, so the config ships **inert**.

**Step 3: Record two claims separately and do not conflate them** — *pinning is delivered and provable*; *upgrade automation is configured and unexercised*. Only the first can be demonstrated by this work. Phase 4.1 recorded `renovate.json` as inert for exactly this reason; do not now claim more than it did.

---

## Task 8: Close the phase

Update `docs/planning/ROADMAP.md` and `docs/planning/MILESTONE.md` as **Phase 4.8**, matching how 4.1 and 4.7 closed.

Record:

- The measured scope: **497 references across 131 projects**, ~96 pinned versions — and that the design's "~120 across ~66" was `src/` only.
- **The nuspec diff result** — the phase's actual evidence.
- **The four conflicts and how each was resolved**, plus any `VersionOverride` used, each as debt.
- That the trigger was `main` going red with no commit pushed, and that pinning **does not prevent deprecations** — it changes them from a red `main` into a reviewable PR.
- Renovate configured but **inert** until the app is enabled.

**Do not tick a DoD box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release
dotnet pack Rag.NET.slnx -c Release -o artifacts/final
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.PackageValidation.Tests
diff docs/plans/2026-08-04-nuspec-baseline.txt <(…extract from artifacts/final…)
```

The diff being empty is the deliverable. Everything else is a precondition for trusting it.
