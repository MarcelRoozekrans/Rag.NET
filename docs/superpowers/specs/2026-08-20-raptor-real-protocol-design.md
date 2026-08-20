# RAPTOR under the Real protocol — design

**Date:** 2026-08-20
**Phase:** 6.2.1 — Retrieval & Answer Sweep, first thread after the GraphRAG local-search work
**Status:** design approved in brainstorming; not yet planned

## Goal

Give `Rag.NET.Raptor` what the GraphRAG path got in Milestone 5: one real run on a real corpus with
a real model, differenced against a control, pinned, and read honestly. RAPTOR is the phase's first
thread because #247 named it — "RAPTOR indexes synthetic summaries beside real chunks the same way
and would take the same fix."

**That prediction is this design's hypothesis, not its premise.** It is tested, and one of the two
findings below already suggests it is only half right.

## What reading the code found, before anything was run

Three defects, all found by reading rather than by running, and all still shipped. They shape the
measurement, so they are stated first.

### 1. RAPTOR builds its tree per document, not over the corpus

`RaptorIngestionBehavior` is an `IIngestionBehavior` that clusters `ctx.EmbeddedChunks`, and
`IngestionContext` carries exactly one `Stream` and one `DocumentMetadata`. It is one document's
chunks. The behaviour's own telemetry says so: `activity?.SetTag("document.id",
ctx.Metadata.DocumentId.Value)`.

The RAPTOR paper clusters across the whole collection. That is the technique's point — a top-level
node summarises themes spanning many documents. A per-document tree can only summarise one
document's own chunks, which is closer to a hierarchical document summary than to RAPTOR.

**This is #300's shape.** That was community detection — a whole-graph operation — running once per
ingested document, 17,648 times on this corpus. This is whole-corpus clustering running once per
document.

The documentation is **not** wrong about it, which matters: `docs/guide/raptor.md` describes
"questions about the overall theme of *a document*". The guide describes the variant accurately.
The package's `Description` carries the paper's name.

**Consequence for the chosen corpus.** MultiHop-RAG's questions require synthesis across articles.
A per-document tree structurally cannot produce a node spanning two of them. So the shipped package
cannot help on this corpus while still paying the displacement cost, because its summaries go into
the shared store and compete for rank. The measurement is expected to come out negative, and the
reason will be **tree scope, not store sharing** — a different cause from #247's, reached by the
same route.

### 2. `Boost` mode cannot promote a summary into the result set

`RaptorRetrievalBehavior` calls `next(ctx, ct)` with `ctx` unmodified, and `VectorStoreBehavior`
fetches exactly `ctx.Options.TopK`. So the behaviour only ever sees the already-truncated top-k, and
`ApplyBoost` multiplies summary scores *within* it. A summary ranked 7th that would beat 6th at
1.2× can never enter. Boost promotes within the set, never into it — the opposite of what
`RaptorRetrievalMode.Boost` documents.

This is #239's shape: an option whose default behaviour undoes its stated purpose, invisible because
every test passes.

`MmrBehavior`, in the same folder, is the correct in-repo template — it rewrites
`ctx.Options.TopK` to a candidate count *before* calling `next`, then reduces after.

### 3. `Filter` mode under-fills

The same root cause. `ApplyFilter` drops summaries out of an already-truncated result set with no
over-fetch: ask for 6, get 6, drop 3 summaries, return 3. The caller asked for six and the
contract silently yields fewer.

## Scope

**In:** the shipped `Blend` default and the shipped `Boost` default, measured on MultiHop-RAG; a
corpus-level tree arm; the over-fetch fix for defects 2 and 3; the pins; the ledger move.

**Out:** fixing the tree scope (defect 1) — priced here, filed and scheduled, not built. `Filter`
gets no paid answer arm; it is covered by the shared fix and a fast-tier regression test. No other
`RaptorOptions` surface is swept: Milestone 6's guard is one real run per feature at its shipped
defaults, and `Boost` is included only because measuring it is what puts a number on defect 2.

## §1 — What is measured, and against what

MultiHop-RAG, 609 articles, 2,255 queries, top-6, gpt-4o-mini @ 0 — the same corpus, embedder,
model and prompt as every graph arm, so the figures land in the same table.

| Arm | Store | Tree scope | Mode |
|---|---|---|---|
| `dense` *(exists, pinned)* | article-only | — | — |
| `raptor` | leaves + summaries | per document *(shipped)* | `Blend` *(shipped default)* |
| `raptorfiltered` | same as `raptor` | per document | `Blend`, summaries dropped before top-6 |
| `raptorboost` | same as `raptor` | per document | `Boost` @ 1.2 *(shipped default)* |
| `raptorcorpus` | leaves + summaries | whole corpus *(the paper's shape)* | `Blend` |
| `raptorboostfixed` | same as `raptor` | per document | `Boost` @ 1.2, **after** the over-fetch fix |

One ingestion per store shape, not one per arm: `raptor`, `raptorfiltered`, `raptorboost` and
`raptorboostfixed` all read the same per-document store and differ only in retrieval policy. This
is what makes their differences mean anything — same store, same depth, same embedder, same model,
same prompt.

### The differences are the deliverable

- **`raptor − raptorfiltered`** — what the per-document summaries do to the answer. Negative means
  displacement: the graph path's finding reproduced.
- **`raptorfiltered − dense`** — **a validation gate, not a result.** It should be ≈ 0. If it is
  not, the setup is wrong and no other number in the table means anything. This is #274's
  byte-identical check reused as a guard: there, 46 of 50 queries hit the answer cache, which is
  keyed on a prompt embedding the context, so a hit proved the filtered context was byte-identical
  to the article-only one.

  **The gate is only valid under one assumption, which the plan must verify rather than presume:**
  that the RAPTOR run's leaf chunks are the same chunks as the `dense` run's — same chunker, same
  settings, same corpus revision — because `RaptorIngestionBehavior` appends summaries and does not
  rewrite leaves (`StoreLeafChunks` defaults to `true`). If the two runs chunk differently, the
  gate compares two things that were never the same and will read as a setup failure when it is
  only a configuration difference. Verify the leaf sets match before spending anything on the
  full sweep.
- **`raptorboost − raptor`** — what `Boost` does as shipped. **Predicted ≈ 0**, for the mechanical
  reason in defect 2. Stated before the run so the run can falsify it.
- **`raptorboostfixed − raptorboost`** — what the over-fetch fix buys.
- **`raptorcorpus − raptor`** — the price of tree scope. The number #247's "RAPTOR takes the same
  fix" assumption never had.

### Why a corpus-level arm at all

Because the alternative repeats a mistake this project made three weeks ago and finished unpicking
two days ago. Milestone 5.2 concluded "GraphRAG does not help on this corpus" from an arm that was
not Microsoft's local search at all; it took #316, #323 and #326 to establish that the real figure
is 0.3459 overall and 0.8603 on inference, and a published finding had to be revised.

Measuring only the shipped variant here would pin a number that reads as a verdict on RAPTOR when
it is a verdict on a per-document variant of RAPTOR. Keeping both arms is the `local` /
`localspec` pattern, and there the comparison between the two *was* the measurement.

### How the corpus-level arm is built

**Public API only, and the library's own clustering code, unmodified.**

`GaussianMixtureModel` and `Umap` are `internal`, and `Rag.NET.Raptor`'s `InternalsVisibleTo`
covers `Rag.NET.Raptor.Tests` and `Rag.NET.Benchmarks` — not
`Rag.NET.Benchmarks.Quality.IntegrationTests`, where the answer arms live. `Rag.NET.GraphRag` sets
no precedent for widening that either; it exposes internals only to its own tests.

None of that is needed. `RaptorIngestionBehavior` and `IngestionContext` are both public, and the
behaviour clusters whatever is in `ctx.EmbeddedChunks`. The corpus arm invokes the shipped
behaviour **once, over the whole corpus's embedded chunks**, with a placeholder `Stream` and
`DocumentMetadata` that only the tree build sees. Leaf chunks keep their own metadata untouched —
the behaviour appends summaries and does not rewrite leaves.

Two things follow, and both are why this shape was chosen over exposing internals or
reimplementing the clustering harness-side:

1. The comparison is honest — both arms run the same clustering, differing only in the scope of
   what is handed to it.
2. If the corpus arm wins, the fix is **"change the scope this behaviour runs at"**, not "write
   better clustering". The measurement points at a specific, small change rather than at a
   redesign.

## §2 — Two defects, and the order they are handled

**The over-fetch defect is measured before it is fixed.** `raptorboost` pins the shipped
behaviour; then the fix lands, using `MmrBehavior`'s pattern — rewrite `ctx.Options.TopK` to a
candidate count before `next`, reduce after; then `raptorboostfixed` re-measures.

This is #247's order — measured (#274), pinned (#280), then fixed (#311, #312) — and the reason
for it is that the fix then has a number to beat rather than a code reading to appeal to. It is
also what stops "Boost was a no-op" from being an assertion.

Because the fix rewrites `TopK` once for the whole behaviour rather than per mode, **defect 3 is
corrected by the same change**. `Filter` gets a fast-tier regression test, not a paid arm.

**Defect 1, the tree scope, is not fixed here.** `raptorcorpus` prices it. The fix is filed as an
issue and scheduled into a phase, per the roadmap's standing rule that debt is recorded with its
origin and then scheduled or re-justified — not left as a note.

## §3 — Cost, and the gate before spending

A pilot at n=50 stratified runs first — #274's shape — to catch setup errors and establish the
sign. The full 2,255-query sweep runs only after the pilot's `raptorfiltered − dense` gate holds.

Tree-build cost is **derived from the pilot, not estimated in this document**. Three timing runs on
2026-08-17 disagreed by 6× on identical inputs and no coefficient has been claimed anywhere since;
this design does not become the first place one is guessed.

What is structural rather than estimated: the per-document arm builds one tree per qualifying
document, the corpus arm builds one tree in total, and the corpus arm should therefore be markedly
cheaper. Documents with fewer than `MinChunksForRaptor` (default 5) chunks get no tree at all, so
the count of qualifying documents is itself a number the pilot reports rather than assumes.

The full sweep runs overnight. `MultiHopRagAnswerReproduction` then replays every answer from cache
and must agree row for row. On the local-search run it replayed 2,556 answers, generated zero, and
found no disagreement — a clean pass, which is what it is supposed to produce and no evidence that
it would stay silent on a real one.

## §4 — Pinning and the ledger

New constants in `AnswerArm`, carrying the same documentation discipline as the existing six: what
the arm isolates, what it is *not*, and what its difference against its control means.

Pin entries go in `MultiHopRagAnswerReproduction`. The unpinned-arm guard from #280 fails in 47 ms
if one is missed, so a forgotten pin is a fast red test rather than a silent gap.

`Rag.NET.Raptor.csproj` moves `VerifiedBy=unit` → `benchmark`, and `features.md` gains its
*Exercised by* pointer. That takes 6.2.1's bare-`unit` count from 22 to 21, against the milestone's
Definition of Done.

## §5 — Testing

Fast tier, no model, both written before the fix and both failing against today's code:

- `Filter` at a given `TopK` returns `TopK` results when enough non-summary candidates exist —
  today it returns fewer.
- `Boost` promotes into the result set a summary that ranked below the cut without it — today it
  cannot.

Plus the pin guard and the reproduction agreement described in §3 and §4.

## What this design does not promise

That RAPTOR is good. Milestone 6's bar is *measured*, and a feature measured and found wanting is a
completion — 5.2 was. The expected outcome here is that the shipped per-document variant costs
accuracy on a multi-hop corpus; the arms exist so that the finding names the right cause and is
falsifiable rather than assumed.

## Open questions for the plan, not for this design

- Whether `raptorcorpus` should also be pinned as a *ranking* figure (nDCG@10) rather than answers
  only. The phase's bar for a retrieval technique is a pinned figure with a control, and answers
  satisfy it; a ranking leg would be additional evidence at additional cost.
- Where the tree-scope fix lands once filed — 6.2.1 alongside the other techniques, or after v1.0.
  That is a scheduling call and belongs to the roadmap, not here.

## Decisions taken during brainstorming

| Question | Chosen | Alternative rejected |
|---|---|---|
| Corpus | MultiHop-RAG, reusing #247's arms | A BEIR ranking leg — passage-level relevance means a tree summary can only displace, never help |
| Mode scope | `Blend` + `Boost` | `Blend` only — would have left defect 2 unmeasured |
| Defect 2 handling | Measure as shipped, then fix, then re-measure | Fix first — leaves the broken state pinned by nothing |
| Approach | Shipped variant **and** a corpus-level arm | Shipped only — repeats 5.2's misattribution; fix-first — decides a breaking change with no measurement behind it |
