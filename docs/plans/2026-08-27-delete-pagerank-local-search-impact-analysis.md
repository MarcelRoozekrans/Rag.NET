# Impact Analysis: delete the PageRank-blend local search

Input to `superpowers:writing-plans`. Design: [`2026-08-27-delete-pagerank-local-search-design.md`](2026-08-27-delete-pagerank-local-search-design.md).

## Summary

| Metric | Value |
| --- | --- |
| Date | 2026-08-27 |
| Refactor type | Move + Delete + Rename (three kinds in one change) |
| Targets | 7 |
| Directly affected files | 21 `.cs` |
| Transitively affected | 0 new files (closure terminated at depth 2) |
| Total affected | 21 `.cs` across 5 projects + 1 markdown |
| Breaking changes | 16 |
| Risks identified | 7 |
| Risk level | **Medium** |

**Two counts in circulation are wrong.** `STATE.md` and the ROADMAP say "17 files in four
projects". The design doc corrected that to 19 across five. Both are low: the real figure is
**21 `.cs` files across five projects**, because the rename (T5) reaches
`GraphGlobalSearchBehavior.cs` and `GraphGlobalSearchBehaviorTests.cs`, which touch the options
type but never the deleted members. Each successive count was produced by grepping for the two
*named* members; only grepping the union of all seven targets finds the true set.

## Targets

| # | Target | Kind | Refactor type | Fan-in |
| --- | --- | --- | --- | --- |
| T1 | `GraphLocalSearchBehavior` | public class, 161 lines | Move → test fixture | 12 files |
| T2 | `PageRankWeight` + `PageRankWeightIsFinite` | public property + validator | Delete | 9 files |
| T3 | `LocalSearchDepth` | public property | Delete | 5 files |
| T4 | `LocalTopEntities` | public property | Delete | 4 files |
| T5 | `GraphRagRetrievalOptions` → `GraphRagGlobalSearchOptions` | public class | Rename | 14 files |
| T6 | `GraphRagRetrievalOptionsValidator` | **source-generated** (`[Validate]`, ZeroAlloc.Validation) | Rename (follows T5) | 1 file |
| T7 | `UseGraphRag(retrieval:)` | public extension method | Change interface | 33 call sites repo-wide |

## Transitive closure

Terminated at **depth 2**. Depth 1 is everything referencing a target directly. Depth 2 is
`GraphGlobalSearchBehavior`'s public constructor, whose parameter type changes under T5 — its
dependents are `GraphRagBenchmarks`, `RagBuilderExtensions`, `GraphRagRun` and
`GraphGlobalSearchBehaviorTests`, all already in the set. No round-3 files appeared, because the
deletions remove API rather than reshaping surviving signatures, and the rename is name-only.

## Affected files

### Breaking (16)

| # | File | Change required | By | Complexity |
| --- | --- | --- | --- | --- |
| 1 | `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs` | Delete; content moves to fixture | T1 | Trivial |
| 2 | `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs` | Remove 3 properties + validator method; rename type and file | T2–T6 | Moderate |
| 3 | `src/Rag.NET.GraphRag/RagBuilderExtensions.cs` | Drop `AddSingleton<GraphLocalSearchBehavior>`; update `ThrowIfInvalid(new …Validator())`; retype `retrieval` param | T1,T5,T6,T7 | Moderate |
| 4 | `src/Rag.NET.GraphRag/GraphGlobalSearchBehavior.cs` | Constructor param type rename only | T5 | Trivial |
| 5 | `tests/…Quality.IntegrationTests/GraphRagRun.cs` | Retarget to fixture; local options → fixture options | T1,T5 | **Complex** |
| 6 | `tests/…Quality.IntegrationTests/BeirGraphRagCorpusTests.cs` | Ablation retargets to fixture | T1,T2,T5 | Moderate |
| 7 | `tests/Rag.NET.GraphRag.Tests/GraphLocalSearchBehaviorTests.cs` | **Move to harness with the fixture** (see Decision 1) | T1 | Moderate |
| 8 | `tests/Rag.NET.GraphRag.Tests/GraphRagOptionsValidationTests.cs` | Delete 4 test methods for removed properties; keep `Global*` | T2,T3,T4 | Trivial |
| 9 | `tests/Rag.NET.GraphRag.Tests/GraphRagTelemetryTests.cs` | Constructs the behaviour at `0.3` — retarget or delete | T1,T5 | Moderate |
| 10 | `tests/Rag.NET.GraphRag.Tests/PipelinePlacementTests.cs` | Asserts the type is absent from / addable to the chain | T1 | Moderate |
| 11 | `tests/Rag.NET.GraphRag.Tests/RagBuilderExtensionsTests.cs` | `:103` asserts the DI registration being removed; `:19` asserts options type | T1,T5 | Trivial |
| 12 | `tests/Rag.NET.GraphRag.Tests/GraphGlobalSearchBehaviorTests.cs` | Options type rename only | T5 | Trivial |
| 13 | `tests/Rag.NET.E2ETests/GraphRagFunctionalTests.cs` | `:177` adds the behaviour to a real pipeline | T1,T5 | Moderate |
| 14 | `benchmarks/Rag.NET.Benchmarks/GraphRagBenchmarks.cs` | Benchmarks the deleted code (see Decision 2) | T1–T5 | Moderate |
| 15 | `tests/…Quality.IntegrationTests/GraphRagFunctionsTests.cs` | Options type rename | T5 | Trivial |
| 16 | **`docs/guide/graphrag.md`** | **4 lines in 2 `csharp` fences — fails the doc guard** | T1–T4 | Moderate |

### Update Required (4)

`AnswerArm.cs`, `BeirReproduction.cs`, `BeirRunBudget.cs`, `MultiHopRagAnswerReproduction.cs` —
prose and `<see cref>` only. Compiles either way; wording must say the code now lives in the
harness. `BeirRunBudget.cs:911` also `nameof`s the ablation test method, so it follows any rename
of that method.

### Cosmetic (2)

`src/Rag.NET.GraphRag/LocalSearch/LocalSearchContextBuilder.cs` (comment cites the `w = 0`
finding), `src/Rag.NET.GraphRag/README.md` and `docs/reference/features.md` (prose only — verified
that neither uses a target inside a `csharp` fence).

### Explicitly not touched

`docs/plans/**` and `.superpowers/sdd/**` — historical records. **Verified safe**: the doc guard
excludes exactly `docs/plans` (`ExcludedDirectoryName = "plans"`). `docs/planning/**` is *not*
excluded, but its files contain **0 `csharp` fences** — all target references there are prose in
backticks, which the guard does not extract.

## Risk register

| # | Risk | Files | Severity | Mitigation |
| --- | --- | --- | --- | --- |
| R1 | **Published-package breaking change.** These are public types on a package live on nuget.org since 2026-08-11. Downstream consumers cannot be analysed. | `src/Rag.NET.GraphRag/**` | **High** | Inherent to the decision, not the execution. Pre-v1.0 is when this is cheapest — the reason the phase is sequenced before 6.3. |
| R2 | **The doc guard turns markdown into a build break.** `DocsCodeExamplesTests` extracts `csharp` fences from `docs/**` (minus `docs/plans`) and `src/**/README.md` and resolves every identifier against the real public API. `docs/guide/graphrag.md` uses all four deleted members in fences. | `docs/guide/graphrag.md` | **High** | Update the doc in the **same group** as the deletion. Not a follow-up commit — the build is red between them. |
| R3 | **String-literal assertions the compiler cannot catch.** `GraphRagOptionsValidationTests` asserts on `"PageRankWeight"`, `"LocalSearchDepth"`, `"LocalTopEntities"` inside exception messages. | `GraphRagOptionsValidationTests.cs` | Medium | Those 4 methods are deleted outright; each also *uses* the property, so the compiler does flag them here. Listed because the pattern would be invisible if the usage were removed first. |
| R4 | **Source-generated type renames invisibly.** `[Validate]` generates `GraphRagRetrievalOptionsValidator`; renaming the class renames the generated type, and the failure surfaces in generated code. | `RagBuilderExtensions.cs:72` | Medium | Rename class and update `ThrowIfInvalid` in one atomic edit (design §2). |
| R5 | **The fixture silently diverges from what the figures measured.** A "tidied" copy still computes — just differently — and three pins change meaning with no failure. | fixture + `GraphRagRun.cs` | Medium | Move the 10 unit tests with it (Decision 1); reproduce all three figures before calling done. |
| R6 | **`retrieval:` is a cross-package convention.** `AddRagNet(retrieval:)`, `UseRaptor(retrieval:)`, `UseGraphRag(retrieval:)`; siblings `RaptorRetrievalOptions`, `TagRetrievalOptions`, `RetrievalOptions`. | `RagBuilderExtensions.cs` + 33 call sites | Medium | **See Decision 3 — this contradicts part of the approved design.** |
| R7 | **The E2E test wires the behaviour into a real pipeline.** Not a mock; deleting the type removes a leg of an end-to-end assertion. | `GraphRagFunctionalTests.cs:177` | Low | Decide whether the E2E keeps a local-search leg using the replacement, or drops it. |

## Decisions this analysis resolves

**Decision 1 — `GraphLocalSearchBehaviorTests.cs` moves with the fixture.** Deferred from
brainstorming. 387 lines, 10 tests, all covering the behaviour's own mechanics rather than the
package contract. Two of them —
`HandleAsync_ChunksFromDifferentDocumentsSharingChunkIndex_BothSurvive` and
`HandleAsync_DuplicateChunkWithinOneDocument_CollapsesToHighestScore` — encode precisely the
deduplication defect `BeirReproduction.cs:531` names as affecting the 0.56897 figure. They are the
mechanical guard against R5, and a doc comment is not. Move all 10.

**Decision 2 — RESOLVED 2026-08-27: delete the local-search benchmark, keep the global-search
one.** It is a BenchmarkDotNet perf benchmark of the deleted behaviour, not one of the three pinned
figures, and benchmarking code that no longer ships measures nothing anyone can act on. The file
survives; only its local-search members go.

**Decision 3 — RESOLVED 2026-08-27: rename the type, keep the parameter.** The design (§2)
proposed `retrieval` → `globalSearch`. R6 shows `retrieval:` is a repo-wide convention across three
packages and four sibling options types, so renaming GraphRAG's alone would make it the only
package breaking the pattern for a cosmetic gain. `GraphRagRetrievalOptions` becomes
`GraphRagGlobalSearchOptions`; `UseGraphRag(retrieval:)` keeps its parameter name. **Group 5 stays,
covering the type only.** The design doc §2 has been corrected to match — it carried the superseded
recommendation.

## Execution order

Ordered so the tree is green at every checkpoint. The deletion runs before the rename, so the
rename applies to a smaller surface.

### Group 1 — Add the fixture (additive, nothing breaks)

- New `LegacyPageRankLocalSearch` + its options record in `…Quality.IntegrationTests`
- Move the 10 tests from `GraphLocalSearchBehaviorTests.cs` alongside it (copy, do not delete yet)
- **Checkpoint:** build + the moved tests pass against the copy. **Commit.** The copy is now proven
  equivalent while the original still exists — the only moment that comparison is possible.

### Group 2 — Retarget the measurement harness

- `GraphRagRun.cs`, `BeirGraphRagCorpusTests.cs` → construct the fixture instead of the shipped type
- **Checkpoint:** the three pinned figures reproduce to recorded precision. **This is the gate the
  whole change turns on** — after Group 3 the original is gone and the comparison is impossible.
  **Commit.**

### Group 3 — Delete from `src/` (ATOMIC with the doc update)

- Delete `GraphLocalSearchBehavior.cs`; drop its `AddSingleton`
- Remove `PageRankWeight`/`PageRankWeightIsFinite`/`LocalSearchDepth`/`LocalTopEntities`
- Delete the original `GraphLocalSearchBehaviorTests.cs` and the 4 validation test methods
- Fix `PipelinePlacementTests`, `RagBuilderExtensionsTests`, `GraphRagTelemetryTests`,
  `GraphRagFunctionalTests`, `GraphRagBenchmarks`
- **`docs/guide/graphrag.md` in this same group** — R2: the doc guard fails between deletion and
  doc update, so they cannot be separate commits
- **Checkpoint:** full build, `DocsCodeExamplesTests`, `pack-validate`. **Commit.**

### Group 4 — Prose sweep

- `AnswerArm.cs`, `BeirReproduction.cs`, `BeirRunBudget.cs`, `MultiHopRagAnswerReproduction.cs`,
  `LocalSearchContextBuilder.cs`, `README.md`, `features.md`
- **Checkpoint:** build. **Commit.**

### Group 5 — Rename (ATOMIC; **gated on Decision 3**)

- `GraphRagRetrievalOptions` → `GraphRagGlobalSearchOptions` across all 14 files, and
  `ThrowIfInvalid`'s generated-validator call site in the same edit (R4)
- The `retrieval:` parameter name is **not** touched (Decision 3)
- **Checkpoint:** full build + suite. **Commit.**

## What the compiler does and does not cover

`TreatWarningsAsErrors=true` makes every stale *code* reference a build error — this is why
deletion was chosen over `[Obsolete]`, and it is the completeness guarantee for Groups 3 and 5.

It does **not** cover three things, which is what the checkpoints above exist for: the doc-guard
fences (R2, caught only by `DocsCodeExamplesTests`), the fixture's behavioural fidelity (R5, caught
only by reproducing the figures in Group 2), and prose accuracy (Group 4, caught by nothing —
review only).
