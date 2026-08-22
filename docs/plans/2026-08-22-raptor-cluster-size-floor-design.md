# A Size Floor on RAPTOR's Cluster Count — design

**Date:** 2026-08-22
**Issue:** #345
**Blocks:** Phase 6.2.1's RAPTOR measurement (`docs/plans/2026-08-21-raptor-real-protocol-implementation.md`, Tasks 4–6)
**Status:** design approved in brainstorming; not yet planned

## Goal

Make RAPTOR build a usable tree over a real corpus. Today it cannot build one at all: the
summarisation prompt exceeds the model's context by construction, and even if it did not, the tree
would be too shallow to be the technique it is named after.

## The defect

`SelectClusterCount` picks the cluster count with:

```csharp
GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(count, 10))
```

when `MaxClusters` is null, which is the shipped default. **`k` is capped at 10 at every level,
regardless of how many chunks that level holds.** `ConcatenateChunkTexts` then joins every chunk in
a cluster with `"\n\n---\n\n"` and substitutes the result into `SummaryPrompt`, with **no length
bound of any kind**.

### It fails, and it fails by construction

Under `RaptorTreeScope.Corpus` — the shipped default since #340 — level 1 clusters the whole corpus.
On MultiHop-RAG that is 17,648 chunks into at most 10 groups.

Chunks come from `RecursiveChunkingStrategy` at stock `ChunkingOptions`, where `MaxChunkSize` is
**512 characters**. Using the corpus's own recorded shape — 10,340-character mean article × 609
articles, plus 17,648 × 50 characters of overlap — a chunk averages ~407 characters ≈ ~100 tokens.

| | |
|---|---|
| Largest level-1 cluster | ≥ ⌈17,648 / 10⌉ = **1,765 chunks** |
| Joined length | 1,765 × 407 + 1,764 × 7 = **730,703 characters** |
| As tokens | **≈ 183k** |
| `gpt-4o-mini` context | **128k** |

The *minimum possible* largest cluster already exceeds the context by ~1.4×, and no `k` within the
cap can avoid it. `CachedGraphRagClient` retries five times and throws.

### The same defect, a second time

`CachedGraphRagClient.LongestPrompt`'s doc comment records the community-report prompt reaching
**1,806,352 characters, ~450,000 tokens** — which is why `GraphRagOptions.MaxCommunityReportPromptLength`
exists. RAPTOR has no equivalent.

### Why no test caught it

`RaptorRunTests` builds a 120-leaf corpus. At 10 clusters that is 12 chunks per prompt. **Nothing
between 120 and 17,648 leaves has ever been exercised.** This is the third defect in this package a
green suite could not reach, after #332 and #333, with the same cause each time: the fixtures cannot
produce the input that fails.

### And a prompt bound alone would not be enough

With `k` capped at 10 per level, a corpus tree over 17,648 chunks has roughly **20–40 nodes in
total**. Truncating the prompt would let the build finish and still leave a tree that is not RAPTOR
as published — and Phase 6.2.1's `raptorfiltered` arm (the corpus store with summaries removed)
would be near-identical to `raptorcorpus`, so *"do the summaries help?"* would be answered by a
40-node tree rather than by the technique.

**The fix must produce a non-degenerate tree, not merely a survivable prompt.**

## Why not simply raise `maxK`

Because `SelectK` calls `Fit` for **every k from 1 to maxK**, and each fit is EM over
`count × k × dims × iterations`.

| maxK | fits | approximate work at n = 17,648 |
|---|---|---|
| 10 (today) | 10 | ~10⁹ |
| 133 (`√n`) | 133 | **~10¹¹** |

Three orders of magnitude, single-threaded, *after* the O(n²) UMAP that already runs first. Deriving
`k` and fitting once is ~10⁹ — the same cost as today.

*(Derived from the loop structure, not measured. The gap is wide enough that the conclusion does not
turn on the constant factor, but a plan should not quote these as measurements.)*

**So BIC selection and a high `maxK` are mutually exclusive at corpus scale.** That constraint,
rather than a preference, is what shapes the design.

## §1 — The rule: a floor, not a replacement

`RaptorOptions` gains `TargetClusterSize`, and `SelectClusterCount` computes:

```csharp
// The smallest k that keeps every cluster at or under the target size.
var floor = (int)System.Math.Ceiling(count / (double)options.TargetClusterSize);

int k;
if (floor <= BicMaxK)
{
    // BIC still chooses, exactly as today; the floor can only raise its answer.
    var bic = GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(count, BicMaxK));
    k = System.Math.Max(bic, floor);
}
else
{
    // BIC is unaffordable at this scale — derive k directly and fit once.
    k = floor;
}

k = System.Math.Min(k, count - 1);   // strict decrease, as today
```

`BicMaxK` is the existing hard-coded `10`, given a name.

### The property that matters

**Below the target, today's behaviour is untouched.** When `count <= TargetClusterSize`, `floor` is
1 and `Math.Max(bic, 1) == bic`. Every existing test exercises exactly that regime, so **this fix
should move no existing assertion.** If one moves, that is a finding to report, not an expected
adjustment to absorb.

| level size | floor (target 100) | path | k | cluster size |
|---|---|---|---|---|
| 24 | 1 | BIC | whatever BIC says | unchanged from today |
| 200 | 2 | BIC, floored | `max(bic, 2)` | ≤ 100 |
| **17,648** | **177** | derived | **177** | **~100 chunks ≈ 10k tokens** |

### Why a floor rather than a replacement

Replacing BIC everywhere would impose a fan-out on levels small enough for BIC to choose one
honestly. The floor changes behaviour only where the current behaviour is broken, which is also what
makes the blast radius reviewable.

### Correction, 2026-08-22 — what the floor actually guarantees

The Task 2 review found that this section, and the option's doc comment derived from it, claim more
than the mechanism delivers. **`k >= ceil(count / TargetClusterSize)` bounds the *average* cluster
size, not the maximum.** GMM assignment is free to put a disproportionate share of the points into
one component; nothing in a floor on `k` prevents that.

What is true:

- The floor guarantees **at least** `ceil(count / target)` clusters.
- It **materially reduces the expected maximum** — at the scale that motivated #345, from ~1,765
  chunks on a balanced split to ~100.
- An individual cluster **may still exceed the target** if the assignment is unbalanced.

A hard bound would require splitting oversized clusters *after* assignment. This design deliberately
does not, and whether it is needed is a question **the measurement can answer**: if real corpora
cluster evenly the floor suffices, and if they do not, the evidence will say so. Adding the split
speculatively would be a second mechanism justified by nothing.

Recorded rather than quietly reworded, because the overclaim reached the shipped XML docs before it
was caught, and a reader comparing the two should see that it was corrected rather than wonder which
is current.

## §2 — The option

```csharp
public int TargetClusterSize { get; set; } = 100;
```

Validated in the shape its siblings use — `[GreaterThan(1)]`, since a target of 1 would make every
chunk its own cluster and reintroduce #333's shape from the other direction.

**Chunk count rather than a character budget**, deliberately. It is the same currency as
`MinChunksForRaptor` and `MaxClusters`, so the three options read consistently, and the relationship
between the setting and the resulting tree is legible: halve it, double the fan-out. The cost is
that it is not tokens — an unusual chunker changes what the number means — and the doc comment must
say so, with the arithmetic for the default.

## §3 — `MaxClusters` becomes a cap that cannot force a failure

`MaxClusters` still caps `k` wherever the cap is satisfiable. Where honouring it would produce a
cluster above `TargetClusterSize`, **the floor overrides it**, and telemetry records that it did.

The reasoning: the floor is a correctness bound and `MaxClusters` is a preference. `MaxClusters = 10`
on 17,648 chunks is not a configuration a user can benefit from — it is precisely the configuration
that cannot build.

**This is a documented option not being honoured in one case, and the doc comment must say so
plainly.** Hiding it would be worse than the behaviour: a user whose cap is silently exceeded and
who cannot find out why is in a worse position than one who reads that the cap yields to a
correctness bound.

## §4 — Testing

The failing case is two orders of magnitude beyond any existing fixture, which is *why* #345
shipped. So the test that matters is at a scale nothing has ever run.

- **A level large enough that the old code produces an over-target cluster and the new code does
  not.** Assert the maximum cluster size, not the value of `k` — the size is what the prompt bound
  is about, and asserting `k` would pin an implementation detail.
- **Mutation-checked.** Revert the floor, watch the test fail, restore it, watch it pass, and report
  both observations. Four separate "guards" in this package could not fail for what they claimed
  (#332's regression test, `SetupEmbedder`, `CorpusRebuildCount`, the `raptorfiltered` under-fill
  condition). A fifth is not acceptable.
- **`MaxClusters` yielding to the floor**, with the telemetry tag asserted.
- **`TargetClusterSize` validation** rejects 1 and 0.
- **An existing-behaviour test**: a small level's `k` is unchanged from what BIC alone would pick.

## §5 — What this does not fix

`Umap.Fit` remains brute-force O(n²): at n = 17,648 over 384 dimensions that is ~311M distance
evaluations and a delegate-comparison `Array.Sort` per row, plus a `new (float, int)[n]` allocation
per row — 141,184 bytes, **over the 85 KB large-object-heap threshold** — for roughly **2.5 GB of
LOH traffic**.

**Raising `k` does not help**, because UMAP runs before clustering. A corpus build may still be slow
or memory-hungry, and may fail on memory before it would fail on anything else. **File it
separately; do not fold it into this fix** — it is a different problem in a different method, and
bundling them would make both harder to review.

## §6 — Out of scope

- The UMAP cost above.
- #337, the variance floor. It interacts — `SelectK` saturating near its filter ceiling on
  unstructured input is why the derived path exists — but the floor does not depend on it, and the
  derived path bypasses `SelectK` entirely at the scale where #337 bites hardest.
- #336 and #338.
- Phase 6.2.1's measurement itself, which this unblocks but does not run.

## Decisions taken during brainstorming

| Question | Chosen | Rejected |
|---|---|---|
| How is `k` chosen at corpus scale? | Derive from a target cluster size; keep BIC for small levels | Keep BIC everywhere with a cheaper sweep — a numerical redesign of `SelectK` in a package that has already had two clustering defects. Bound the prompt only — leaves a 20–40 node tree |
| What sets the target? | `TargetClusterSize`, a chunk count, defaulted and configurable | A character budget — self-adjusting but less legible; an internal constant — repeats exactly the pattern that caused #345 |
| `MaxClusters` vs. the floor | The floor wins; telemetry records it | `MaxClusters` wins and the build throws — a user whose corpus grew hits a hard failure. `MaxClusters` wins with a warning — reintroduces #345's failure mode |
