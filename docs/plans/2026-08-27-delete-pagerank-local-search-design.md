# Delete the PageRank-blend local search — design

**Phase 6.2.1. Issue #239 point 1's endgame.** Deprecated in `<remarks>` on 2026-08-19 (Task 1 of
the local-search completion plan), unblocked on 2026-08-20 when 6.x.7 published the replacement
figure that was its stated precondition, and picked up on 2026-08-27.

## The problem

`Rag.NET.GraphRag` ships **two** local searches. One is the Microsoft-spec implementation under
`LocalSearch/`, which measured **0.3459 overall and 0.8603 on inference** — the strongest
entity-question result this project has recorded. The other is `GraphLocalSearchBehavior`, a
PageRank blend over dense candidates, which is what Milestone 5.2 actually measured when it
published "GraphRAG does not help on this corpus" from a score of **0.2102**.

That published finding was wrong, and 6.x.7 corrected it. What has not happened is deleting the
code that produced it.

**The blend's default was the whole defect.** `PageRankWeight` defaulted to 0.3 while PageRank
normalises to a mean of 1.6e-5 against cosine's 0.3–0.6, so the behaviour demoted precisely the
chunks it had traversed to. At `w = 0` it reproduced the candidate-set control on **2,255 of 2,255
queries** — the entire −0.02761 was that default. The default is now 0 (#296), which makes the
behaviour a no-op that still ships, still registers, and still appears in the public API.

### Why deletion rather than `[Obsolete]`

This was decided on 2026-08-19 and the reasoning stands. `Directory.Build.props` sets
`TreatWarningsAsErrors=true`, so CS0618 would be a **build error** across the files that
deliberately still reference these members — and `PageRankWeight`'s
`[Must(nameof(PageRankWeightIsFinite))]` validator has source-generated code referencing the
property that cannot be `#pragma`'d around by hand. `[Obsolete]` was not skipped for convenience;
it does not work here.

That same `TreatWarningsAsErrors` is what makes deletion *safe*: the compiler finds every stale
reference, so the change cannot be half-done.

### What the planning notes got wrong

`STATE.md` and the ROADMAP both describe these members as "unregistered from the default pipeline".
That is true of **pipeline placement** and false of **DI**: `RegisterRetrievalBehaviors` still runs
`services.AddSingleton<GraphLocalSearchBehavior>(...)` today, so the type is resolvable from any
container built by `UseGraphRag()` and is publicly constructible besides. The deletion is therefore
somewhat more breaking than the notes imply, and this document is the record of that correction.

## The obstacle, and what it is not

Three pinned figures execute this code:

| Figure | Where | Value |
| --- | --- | --- |
| The `local` answer arm | `MultiHopRagAnswerReproduction`, `GraphRagRun` | 0.2102 |
| `BeirReproduction`'s GraphRag cell | `BeirReproduction.cs` | 0.56897 |
| The blend ablation | `BeirGraphRagCorpusTests.Ablations_UnderTheGraphPath_PageRankWeightZero_AndGraphReach` | `w = 0` reproduces the control on 2,255/2,255 |

Deleting the code they run makes all three unreproducible. **This project treats reproducibility as
load-bearing** — every pinned figure in `BeirReproduction` exists to be re-run — so that cost is
real rather than notional.

**But the affected files are not all obstacles.** First, a correction to the figure this thread
inherited: `STATE.md` and the ROADMAP both say "17 files in four projects". The real count is
**19 `.cs` files across five projects** — 4 in `src/Rag.NET.GraphRag`, 8 in
`Rag.NET.Benchmarks.Quality.IntegrationTests`, 5 in `Rag.NET.GraphRag.Tests`, 1 in
`Rag.NET.E2ETests`, 1 in `Rag.NET.Benchmarks` — plus 8 markdown files. The planning note
predates two phases of work that added references.

Those 19 split in two:

- **Executable** — `GraphRagRun.cs` constructs the behaviour at `PageRankWeight = 0.3` for the
  measured arms, and `BeirGraphRagCorpusTests` runs the ablation. These genuinely need the code.
- **Documentary** — `AnswerArm.cs`, `BeirReproduction.cs`, `BeirRunBudget.cs` and
  `MultiHopRagAnswerReproduction.cs` describe in prose what was measured and when. They reference
  the names in comments and need only their wording updated.

Counting references overstates the problem by roughly threefold. Only the executable set constrains
the design.

## Approaches considered

### Rejected — delete everything and annotate the figures as historical

The tidiest tree, and the cheapest change. Each of the three figures would carry a note naming the
PR that deleted the code it was measured against.

Rejected because it converts three reproducible measurements into three claims, and this project's
whole method is that a published figure can be re-run. 5.2's misattribution was caught *because*
the arms could be re-run and differenced; the discipline that caught it is the one this option
spends.

### Rejected — re-derive against the replacement first

Run the Microsoft-spec local search to produce a live control for the same questions, then delete
the old code and the old figures together.

Rejected as re-buying evidence already held: `localspec` was pinned at 0.3459 on 2026-08-20 against
the same corpus and the same 2,556 queries. This spends a measurement run to produce a number that
exists.

### Chosen — move the behaviour into the measurement harness

Delete it from `src/`, where it is public API on a published package; keep a frozen copy in
`Rag.NET.Benchmarks.Quality.IntegrationTests`, where it is a measurement fixture.

This separates two things the current arrangement conflates. **What users install** should carry one
local search — the one that was measured properly. **What reproduces this project's published
figures** has to carry whatever those figures were measured against, forever, including code that
was wrong. Those are different artifacts with different lifetimes, and the only reason they were
ever the same file is that nobody had needed to tell them apart.

## Design

### 1. What leaves `src/Rag.NET.GraphRag`

- `GraphLocalSearchBehavior.cs` — deleted (161 lines).
- Its `AddSingleton<GraphLocalSearchBehavior>` registration in `RegisterRetrievalBehaviors`.
- Three properties on the public options type: `PageRankWeight` (with its
  `PageRankWeightIsFinite` validator), `LocalSearchDepth`, `LocalTopEntities`.

`LocalSearch/` is untouched. `GraphGlobalSearchBehavior`, `GraphChunkRoutingBehavior` and
`GraphChunkStore` are untouched.

### 1a. The three properties go together, and that is not scope creep

`LocalSearchDepth` and `LocalTopEntities` are read **only** by `GraphLocalSearchBehavior` — verified
by grep across `src/`. The replacement carries its own `LocalSearchContextOptions`. So deleting the
behaviour orphans them, and leaving them behind would ship two public knobs that configure nothing.

That is precisely the failure mode 6.2.6 removed `UseAuditLog()` to avoid: a setting that reads as
configured while doing nothing is worse than no setting, because it cannot be discovered by testing —
it fails silently and only at the point someone relies on it.

### 2. `GraphRagRetrievalOptions` becomes `GraphRagGlobalSearchOptions`

After the removal, the type's only consumer in `src/` is `GraphGlobalSearchBehavior`, and its
remaining members are all `Global*`. The name would overpromise.

This is the change's largest blast radius and the part most worth sequencing carefully:

- `UseGraphRag(retrieval: ...)`'s parameter type changes. **The parameter name stays `retrieval`.**
  This section originally proposed renaming it to `globalSearch`; the impact analysis found that
  `retrieval:` is a repo-wide convention — `AddRagNet(retrieval:)`, `UseRaptor(retrieval:)`,
  `UseGraphRag(retrieval:)`, alongside sibling types `RaptorRetrievalOptions`,
  `TagRetrievalOptions` and `RetrievalOptions` — so renaming GraphRAG's alone would make it the
  only package breaking the pattern, for a cosmetic gain. Decided 2026-08-27: rename the type,
  keep the parameter.
- The source generator emits `GraphRagRetrievalOptionsValidator` from the `[InclusiveBetween]` and
  `[Must]` attributes; that generated type renames with it. **`ThrowIfInvalid(new
  GraphRagRetrievalOptionsValidator()...)` in `RagBuilderExtensions` must be updated in the same
  commit as the rename**, or the build breaks on generated code, which is confusing to diagnose.

### 3. The harness copy is a fixture, not an implementation

The behaviour lands in `Rag.NET.Benchmarks.Quality.IntegrationTests` as `LegacyPageRankLocalSearch`.

**It must be behaviourally byte-faithful.** Its only purpose is to reproduce three figures to their
recorded precision; a copy that is "cleaned up on the way in" reproduces nothing and the difference
would not be obvious. So it moves verbatim apart from the namespace, the type name, and whatever
minimal shape change the removal of the shared options type forces (see §3a).

It carries a doc comment stating plainly that it is a frozen measurement fixture reproducing figures
from code deleted in this change, that it is not maintained, and that improving it invalidates three
pins. Without that, the next person to read it sees dead-looking code in a test project and tidies
it — and the failure is silent, because the figures still compute, just differently.

### 3a. It needs its own options

`GraphRagRetrievalOptions` is losing the three properties the behaviour reads, so the fixture cannot
take the shipped type. It gets a small test-local record carrying `PageRankWeight`,
`LocalSearchDepth` and `LocalTopEntities`, with the defaults **as they were when the figures were
measured** — `PageRankWeight = 0.3`, not the current 0. `GraphRagRun` already pins 0.3 explicitly,
so this is recording an existing intent rather than introducing one, but the fixture's default
should not be the value that makes it a no-op.

### 4. Documentation

`docs/guide/graphrag.md` currently documents the blend and its deprecation. It should state that the
PageRank blend was removed, name the replacement, and keep the finding — that the behaviour added no
candidates and that its default cost quality — because that finding is why the code is gone and is
worth more than the code was. `src/Rag.NET.GraphRag/README.md` and `docs/reference/features.md` need
the same treatment.

Historical planning documents under `docs/plans/` and `.superpowers/sdd/` are **not** updated. They
are records of what was true when written.

## Testing

- **The compiler is the completeness check.** `TreatWarningsAsErrors` means any missed reference is
  a build error, across all five affected projects. This is the guarantee that makes a deletion of
  this size tractable.
- **The three pinned figures must reproduce through the fixture, to their recorded precision.** This
  is the real regression test and the only one that catches an unfaithful copy. It is also the one
  that must run before the change is called done, not after.
- **`pack-validate`** catches the public-surface change on the published package.
- **Unit tests for the deleted behaviour** — `GraphLocalSearchBehaviorTests.cs` — disposition is
  deferred to `refactor-analysis`, which will report which assertions cover the reproduction path
  (and should move with the fixture) versus the shipped-package contract (and should go).

## Out of scope

- **Any change to the Microsoft-spec local search.** It is the replacement, it is measured, and it
  is not being touched.
- **Re-measuring anything.** No new figures. The replacement's 0.3459 was pinned 2026-08-20.
- **The second-corpus RAPTOR arm** decided on 2026-08-27 — separate thread in the same phase.
- **Other 6.2.1 sweep threads**: HyDE, reranking, hybrid BM25, late chunking, SPLADE, the three
  answer engines, the vector-store parity leg, the pipeline-parity test.

## Risks

| Risk | Mitigation |
| --- | --- |
| The fixture copy silently diverges, and three figures quietly change meaning | Reproduce all three to recorded precision before calling the change done; the fixture's doc comment names the stakes |
| The rename and the deletion land together and a failure is hard to attribute | `refactor-analysis` produces the execution order; deletion before rename, so the rename applies to a smaller surface |
| Generated validator breaks the build in a confusing way | Update `ThrowIfInvalid`'s call site in the same commit as the rename (§2) |
| Someone later "revives" the blend from git history | The finding is documented in `graphrag.md` with its measurement, not just the removal |
