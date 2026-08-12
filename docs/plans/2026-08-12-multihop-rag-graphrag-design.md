# MultiHop-RAG and the GraphRAG functional guard — design

**Date:** 2026-08-12
**Phase:** 5.2, first slice
**Status:** approved

Two things land together: MultiHop-RAG as the harness's first non-BEIR-format dataset, and the run
that establishes `Rag.NET.GraphRag` functions at all. They are one design because the second needs
the first as substrate, and because the dataset's shape is what forces the structural change in §1.

## What was measured before designing anything

From primary sources on 2026-08-12, not from the paper's prose or a secondary card:

| Fact | Value | Source |
|---|---|---|
| Licence | `odc-by` | Frontmatter of the authors' own HF dataset repo, `yixuantt/MultiHopRAG` |
| Revision pin | `71ac0d0bd1f951d2d6b70311f7d2ae404e1ffa82` | HF API `sha` |
| `corpus.json` | 6,785,567 bytes, MD5 `9b81a85a6acbe0a452b9d51368a2ce87` | Downloaded at that revision |
| `MultiHopRAG.json` | 5,171,312 bytes, MD5 `7408d2a79e977d9c6d1641ac39dc3310` | Downloaded at that revision |
| Documents | 609, all titled | Counted from `corpus.json` |
| Body length | mean 10,340 chars, min 4,770, max 71,034 | Counted |
| Queries | 2,556 | Counted from `MultiHopRAG.json` |
| Null queries | 301, every one with an empty `evidence_list` and answer `"Insufficient information."` | Counted |
| Scored queries | 2,255 | Counted |
| Derived qrels rows | 5,908 | Derived |
| Relevant docs per query | min 2, max 4, mean 2.62 | Derived |
| Evidence join | 6,084 of 6,084 rows join to the corpus on **both** url and title; both bijective over 609 | Verified |

**The licence is a real declaration, not an aggregator's blanket tag.** That distinction is load
bearing here: the HuggingFace tags this repository has already caught being wrong — SciFact's and
TREC-COVID's `cc-by-sa-4.0` — were BEIR applying one label across 19 datasets whose authors declared
nothing. `yixuantt/MultiHopRAG` is the paper's authors declaring a licence for their own data, and
`odc-by` matches SciFact's corpus licence, which this repository already accepted. The upstream
*code* repo carries no licence, which is irrelevant: we want the data, not the code.

**Three findings contradict how the roadmap framed this phase**, and they are the reason the design
looks as it does rather than being a fourth copy of the SciFact descriptor:

1. **The paper chunks at 256 tokens** — verbatim, *"We partition the documents in the MultiHop-RAG
   knowledge base into chunks, each consisting of 256 tokens."* The parity protocol indexes one
   chunk per document at a 256-token limit, so on a 10,340-character article it would index roughly
   the first tenth and discard the rest. A parity run here reports a truncation artefact wearing a
   retrieval number.
2. **There is no published reference for our embedder.** Table 5 covers ada-002, llm-embedder,
   bge-large-en-v1.5, jina-v2, e5-base-v2, voyage-02 and instructor-large. No MiniLM row.
3. **They report MAP@K, MRR@K and Hit@K**, not the nDCG@10 that `BeirReproduction` pins.

So the ROADMAP's claim that MultiHop-RAG offers *"retrieval-stage reference figures rather than
answer-level ones"* is true but misleading in the way that matters: the figures exist for other
models under a different metric, and nothing our run produces can be checked against them. This is
corrected in the ROADMAP alongside this design. It does not block the phase — the phase's own
framing already says the "does it function" goal "does not need MuSiQue, a published baseline, or a
comparable number."

## §1 Protocol applicability

**The codebase cannot currently distinguish "nobody has run this" from "running this would be
meaningless."** Both are written the same way: an empty `BeirReproduction` array, a
`FitsTheNightly: false` budget cell reading NEVER RUN. TREC-COVID's Comparison cell means the
first; MultiHop-RAG's Parity leg would mean the second. Conflating them is not a tidiness
complaint — it is why a descriptor added for Phase 5.3 silently joined `BeirComparisonControlTests`
and killed a cost sweep seven minutes in on 2026-08-12, because `BeirDatasetDescriptor.All` enrols
a dataset in every theory that iterates it.

`BeirDatasetDescriptor` gains `Supports(BeirProtocol)`, defaulting to **all** protocols so every
existing descriptor is unchanged and nothing currently green moves. MultiHop-RAG declares a narrow
set: `Real` and `GraphRag`.

Each theory consults it **before** the provisioning and budget gates. Ordering is deliberate and
follows the precedent already set in `BeirParityTests`: an unprovisioned machine should be told it
is unprovisioned, and an inapplicable case should say it is inapplicable rather than blaming a
missing model file.

The two registries become bidirectional. `BeirRunBudget` and `BeirReproduction` today demand a cell
for every dataset under every protocol; they will demand one for every **applicable** pair and
**refuse** one for an inapplicable pair. A budget entry for a protocol that cannot run is a
contradiction, and nothing would notice it today.

**Against the inert path.** A skip that covers everything is this repository's recurring failure, so
the set of inapplicable pairs is itself pinned in a fast test. Marking a protocol inapplicable to
dodge a failing run fails that test.

## §2 Acquisition and conversion

Acquisition becomes a seam with one postcondition: after it runs, `DirectoryFor(descriptor)` holds
`corpus.jsonl`, `queries.jsonl` and `qrels/test.tsv`. BEIR datasets satisfy it by
download-extract-verify exactly as now. MultiHop-RAG satisfies it by fetching two JSON files at the
pinned revision, verifying each against its own MD5, and converting. `BeirLoader` and everything
downstream — `IrMetrics`, `DocumentRanking`, the harness, the sidecars — never learn the difference.

**Document id is the article URL.** Bijective across all 609 documents and joining all 6,084
evidence rows. Title is equally bijective and was checked, but a URL is self-describing and
independent of file ordering, so a reordered upstream file cannot silently remap ids.

**Query ids are the zero-padded original file position**, assigned before any exclusion, so an id
stays traceable to a line in the source and nothing renumbers if the null set changes.

**The 301 null queries are not filtered out.** All 2,556 queries are written to `queries.jsonl` and
the nulls simply receive no qrels rows. This is BEIR's own convention — SciFact ships 1,109 queries
and judges 300 — and since Phase 3.15 the harness retrieves only judged queries, so the exclusion
happens through machinery already proven rather than through a new branch. The descriptor carries
it as `QueryCount: 2556` / `TestQueryCount: 2255`, where every other dataset carries the same fact.

> This replaces the approach originally proposed and approved, which was to filter the nulls out
> during conversion. Same outcome, less code, and it fails the way the other four datasets fail
> rather than in a way unique to this one.

**Conversion asserts its own output** — 609 documents, 2,556 queries, 5,908 qrels rows over 2,255
queries — and throws rather than writing a short corpus. A converter that silently drops rows
produces a plausible figure from less data.

## §3 The GraphRAG functional guard

**The slice is chosen by queries.** Walk queries in id order, accumulating their evidence documents
until roughly 60 distinct articles are reached, then pin that document-id set explicitly rather than
recomputing it. Choosing by query is what makes the slice work: a multi-hop query references 2–4
articles *because those articles share entities*, so a query-derived slice guarantees the
cross-article entity recurrence community detection needs. Sixty arbitrary articles might share
nothing, and global search would pass while finding nothing.

**It also supplies ground truth**, which is the difference between a smoke test and a guard. Because
the slice derives from queries with qrels, the guard asserts that local search on a slice query
retrieves at least one of that query's known-relevant documents — an assertion a broken pipeline
fails.

**Extraction is cached the way HyDE's hypotheticals are**: identity `openai/gpt-4o-mini@t0.0` with
the temperature in the key, written by an opt-in generation run, replayed refuse-on-miss, never
committed. Temperature 0 for determinism. Roughly 600 chunks, so cents and minutes, and a fresh
runner without the cache skips exactly as the Hyde cells do.

**`GraphRag` becomes the eleventh `BeirProtocol`**, applicable to MultiHop-RAG alone. §1 pays for
itself here: without applicability, an eleventh protocol would demand a budget cell and a
reproduction entry from all four existing datasets — eight new entries declaring NEVER RUN about a
protocol none of them can use.

**Deliberately not tested: `GraphRagRetrievalMode.Auto` and `GraphRagRetrievalOptions.Mode`.** Mode
is never read and `Auto` has no implementation; that is #104, still open. A test asserting
`Mode = Local` routes to local search would be red on arrival. A permanently-failing test is not a
guard and skipping it is the inert path, so the assertion is scheduled to land with #104's fix and
named in the implementation plan rather than left as a comment.

## §4 Testing

Every new guard is shown red, because a guard never seen to fail is a guard that covers nothing.

| Guard | Defect claimed | Red run |
|---|---|---|
| Pinned inapplicable pairs | Marking a protocol inapplicable to dodge a failing run | Mark SciFact `Parity` inapplicable → fails |
| Budget refuses inapplicable cells | A cell for a protocol that cannot run | Add a MultiHop-RAG `Parity` cell → fails |
| Budget demands applicable cells | The TREC-COVID case, re-verified | Delete the `Real` cell → fails |
| Conversion self-assertion | A converter silently dropping rows | Truncate the corpus → throws, does not write short |
| Evidence join totality | An id scheme that does not join | Mutate one evidence URL → fails, does not skip the row |
| Local search ground truth | A broken retrieval path | Return an empty or irrelevant set → fails |
| Community detection | Global search exercised in name only | Force zero communities → fails |

**The dataset is measured, not merely converted.** MultiHop-RAG runs the chunked `Real` protocol
once, giving it a `BeirRunBudget` timing and a `BeirReproduction` pin and satisfying the
per-dataset checklist every dataset here is held to. That figure is also the anchor the later "does
GraphRAG help" comparison will need. The published-reference slot records that **no comparable
figure exists** for `all-MiniLM-L6-v2` under MAP/MRR/Hit, rather than borrowing one from a
different model.

**Baselines, so drift is visible:** `Rag.NET.Tests` 1,336; `Benchmarks.Quality.Tests` 278;
`Benchmarks.Quality.IntegrationTests` 63 passed / 46 skipped; `RepoConventions.Tests` 83.

## Out of scope

- **"Does GraphRAG help"** — the comparative question. This slice establishes it functions. The
  roadmap's own reasoning is that (1) must not wait for (2).
- **HotpotQA, MuSiQue, 2WikiMultiHopQA.** MuSiQue and 2Wiki ship no retrieval corpus and need
  corpus construction, which determines whose published numbers are comparable and is therefore the
  experiment rather than setup. 2Wiki is additionally licence-blocked.
- **#104.** Named above, scheduled, not smuggled in here.
