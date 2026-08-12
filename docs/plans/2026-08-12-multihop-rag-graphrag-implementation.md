# MultiHop-RAG and the GraphRAG Functional Guard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Land MultiHop-RAG as the harness's first non-BEIR-format dataset, and prove
`Rag.NET.GraphRag` functions end to end for the first time since it shipped.

**Architecture:** A descriptor declares which protocols apply to it, which lets a dataset join
`BeirDatasetDescriptor.All` — and keep both existing guards — without being dragged into theories
that would produce meaningless numbers. Acquisition becomes a seam whose postcondition is "BEIR
layout present on disk", so a converter satisfies it and nothing downstream changes. GraphRAG's
extraction is cached the way HyDE's hypotheticals are, making a paid one-off run into a permanent
free guard.

**Tech Stack:** .NET 10, xUnit v3, `Rag.NET.Benchmarks.Quality`, `Rag.NET.GraphRag`, ONNX Runtime
(CPU), OpenRouter for extraction.

---

## Before you start

Read `docs/plans/2026-08-12-multihop-rag-graphrag-design.md`. It records what was measured and, more
importantly, three findings that contradict how the ROADMAP framed this phase.

**House rules that will fail your build if you miss them:**

- Analyzers are **errors**. `#pragma`, `[SuppressMessage]`, `NoWarn` and
  `TreatWarningsAsErrors=false` are **forbidden** — satisfy the analyzer instead.
- **No dataset, model, embedding or cache file may ever be committed.** `git add` explicit paths,
  never `-A` or `.`.
- Build must end **0 Warning(s), 0 Error(s)**.
- **You do not merge the PR.** Open it and stop.
- The solution file is `Rag.NET.slnx`, not `.sln`.
- xUnit v3 runs tests in a process named after the assembly. When checking for stray runs, do not
  grep for `dotnet`.

**Baselines — if these move without you intending it, stop and find out why:**

| Suite | Count |
|---|---|
| `Rag.NET.Tests` | 1,336 passed |
| `Rag.NET.Benchmarks.Quality.Tests` | 278 passed |
| `Rag.NET.Benchmarks.Quality.IntegrationTests` | **109 total** — 63 passed / 46 skipped *with* `RAGNET_BEIR_CACHE` set, **58 passed / 51 skipped without it** |
| `Rag.NET.RepoConventions.Tests` | 83 passed |

> **The integration suite's split depends on your environment, so quote the total.** Five cases gate
> on `RAGNET_BEIR_CACHE`, `RAGNET_ONNX_EMBED_MODEL` and `RAGNET_ONNX_EMBED_VOCAB` and skip when they
> are unset. Both splits above were measured on 2026-08-12. **The invariant to check is 109 total
> and your own before-vs-after, not a particular split** — and if a count moves, report it rather
> than adjusting anything to reach it.

**Environment for anything that runs a dataset:**

```bash
export RAGNET_BEIR_CACHE="$TEMP/claude/c--Projects-Prive-Rag-NET/<session>/scratchpad/bench"
export RAGNET_ONNX_EMBED_MODEL="$RAGNET_BEIR_CACHE/model.onnx"
export RAGNET_ONNX_EMBED_VOCAB="$RAGNET_BEIR_CACHE/vocab.txt"
```

**On red runs.** Several steps below tell you to break something deliberately and confirm a test
goes red. Do not skip them and do not use `git checkout --` to undo them — revert by editing the
line back. A guard never seen to fail is a guard that covers nothing, and this repository has
shipped several.

---

## Task 1: `Supports(BeirProtocol)` on the descriptor

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/BeirDatasetDescriptor.cs`
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/BeirDatasetDescriptorTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void EveryExistingDatasetSupportsEveryProtocol_SoThisChangeMovesNothing()
{
    // The default has to be "all", or adding applicability silently gates off cells that four
    // datasets have already measured and pinned.
    foreach (var descriptor in BeirDatasetDescriptor.All)
    {
        foreach (var protocol in Enum.GetValues<BeirProtocol>())
        {
            Assert.True(
                descriptor.Supports(protocol),
                $"{descriptor.Name} stopped supporting {protocol}, which would gate off a measured cell.");
        }
    }
}
```

`BeirProtocol` lives in the IntegrationTests project today. Move it to
`src/Rag.NET.Benchmarks.Quality/BeirProtocol.cs` first — the descriptor cannot reference a test
project. Keep the enum members and their XML docs byte-identical; this is a move, not an edit.

**Step 2: Run it and watch it fail**

```bash
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests -c Release
```

Expected: compile error, `'BeirDatasetDescriptor' does not contain a definition for 'Supports'`.

**Step 3: Implement**

Add to the `BeirDatasetDescriptor` record body:

```csharp
/// <summary>
/// The protocols this dataset can be measured under, or <see langword="null"/> for all of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because "nobody has run this" and "running this would be meaningless" were the
/// same statement.</b> Both were written as an empty <c>BeirReproduction</c> array beside a
/// <c>FitsTheNightly: false</c> budget cell reading NEVER RUN. TREC-COVID's Comparison cell means
/// the first. MultiHop-RAG's Parity leg means the second — its articles average 10,340 characters
/// and the parity protocol indexes one chunk per document truncated at 256 tokens, so the run
/// would score roughly the first tenth of each article and report the result as retrieval quality.
/// </para>
/// <para>
/// Conflating the two is not untidiness. <see cref="All"/> enrols a descriptor in every theory
/// that iterates it, so on 2026-08-12 a descriptor added for Phase 5.3 joined the comparison
/// control and killed a cost sweep seven minutes in against a cold embedding cache.
/// </para>
/// <para>
/// <see langword="null"/> rather than a populated set is deliberate: it keeps the four existing
/// descriptors byte-identical and makes "supports everything" the thing you get by not thinking
/// about it, which is right for a BEIR dataset and wrong only for the shapes BEIR does not cover.
/// </para>
/// </remarks>
public IReadOnlySet<BeirProtocol>? ApplicableProtocols { get; init; }

/// <summary>Reports whether this dataset can be measured under one protocol.</summary>
public bool Supports(BeirProtocol protocol) =>
    ApplicableProtocols is null || ApplicableProtocols.Contains(protocol);
```

**Step 4: Run it and watch it pass**

```bash
dotnet test tests/Rag.NET.Benchmarks.Quality.Tests -c Release
```

Expected: PASS, 279 total (278 + 1).

**Step 5: Commit**

```bash
git add src/Rag.NET.Benchmarks.Quality/BeirProtocol.cs \
        src/Rag.NET.Benchmarks.Quality/BeirDatasetDescriptor.cs \
        tests/Rag.NET.Benchmarks.Quality.Tests/BeirDatasetDescriptorTests.cs
git rm --cached tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirProtocol.cs
git commit -m "feat(quality): let a descriptor declare which protocols apply to it"
```

---

## Task 2: Pin the inapplicable pairs, so a skip cannot be used as an escape

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.Tests/ProtocolApplicabilityTests.cs`

A skip that covers everything is this repository's recurring failure. Applicability is a mechanism
for turning a failing run into a silent skip, so the set of pairs it hides must itself be pinned.

**Step 1: Write the test**

```csharp
[Fact]
public void TheInapplicablePairsAreExactlyThese_SoApplicabilityCannotHideAFailingRun()
{
    // Restated as a literal rather than computed from the descriptors, which is the whole point:
    // computing it from the source it is meant to constrain would agree with any value that
    // source ever takes.
    var expected = new HashSet<string>(StringComparer.Ordinal)
    {
        "multihop-rag/Parity",
        "multihop-rag/HybridBm25",
        "multihop-rag/Hyde",
        "multihop-rag/Reranked",
        "multihop-rag/Comparison",
        "multihop-rag/SemanticKernel",
        "multihop-rag/LangChain",
        "multihop-rag/LlamaIndex",
        "multihop-rag/Haystack",
        "scifact/GraphRag",
        "fiqa/GraphRag",
        "arguana/GraphRag",
        "trec-covid/GraphRag",
    };

    var actual = new HashSet<string>(
        from d in BeirDatasetDescriptor.All
        from p in Enum.GetValues<BeirProtocol>()
        where !d.Supports(p)
        select $"{d.Name}/{p}",
        StringComparer.Ordinal);

    Assert.Equal(expected, actual);
}
```

**Resolved 2026-08-12: this test is authored here and lands in Task 8.**

The point of writing it early is that a pin derived from what the code already does pins nothing —
it would agree with any value the descriptor ever takes. That requirement is already satisfied: the
expected set above is committed as a literal in this plan at `539b0f39`, before any of the code it
constrains exists. Landing the file in Task 8 does not weaken it, because the content cannot be
back-fitted to whatever Task 8 happens to produce.

Carrying a deliberately red test across six tasks would, by contrast, cost something real: every
intervening task verifies against "all suites green", so a standing failure would either mask a new
one or train whoever is running the plan to ignore a red suite. That trade is not worth it.

**So: no commit in this task.** Copy the literal above into
`tests/Rag.NET.Benchmarks.Quality.Tests/ProtocolApplicabilityTests.cs` during Task 8, unchanged
from what is written here. If you find yourself editing the expected set to make it pass, stop —
that is the failure mode this task exists to prevent, and the disagreement is telling you the
descriptor is wrong, not the pin.

---

## Task 3: Make the budget bidirectional

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirRunBudget.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirRunBudgetTests.cs:26-42`

Today `EveryDescribedDatasetHasARecordedCostUnderEveryProtocol` demands a cell for all ten
protocols. It must demand one for every **applicable** pair and **refuse** one for every
inapplicable pair. A budget entry for a protocol that cannot run is a contradiction and nothing
notices it today.

**Step 1: Rewrite the existing test**

```csharp
[Fact]
public void EveryApplicablePairHasARecordedCost_AndNoInapplicablePairHasOne()
{
    foreach (var descriptor in BeirDatasetDescriptor.All)
    {
        foreach (var protocol in Enum.GetValues<BeirProtocol>())
        {
            if (descriptor.Supports(protocol))
            {
                _ = BeirRunBudget.IsGatedOff(descriptor.Name, protocol, out _);
                continue;
            }

            // The other direction, and it is the one that rots silently. A cell left behind after
            // a protocol is declared inapplicable reads as a measurement somebody took.
            Assert.False(
                BeirRunBudget.HasCost(descriptor.Name, protocol),
                $"{descriptor.Name} declares {protocol} inapplicable but still carries a budget " +
                "cell. One of the two is wrong, and a stale cell looks exactly like a measurement.");
        }
    }
}
```

**Step 2: Add `HasCost` to `BeirRunBudget`**

Beside `FitsTheNightly` (around line 493):

```csharp
/// <summary>Reports whether the table holds a cell for one pair, without throwing when it does not.</summary>
/// <remarks>
/// <see cref="IsGatedOff"/> and <see cref="FitsTheNightly"/> both go through <c>Find</c>, which
/// throws on an absent pair — correct for them, useless for asking whether a pair is absent.
/// </remarks>
public static bool HasCost(string datasetName, BeirProtocol protocol)
{
    foreach (var cost in Costs)
    {
        if (string.Equals(cost.Dataset, datasetName, StringComparison.Ordinal) &&
            cost.Protocol == protocol)
        {
            return true;
        }
    }

    return false;
}
```

**Step 3: Red run — both directions**

Direction one, the existing behaviour, re-verified:

```bash
# Delete the "scifact" / BeirProtocol.Parity entry from BeirRunBudget.Costs
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release \
  --filter "FullyQualifiedName~BeirRunBudgetTests"
```
Expected: FAIL, "No run cost is recorded for dataset 'scifact' under the Parity protocol."
Restore the entry by editing it back.

Direction two, the new behaviour. You cannot test this until an inapplicable pair exists, so
**return to this step after Task 6** and:

```bash
# Add a "multihop-rag" / BeirProtocol.Parity cell to BeirRunBudget.Costs
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release \
  --filter "FullyQualifiedName~BeirRunBudgetTests"
```
Expected: FAIL, "declares Parity inapplicable but still carries a budget cell."
Remove the cell again.

**Step 4: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirRunBudget.cs \
        tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirRunBudgetTests.cs
git commit -m "feat(quality): the run budget now refuses a cell for an inapplicable protocol"
```

---

## Task 4: Make the reproduction table bidirectional

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirReproduction.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirReproductionTests.cs:42-48`

Same shape as Task 3. Add `HasReproduction(string, BeirProtocol)` mirroring `HasCost`, then:

```csharp
foreach (var descriptor in BeirDatasetDescriptor.All)
{
    foreach (var protocol in Enum.GetValues<BeirProtocol>())
    {
        if (descriptor.Supports(protocol))
        {
            BeirReproduction.RequireRecordedCase(descriptor.Name, protocol);
            continue;
        }

        Assert.False(
            BeirReproduction.HasReproduction(descriptor.Name, protocol),
            $"{descriptor.Name} declares {protocol} inapplicable but still carries a reproduction entry.");
    }
}
```

**Red run:** delete the `scifact`/`Parity` reproduction entry → expect "No reproduction is
recorded". Restore it.

**Commit:** `feat(quality): the reproduction table now refuses an entry for an inapplicable protocol`

---

## Task 5: Theories consult applicability first

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirParityTests.cs:88-96`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirRealChunkingTests.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirAblationTests.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirComparisonControlTests.cs:73-77`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirSemanticKernelDefaultsTests.cs:123-127`

In every theory, add a third gate **first**:

```csharp
var descriptor = BeirDatasetDescriptor.ByName(datasetName);

// First of the three, because the answer is a property of the dataset rather than of this
// machine. An inapplicable case that reported "no model file" would send the reader to their
// environment for something no environment can fix.
Assert.SkipUnless(
    descriptor.Supports(BeirProtocol.Parity),
    $"{datasetName} does not support the Parity protocol: {BeirProtocolApplicability.Explain(descriptor, BeirProtocol.Parity)}");

Assert.SkipUnless(BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory), BeirHarness.SkipReason);
Assert.SkipWhen(BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.Parity, out var budgetReason), budgetReason);
```

Note `ByName` currently runs *after* the gates in most theories; hoist it. It throws on an unknown
name, which is fine — an unknown name is a bug, not a skip.

**Verify nothing moved:**

```bash
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release
```
Expected: **63 passed / 46 skipped**, unchanged. If skips went up, a real dataset lost a protocol.

**Commit:** `refactor(quality): theories ask whether a protocol applies before asking the machine`

---

## Task 6: The acquisition seam

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/IBeirDatasetSource.cs`
- Modify: `src/Rag.NET.Benchmarks.Quality/BeirDatasetCache.cs:131`
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/BeirDatasetSourceTests.cs`

One postcondition: after `PrepareAsync`, `DirectoryFor(descriptor)` holds `corpus.jsonl`,
`queries.jsonl` and `qrels/test.tsv`.

```csharp
/// <summary>
/// Puts one dataset on disk in BEIR's layout, however that dataset happens to be published.
/// </summary>
/// <remarks>
/// The postcondition is the whole interface: <c>corpus.jsonl</c>, <c>queries.jsonl</c> and
/// <c>qrels/{split}.tsv</c> under the dataset directory. BEIR's own datasets satisfy it by
/// downloading a zip and verifying its MD5; MultiHop-RAG satisfies it by downloading two JSON
/// files and converting them. <see cref="BeirLoader"/> reads a directory and never learns which
/// happened, so nothing downstream — metrics, ranking, sidecars, the budget — acquires a special
/// case for a dataset that is not shaped like BEIR's.
/// </remarks>
public interface IBeirDatasetSource
{
    Task PrepareAsync(string datasetDirectory, CancellationToken cancellationToken = default);
}
```

Refactor the existing download-extract-verify body of `EnsureAsync` into `BeirArchiveSource`
**unchanged** — this is a move. Re-run the quality suites and confirm 278/279 still pass before
going further; a refactor that changes behaviour here would be invisible until a dataset run.

**Commit:** `refactor(quality): put dataset acquisition behind a seam with one postcondition`

---

## Task 7: The MultiHop-RAG converter

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/MultiHopRagSource.cs`
- Test: `tests/Rag.NET.Benchmarks.Quality.Tests/MultiHopRagSourceTests.cs`

**Rules, all of them load-bearing:**

- **Document id is the article `url`.** Bijective over 609 documents; all 6,084 evidence rows join
  on it. `title` is also bijective and was checked, but a URL cannot be silently remapped by a
  reordered upstream file.
- **Query id is the zero-padded original file position** (`mhr-0000` … `mhr-2555`), assigned
  **before** any exclusion, so an id maps to a line in the source file.
- **All 2,556 queries are written.** The 301 nulls simply get no qrels rows. This is BEIR's own
  convention — SciFact ships 1,109 and judges 300 — and since Phase 3.15 the harness retrieves only
  judged queries, so the exclusion runs through proven machinery instead of a new branch.
- **Qrels score is 1.** The judgements are binary; do not invent grades.

**Step 1: Write the failing tests**

```csharp
[Fact]
public void ConversionRefusesToWriteAShortCorpus()
{
    var source = new MultiHopRagSource(corpusJson: OneDocumentOnly(), queriesJson: AllQueries());
    var thrown = Assert.Throws<InvalidOperationException>(() => source.Convert(_directory));
    Assert.Contains("609", thrown.Message, StringComparison.Ordinal);
}

[Fact]
public void EveryEvidenceRowJoinsToADocument_OrConversionFails()
{
    // A row that does not join is a document id scheme that does not work. Skipping it would
    // produce a smaller qrels file and a plausible, wrong number.
    var source = new MultiHopRagSource(corpusJson: AllDocuments(), queriesJson: OneEvidenceUrlMutated());
    var thrown = Assert.Throws<InvalidOperationException>(() => source.Convert(_directory));
    Assert.Contains("does not match any document", thrown.Message, StringComparison.Ordinal);
}

[Fact]
public void TheNullQueriesAreWrittenButUnjudged()
{
    new MultiHopRagSource(AllDocuments(), AllQueries()).Convert(_directory);
    var dataset = BeirLoader.Load(_directory);
    Assert.Equal(2556, dataset.Queries.Count);
    Assert.Equal(2255, dataset.Qrels.Count);
}
```

**Step 2: Run, watch fail, implement, run, watch pass.**

The self-assertion after writing:

```csharp
Expect(documents.Count, 609, "documents");
Expect(queries.Count, 2556, "queries");
Expect(judgedQueries, 2255, "judged queries");
Expect(qrelsRows, 5908, "qrels rows");
```

**Step 3: Commit**

```bash
git add src/Rag.NET.Benchmarks.Quality/MultiHopRagSource.cs \
        tests/Rag.NET.Benchmarks.Quality.Tests/MultiHopRagSourceTests.cs
git commit -m "feat(quality): convert MultiHop-RAG into BEIR's layout, or refuse"
```

---

## Task 8: The descriptor

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/BeirDatasetDescriptor.cs`

Declare it **above** `All` — static initialisation runs in declaration order and a descriptor
declared after `All` is captured as `null` (this cost a `CS8601` during Phase 5.3).

Pinned facts, verbatim, do not re-derive:

| Field | Value |
|---|---|
| Revision | `71ac0d0bd1f951d2d6b70311f7d2ae404e1ffa82` |
| `corpus.json` | 6,785,567 bytes, MD5 `9b81a85a6acbe0a452b9d51368a2ce87` |
| `MultiHopRAG.json` | 5,171,312 bytes, MD5 `7408d2a79e977d9c6d1641ac39dc3310` |
| Licence | `odc-by`, declared by the authors on their own HF repo |
| Documents | 609, all titled |
| Queries / judged | 2,556 / 2,255 |
| Applicable protocols | `Real`, `GraphRag` |

The published-reference slot records that **no comparable figure exists**:

```
"NO PUBLISHED REFERENCE, and this is a determination rather than an omission. The MultiHop-RAG
paper's Table 5 reports MAP@K, MRR@K and Hit@K for ada-002, llm-embedder, bge-large-en-v1.5,
jina-v2, e5-base-v2, voyage-02 and instructor-large. There is no MiniLM row, and this repository
pins all-MiniLM-L6-v2 at nDCG@10. Both the model and the metric differ, so no figure there can
anchor a run here, and borrowing one would compare our embedder against somebody else's. The
ROADMAP's description of MultiHop-RAG as offering 'retrieval-stage reference figures rather than
answer-level ones' is true and misleading in exactly this way; it is corrected alongside this
descriptor."
```

Also add `GraphRag` to `BeirProtocol` in this task, and give the four existing descriptors
`ApplicableProtocols` excluding it.

**Now Task 2's pinned test should go green**, and Task 3's second red run becomes possible. Do both.

**Commit:** `feat(quality): describe the MultiHop-RAG dataset`

---

## Task 9: Earn the budget timing and the reproduction pin

Run the chunked `Real` protocol once. 609 documents, so this is minutes, not the six hours
TREC-COVID's derivation predicted for a bigger corpus — and do not derive a figure, measure it.

```bash
RAGNET_BEIR_LONG_RUNS=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
  -c Release --no-build \
  --filter "FullyQualifiedName~BeirRealChunkingTests&DisplayName~multihop-rag" \
  --logger "console;verbosity=detailed"
```

The reproduction pin starts as `[]`. **Be aware that an empty pin does not fail the run** —
`AssertReproduces` writes a note and returns early. The gate on a first measurement is whatever the
descriptor's target says; with no published anchor there is none, so this first figure is pinned on
its own authority and that must be said in the provenance string.

Replace the budget's `DERIVED` string with the measured wall clock and the reproduction's `[]` with
the measured nDCG@10. Record the cache hit/miss counts the run reports.

**Commit:** `feat(quality): pin MultiHop-RAG's measured chunked figure`

---

## Task 10: The query-derived slice

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagSlice.cs`

Walk queries in id order, accumulating evidence documents until ~60 distinct articles are reached.
Then **pin the resulting document-id set as a literal**, and pin the query ids too. Recomputing it
at run time would let the slice drift with the dataset.

Add a test asserting the slice is self-contained: every pinned query's evidence documents are all
in the pinned document set. A query whose evidence is half outside the slice cannot be retrieved
correctly and would make the guard in Task 12 fail for a reason that is not GraphRAG's fault.

**Commit:** `test(quality): pin the MultiHop-RAG slice the GraphRAG guard runs on`

---

## Task 11: The extraction cache

**Files:**
- Create: `src/Rag.NET.Benchmarks.Quality/GraphExtractionCache.cs`
- Create: `tools/` generation entry point (mirror the hypothetical generator)

Mirror `HypotheticalCache` exactly — read it first and follow its shape rather than inventing one.

- Identity `openai/gpt-4o-mini@t0.0`. **The temperature goes in the key.** A review found that
  sampling settings outside the key silently serve text drawn from another distribution.
- Written by an opt-in generation run; replayed **refuse-on-miss**, failing with the key it wanted.
- **Never committed.** Add the path to `.gitignore` in the same commit as the cache.

**Commit:** `feat(quality): cache GraphRAG extractions the way hypotheticals are cached`

---

## Task 12: The functional guard

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/GraphRagFunctionsTests.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj` (add a `Rag.NET.GraphRag` reference)

Assert, in this order — weakest first so a failure reads as a diagnosis:

1. Extraction produced entities **and** relationships.
2. Entities **recur across articles** — at least one entity appears in two or more slice documents.
   Without this, community detection has nothing to find and step 3 passes vacuously.
3. Community detection produced **more than one** community, each holding **more than one** entity.
4. **Local search on a slice query retrieves at least one of that query's known-relevant
   documents.** This is the assertion that makes it a guard rather than a smoke test.
5. Global search returns results and its result set **differs** from local search's.

**Red runs — do all four:**

| Break this | Expect |
|---|---|
| Force community detection to return zero communities | 3 fails |
| Make local search return an empty set | 4 fails |
| Make local search return documents not in the qrels | 4 fails |
| Point the guard at a single-article slice | 2 fails |

**Do not assert anything about `GraphRagRetrievalOptions.Mode` or `GraphRagRetrievalMode.Auto`.**
Mode is never read and `Auto` has no implementation — #104, open. Such a test would be red on
arrival, and a permanently-failing test is not a guard.

**Commit:** `test(graphrag): prove GraphRAG functions end to end for the first time`

---

## Task 13: Correct the record

**Files:**
- Modify: `docs/planning/ROADMAP.md` (Phase 5.2 section)
- Modify: `docs/reference/features.md` (GraphRAG row)
- Modify: `docs/planning/MILESTONE.md`

The ROADMAP correction, stated plainly:

> **MultiHop-RAG's published figures cannot anchor our run.** This entry called them
> "retrieval-stage reference figures rather than answer-level ones", which is true and misleading.
> Table 5 covers seven embedding models, none of them MiniLM, under MAP@K/MRR@K/Hit@K rather than
> nDCG@10. Both the model and the metric differ. Verified 2026-08-12 by reading the paper.

features.md's GraphRAG row can now say the pipeline is exercised end to end, and should name what
is still unverified: `Mode` and `Auto`, #104.

**Commit:** `docs: correct MultiHop-RAG's reference claim and record GraphRAG as exercised`

---

## Task 14: Full check and PR

```bash
dotnet build Rag.NET.slnx -c Release          # 0 Warning(s), 0 Error(s)
dotnet test Rag.NET.slnx -c Release --no-build
```

Container suites need Docker; if it is down they fail rather than skip, and
`PackageValidation.Tests` compares `artifacts/packages` against the branch's derived version. Both
are environmental. Say so explicitly in the PR rather than reporting "all green".

Open the PR. **Do not merge it.**

---

## Task 8b: Give `ApplicableProtocols` value equality

**Do this after Task 8, once a second descriptor exists and before any third does.**

`BeirDatasetDescriptor` is a `record`, so its synthesized `Equals`/`GetHashCode` compare
`ApplicableProtocols` with `EqualityComparer<T>.Default`. No BCL set overrides `Equals`, so that is
**reference** equality — including `FrozenSet` and `ImmutableHashSet`. Verified empirically during
Task 1's review: two descriptors identical in every field, including protocol content, compare
unequal and hash differently. One member of a value-equality type quietly opting out is the kind of
thing found later as a mysterious failing assertion.

It is latent while only one descriptor sets the property, and it was deliberately left out of
Task 1: Tasks 3–8 call `Supports(...)`, not the property, so the blast radius is the property
declaration plus one descriptor — cheap to change here, not cheap once several descriptors set it.

**Preferred fix:** a `readonly record struct BeirProtocolSet` over a `uint` mask, used as
`BeirProtocolSet?`. Eleven protocols fit comfortably, `Nullable<T>` gives value equality for free,
the type is genuinely immutable rather than a read-only view, and `null` still means "all". It also
lets `ToString` print the protocol names instead of `System.Collections.Generic.HashSet\`1[…]`.

**Write the failing test first**, since the point is that the current behaviour is wrong:

```csharp
[Fact]
public void TwoDescriptorsWithTheSameProtocols_AreEqual()
{
    var a = BeirDatasetDescriptor.SciFact with { ApplicableProtocols = Set(BeirProtocol.Parity) };
    var b = BeirDatasetDescriptor.SciFact with { ApplicableProtocols = Set(BeirProtocol.Parity) };
    Assert.Equal(a, b);
    Assert.Equal(a.GetHashCode(), b.GetHashCode());
}
```

**Two conditions carried from Task 1's review, both load-bearing:**

- **Keep `ARestrictedDescriptorIgnoresLaterMutationOfTheSetItWasGiven`.** It can no longer go red —
  the `FrozenSet<BeirProtocol>?` backing field enforces the aliasing invariant through the type
  system, and removing `.ToFrozenSet()` now fails to compile rather than failing the test. It is a
  tripwire for *this* task: a replacement storage type that is not genuinely immutable is exactly
  what it will catch.
- **Until this task lands, no test or helper may `Assert.Equal` two independently constructed
  descriptors.** It would fail for a reason that reads as nonsense. Note that a round-tripped
  `with { ApplicableProtocols = d.ApplicableProtocols }` *does* compare equal, because
  `ToFrozenSet` on an already-frozen set with the default comparer returns the same instance — so
  the trap only springs on two separately built sets.

**Commit:** `fix(quality): compare a descriptor's applicable protocols by value`

---

## Follow-ups to record, not to do here

- **#104** — `GraphRagRetrievalOptions.Mode` is never read and `GraphRagRetrievalMode.Auto` is
  unimplemented. The routing assertion lands with that fix.
- **"Does GraphRAG help"** — the comparative run against the dense baseline Task 9 pins.
- **HotpotQA, MuSiQue, 2WikiMultiHopQA** — corpus construction is the experiment for two of them,
  and 2Wiki is licence-blocked.
