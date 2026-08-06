# XML Documentation Implementation Plan (Phase 4.2's blocker)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Document `Rag.NET.Abstractions`' remaining public surface so `GenerateDocumentationFile` can be enabled and all 66 packages ship IntelliSense XML.

**Architecture:** Everything ours lands first — the test exclusion, an anti-vacuity guard, the 172 members, and a packaging guard. The final `GenerateDocumentationFile=true` flip is a single recorded step gated on an upstream fix.

**Tech Stack:** .NET 10, xUnit v3, Roslyn/regex source inspection.

**Design:** `docs/plans/2026-08-06-xml-documentation-design.md`

---

## What was measured

| Source | Undocumented public members |
|---|---|
| **`Rag.NET.Abstractions`** (hand-written) | **172** |
| `Rag.NET.Abstractions` (`ZeroAlloc.ValueObjects` generated) | ~24 — **not ours** |
| `Rag.NET.RepoConventions.Tests` | 30 — not shipped |
| All 65 other packages | **0** |

Abstractions already carries **337 `<summary>` blocks** — this completes a two-thirds-finished file.

Concentration: `RagOptions` 14, `RetrievalOptions` 8, `TextChunk` 7, `IRagDataManager` 7, `DocumentSummary` 7, `DocumentSection` 7.

## The exemplar already in the repository

`RagOptions.SkipCompression` sits in the same file as 13 undocumented properties and is documented
exactly to standard:

```csharp
/// <summary>
/// Bypass contextual compression for this call even when an
/// <c>IContextualCompressor</c> is registered. Use when raw source
/// text is required (admin tooling, UI citation rendering).
/// </summary>
public bool SkipCompression { get; set; }
```

**What it does, when to use it, why.** Point every batch at this, not at an abstract rule.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`, `TreatWarningsAsErrors=false`.** MA0051 (≤60-line methods), MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- A file watcher edits `.csproj` concurrently — `git status` before committing.
- `PackageValidation`'s `EveryPackageCarriesTheVersionGitVersionDerives` fails on stale `.nupkg` files in `artifacts/packages/` — repack at the GitVersion-derived version before trusting that suite.

**Baselines:** `Rag.NET.Tests` **1173**, `RepoConventions` **37 + 1 skip**, `PackageValidation` **20**.

---

## Task 1: Exclude test projects

**Files:** `tests/Directory.Build.props`

Set `<GenerateDocumentationFile>false</GenerateDocumentationFile>`.

Test projects are not packable and never ship. Documenting 30 members there would produce XML no
consumer can read, purely to satisfy a compiler flag.

This is inert until Task 5 flips generation on — **that is intentional**. It lands now so the flip
is one line rather than one line plus a discovery.

---

## Task 2: The anti-vacuity guard — written BEFORE the documentation

**Files:** create `tests/Rag.NET.RepoConventions.Tests/DocumentationQualityTests.cs`

**Write this first**, so all 172 members are written against a check that already exists rather
than reviewed after the fact.

**Step 1: The rule.** A summary is vacuous when, after normalising, it merely restates the member
name. Normalise by lower-casing, stripping punctuation, and removing leading `gets`, `sets`,
`gets or sets`, `the`, `a`, `an`. Split the member name on camelCase boundaries and lower-case it.
**If the two are equal, the summary adds nothing.**

```
TopK              + "Gets the top k."          -> "top k"  == "top k"   -> VACUOUS
TopK              + "Chunks to retrieve..."    -> "chunks to retrieve"  -> ok
SkipCompression   + "Bypass contextual..."     -> "bypass contextual"   -> ok
```

**Step 2: Prove it fails.** Add a deliberately vacuous summary to a real member, run, confirm red,
revert. **Report the mutation and the test name.** A guard nobody has watched go red is not a
guard — this repository has shipped three of those.

**Step 3: Scope it.** Apply to `src/Rag.NET.Abstractions` at minimum. **State whether you scoped it
wider**, and if not, why.

**This check is a floor, not a ceiling.** It catches "Gets the TopK"; it cannot catch a fluent
sentence that says nothing. Say so in the test's remarks so nobody mistakes passing for good.

---

## Task 3: Document the 172

Work **file by file**, largest first: `RagOptions` (14), `RetrievalOptions` (8), `TextChunk` (7),
`IRagDataManager` (7), `DocumentSummary` (7), `DocumentSection` (7), then the tail.

**Commit per file or per small group.** Do not produce one 172-member commit — it is unreviewable,
and this is the phase where review matters most.

**For each member, document what a consumer cannot infer:**

- **Units and defaults** — `MinScore` is a *similarity score*, not a percentage; `TopK` defaults to 5.
- **Valid ranges, and who enforces them** — does the store clamp, or do we?
- **What `null` means** — `MetadataFilter`, `SystemPrompt`, `Temperature` are all nullable; absent and empty are not the same thing.
- **Interactions** — `SynthesisStrategy` selects which of `MapReduceOptions`/`RefineOptions` is read; that is invisible from the signatures.
- **Side effects and cost** — anything that triggers an LLM call or a store round-trip.

**Do not invent behaviour.** If you cannot tell what a member does from the code, **read its call
sites**; if it is still unclear, **say so and leave it** rather than writing a plausible guess. A
confident wrong doc is worse than a missing one — it is the exact defect this repository keeps
finding.

**Run the Task 2 guard after each batch.**

---

## Task 4: The packaging guard

**Files:** `tests/Rag.NET.PackageValidation.Tests/` — extend the existing suite.

Assert **every packable project's produced `.nupkg` contains its XML documentation file**, read
from inside the package as the other guards do — never from the `.csproj`.

Enabling generation once is not enough. A project added later could ship without it silently, which
is exactly how the current state arose: **nobody chose it, it was simply never set.**

**Prove it fails**: pack with generation off for one project, confirm red, revert.

**This guard cannot pass until Task 5 flips generation on.** Write it, prove it fails for the right
reason, and **skip it with a reason naming Task 5 as the unskip trigger** — or gate it on the
property being enabled. **Say which you chose.** A permanently-skipped guard is a gate that does not
gate.

---

## Task 5: The flip — one recorded step, gated upstream

**Files:** `Directory.Build.props`

```xml
<GenerateDocumentationFile>true</GenerateDocumentationFile>
```

**Do not perform this task until the upstream `ZeroAlloc.ValueObjects` fix has shipped and the
package is updated.** `// <auto-generated />` suppresses analyzer diagnostics but not CS1591, so
~24 generated members would fail the build. The owner decided on 2026-08-06 to **raise it upstream
rather than suppress**.

**When attempting it, verify first:**

```bash
dotnet build Rag.NET.slnx -c Release
```

**0 errors is the gate.** If CS1591 fires on generated files, the upstream fix has not landed —
**stop, do not suppress, report.**

**If it has not shipped when the rest of the phase is done**, close the phase with this task open
and its fallback recorded (narrow `.editorconfig` suppression, or hand-writing the four id types).
The value-object work may also make it moot.

---

## Task 6: Close

Update `docs/planning/ROADMAP.md` and `MILESTONE.md`.

**Record:**

- The measurement that corrected the roadmap: **one project, not sixty-six** — 172 ours, ~24
  upstream, 30 in tests, 0 elsewhere.
- The upstream defect, that it affects **every** consumer of `ZeroAlloc.ValueObjects` enabling
  documentation, and where it was raised.
- **Whether Task 5 flipped or is still gated**, with the fallback and its 6.3 wall.
- The anti-vacuity guard's **stated limit** — it catches name-restatement, not fluent emptiness.

**Do not tick a DoD box this phase did not make true.** If Task 5 is still open, documentation is
written but **not shipping**, and the box stays unticked.

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.Tests
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$(dotnet dotnet-gitversion "$(pwd)" /output json | jq -r '.SemVer')"
dotnet test tests/Rag.NET.PackageValidation.Tests
```

**The deliverable is a consumer seeing a useful tooltip** — not a member count.
