# RAPTOR under the Real protocol — design

**Date:** 2026-08-20
**Phase:** 6.2.1 — Retrieval & Answer Sweep, first thread after the GraphRAG local-search work
**Status:** design approved in brainstorming; not yet planned

> ## Amended 2026-08-21, after Phase 6.2.3 shipped
>
> This design was written on 2026-08-20, when RAPTOR clustered **per document** and that was the
> shipped behaviour. Phase 6.2.3 (#340, merged 2026-08-21) made **corpus** clustering the default,
> fixed #332 and #333, and left `Boost` and `Filter` deliberately unfixed so this measurement could
> price them as shipped.
>
> **Amended:** §1's arm table and labels; §3's cost section, which assumed a tree is built once;
> §2's ordering argument, marked as overtaken.
> **Unchanged, because 6.2.3 did not touch them:** differences-as-the-deliverable, the
> `raptorfiltered − dense` validation gate, the `Boost` ≈ no-op prediction, §4's pinning, §5's tests.
>
> §"What reading the code found" below is left as written. #331 and the two defects it describes are
> now fixed; it is the record of what was believed before the run, and retconning it would destroy
> the thing this project keeps such records for.

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

### 1. RAPTOR builds its tree per document, not over the corpus — #331

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
| `raptorcorpus` | leaves + summaries | whole corpus — **the shipped default since #340** | `Blend` *(shipped default)* |
| `raptor` | leaves + summaries | per document — **the retired variant, kept as the control** | `Blend` |
| `raptorfiltered` | same as `raptor` | per document | `Blend`, summaries dropped before top-6 |
| `raptorboost` | leaves + summaries | whole corpus | `Boost` @ 1.2, **after 6.2.4's over-fetch fix** |

**The labels inverted on 2026-08-21 and that is not cosmetic.** When this was written, `raptor` was
the shipped configuration and `raptorcorpus` was the hypothetical. #340 reversed them. So
**`raptorcorpus` is the number that gets published as "RAPTOR's result" on this corpus**, and
`raptor` is the control it is differenced against. Publishing the per-document figure as RAPTOR's
would repeat 5.2's misattribution exactly — a variant's number presented as the technique's.

**The broken-`Boost` arm is dropped, and the fix moves to its own phase (6.2.4).** This design
originally measured `Boost` as shipped and only then fixed it, following #247's measure-then-fix
order. That was wrong here, and the operator caught it on 2026-08-21.

The distinction #247 turned on is whether the defect is *empirical* or *structural*. #247 was
empirical — nobody knew how much the shared store cost until it was measured. `Boost`'s defect is
structural and provable from two lines: `RaptorRetrievalBehavior` calls `next(ctx, ct)` with the
context unmodified, and `VectorStoreBehavior` fetches exactly `ctx.Options.TopK`, so the behaviour
only ever sees the truncated top-k and can reorder within it but never promote into it. **Paying for
an answer arm to confirm arithmetic buys evidence for something already known** — the same objection
that reversed the ordering on #331.

The question reading *cannot* answer is whether a **working** `Boost` helps, and that needs the
fixed behaviour. So `raptorboost` measures the fixed configuration against `raptorcorpus`, and the
broken one is not an arm at all.

`Filter` was never a measurement question. Asking for six results and receiving three is a contract
violation; no number is required to justify returning what the caller asked for.

**The control is preserved by configuration, not by leaving a defect in place** — the same answer
6.2.3 used when it kept `PerDocument` selectable. 6.2.4's over-fetch candidate count reproduces
today's behaviour exactly at 1×, so anyone who wants the broken state pinned can still reach it.

`raptorcorpus` is also no longer built harness-side. §"How the corpus-level arm is built" below
described invoking the shipped behaviour over a synthetic whole-corpus context, because no supported
configuration produced a corpus tree. One does now: `TreeScope = RaptorTreeScope.Corpus` with a
`leafStorePath`. The arm measures the product rather than an approximation of it, which is strictly
better evidence and less code.

One ingestion per store shape, not one per arm. `raptor` and `raptorfiltered` read the same
per-document store and differ only in retrieval policy; `raptorcorpus` and `raptorboost` read the
same corpus store and likewise differ only in policy. This
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
- **`raptorboost − raptorcorpus`** — **what a *working* `Boost` buys.** This is the question
  reading cannot answer, and the only reason `Boost` is measured at all. Both arms read the same
  corpus store; the only difference is whether summaries are promoted after over-fetch.
  *(The superseded pairing was `raptorboost − raptor` against a **broken** `Boost`, predicted ≈ 0 on
  mechanical grounds. That prediction is not withdrawn — it is simply not worth an arm, because it
  follows from the code. 6.2.4's fix makes it unfalsifiable by construction rather than by
  measurement, and 1× on the candidate count reproduces it for anyone who wants the number.)*
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

> **Overtaken 2026-08-21.** This section argues measure-then-fix for the `Boost`/`Filter` over-fetch
> defect. That ordering was reversed: the defect is structural rather than empirical, so the fix
> moved to **Phase 6.2.4** and happens *before* this measurement runs. See the amended §1 for the
> reasoning. The section is kept because its account of #247's resolution order is accurate and is
> what the reversal was argued against — deleting it would remove the thing the decision was
> weighed against.


**~~The over-fetch defect is measured before it is fixed.~~** *(Reversed 2026-08-21 — see the
note at the head of this section and the reasoning in §1. What follows is the superseded argument.)*
`raptorboost` pins the shipped
behaviour; then the fix lands, using `MmrBehavior`'s pattern — rewrite `ctx.Options.TopK` to a
candidate count before `next`, reduce after; then `raptorboostfixed` re-measures.

This is #247's order — measured (#274), pinned (#280), then fixed (#311, #312) — and the reason
for it is that the fix then has a number to beat rather than a code reading to appeal to. It is
also what stops "Boost was a no-op" from being an assertion.

Because the fix rewrites `TopK` once for the whole behaviour rather than per mode, **defect 3 is
corrected by the same change**. `Filter` gets a fast-tier regression test, not a paid arm.

**Defect 1, the tree scope, is not fixed here — it is #331, and its schedule has a trigger.**

`raptorcorpus` prices it. The fix is not deferred on judgment but on a number: **if
`raptorcorpus − raptor` is positive and material, `add-phase` fires for #331; if it is small, the
v1.0 answer is a documented limitation.** That is how #239's point 2 resolved — local search
expanding the candidate set was measured at +0.00148 Recall@100 and closed as a documented
limitation, with `docs/guide/graphrag.md` stating plainly that the behaviour adds no candidates.

**Why the fix is a phase rather than a patch**, recorded here because it is what makes deferring it
a cost decision rather than an evasion. The `#302` pattern transfers in shape — debounce the
whole-corpus operation on growth, plus a rebuilder for on-demand "make this current now". What does
not transfer is where the data comes from. `CommunityDetectionBehavior` calls
`graphStore.GetFullGraphAsync(ct)`; GraphRAG owns a store that enumerates everything it holds.
RAPTOR has no equivalent and the vector store cannot stand in:

| Obstacle | Detail |
|---|---|
| No enumeration on `IVectorStore` | `StoreAsync`, `SearchAsync`, `DeleteByDocumentIdAsync`. Nothing returns the corpus. |
| `IChunkLookup` is by key | You would already need every chunk identity to ask for them. |
| `IChunkLookup` returns `TextChunk` | RAPTOR clusters on **vectors**. The lookup cannot return embeddings at all. |
| Not universal | `InMemoryVectorStore` and `FederatedVectorStore` only, forwarded by `ResilientVectorStore`. #318 is open because the remote stores lack it. |

So the fix needs RAPTOR to own persistent state the way GraphRAG owns its graph store — leaf
embeddings written at ingestion, corpus-wide clustering over them, debounced, with a rebuilder, and
a migration story for existing users. Roughly #302 plus #312 combined.

## §3 — Cost, and the gate before spending

> **Amended 2026-08-21 — a cost this design could not have anticipated.** It assumed a tree is built
> once. Under the shipped `Corpus` scope it is not: `CorpusGrowthThreshold` defaults to `0.10`, so
> ingesting MultiHop-RAG's 609 articles (~29 chunks each, 17,648 total) triggers **48 corpus
> rebuilds**, each re-clustering every leaf ingested so far and summarising it. That is the debounce
> working as designed — it is tuned for a live corpus that grows, not for a bulk load.
>
> **The measurement must ingest with the threshold set high enough to suppress rebuilds, then call
> `RaptorTreeRebuilder.RebuildAsync()` once.** That is precisely what the rebuilder documents itself
> for: *"after a bulk load, before measuring, or on a schedule."* One early build is unavoidable —
> `_leavesAtLastBuild` starts at `-1`, so the first ingest always builds — but it is cheap, being
> over one document's chunks.
>
> **Two known limitations bear on the run and neither is fixed:** #336 means corpus summaries
> accumulate in the BM25 index on every ingest-triggered rebuild, with inflated term statistics, so
> **any arm touching hybrid retrieval is contaminated** — the dense arms here are unaffected, and
> the rebuilder path deletes before storing. #338 is irrelevant to a benchmark, which never deletes.


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
- *(Resolved during brainstorming: the tree-scope fix is #331, scheduled on a trigger — see §2.)*

## Decisions taken during brainstorming

| Question | Chosen | Alternative rejected |
|---|---|---|
| Corpus | MultiHop-RAG, reusing #247's arms | A BEIR ranking leg — passage-level relevance means a tree summary can only displace, never help |
| Mode scope | `Blend` + `Boost` | `Blend` only — would have left defect 2 unmeasured |
| Defect 2 handling | Measure as shipped, then fix, then re-measure | Fix first — leaves the broken state pinned by nothing |
| Approach | Shipped variant **and** a corpus-level arm | Shipped only — repeats 5.2's misattribution; fix-first — decides a breaking change with no measurement behind it |
