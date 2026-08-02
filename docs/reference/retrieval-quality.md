---
id: retrieval-quality
title: Retrieval Quality
sidebar_position: 4
---

# Retrieval Quality

**This page is about accuracy. [Benchmarks](./benchmarks.md) is about speed.** They are deliberately
separate documents with deliberately separate names, because the two questions have nothing to do
with each other: a retriever can be fast and wrong, and the numbers below say nothing about latency
or allocation. If you are looking for microseconds and kilobytes, you want the other page.

There is a third thing that is neither: `EvaluationDatasetBuilder` and the RAGAS metrics
([Evaluation](../guide/evaluation.md)) score *your* corpus with synthesised questions. That is the
right tool for iterating on your own data, but it can only show that a change moved a number — never
that the number is right. This page exists to answer the other question: does Rag.NET's retrieval
path compute what the published research computes?

## The measurement

**Three datasets, `all-MiniLM-L6-v2`, dense retrieval** — the anchor every other number on this
page hangs off; what BM25 hybrid, HyDE and a cross-encoder reranker each do to that anchor is
measured separately, in [the ablation table](#the-ablation-table). The parity legs were measured in Phase
3.12 (2026-07-30/31) and verified unmoved after Phase 3.16 changed the chunker; the SciFact and
ArguAna real legs were re-measured 2026-07-31 under Phase 3.16's packing chunker, and FiQA's real
leg — the one leg that had never run — was measured for the first time by Phase 3.15 (2026-08-02)
under the same chunker, which is the chunker the numbers below describe. Every number below came out of Rag.NET's own path —
`OnnxEmbeddingGenerator` embeds, `InMemoryVectorStore` stores and scores cosine, `DocumentRanking`
aggregates chunks to documents, `IrMetrics` scores. No component was built for the benchmark.

### Two protocols, and why every number has one attached

| Run | What it indexes | Compared against |
|---|---|---|
| **parity** | one chunk per document, truncated at 256 tokens — BEIR's own setup | the **published** figure |
| **real** | `RecursiveChunkingStrategy` at stock `ChunkingOptions`, max-pooled back to documents | **our own parity run**, and nothing published |

The parity run answers *is the harness correct*. The real run answers *what does this library
actually do to a corpus*, and its number is deliberately **not** compared to anyone's published
figure — that would be a number produced under one protocol judged against a reference produced
under another, which is the single error this measurement exists to avoid. Both legs run in the same
process off the same embedding cache, so the only thing between the two figures is the chunking.

### The numbers

The two protocols sit in adjacent columns on purpose. The first three number columns answer "does
this match the literature"; the last two answer "what does chunking do". Read within a group, never
across the boundary.

| Dataset | parity nDCG@10 | published | delta vs published | real nDCG@10 | delta vs **our** parity |
|---|---:|---:|---:|---:|---:|
| **SciFact** | **0.64593** | 0.64508 | +0.00085 | **0.67742** | **+0.03148** |
| **FiQA** | **0.37086** | 0.36867 | +0.00219 | **0.35569** | **−0.01517** |
| **ArguAna** | **0.50432** | 0.50167 | +0.00265 | **0.47559** | **−0.02873** |

**The right-hand column is compared to the left-hand one and to nothing else.** There is no published
nDCG@10 for "chunked with Rag.NET's defaults and max-pooled", none is invented here, and nothing in
the literature says what chunking ought to do to nDCG on these corpora — which is why the run exists.

**All three real deltas now exist, for the first time, and their signs are still the measurement's
most useful result:** Rag.NET's default chunking **helps SciFact by +0.0315 and hurts ArguAna by
−0.0287 and FiQA by −0.0152**. Under the pre-3.16 fragmenting chunker SciFact's and ArguAna's
deltas were +0.0100 and −0.0784 — the packing fix improved both real numbers while leaving both
parity numbers untouched — and FiQA's real leg has only ever been measured under the fixed chunker.
See
[what chunking does, in both directions](#what-chunking-does-to-the-numbers-and-it-goes-both-ways).

**Only the SciFact and ArguAna parity rows are under nightly regression guard.** The others cost more
than the `env-gated` job's 120 minutes allow and are now opt-in behind `RAGNET_BEIR_LONG_RUNS`, which
skips them with their measured cost and the command that runs them; see
[what the nightly actually measures](./ci.md#what-the-nightly-actually-measures-and-what-it-does-not).
A number on this page that is not in those two rows will not be re-measured until someone asks for
it, so treat it as a reading taken on a date rather than as a figure something is watching.

Every published figure is MTEB's, for this model, at a pinned revision — see
[Where the published figures come from](#where-the-published-figures-come-from). SciFact's parity
**band** is centred on the bare `0.645` Phase 3.7 measured against and carried unsourced for two
phases; 0.64508 is the MTEB figure that was later found to corroborate it, and the delta above is
against that.

The supporting metrics, printed by the same runs:

| Dataset / run | nDCG@10 | Recall@10 | MRR@10 |
|---|---:|---:|---:|
| SciFact parity | 0.64593 | 0.78667 | 0.60483 |
| **SciFact real** | **0.67742** | **0.81322** | **0.63757** |
| FiQA parity | 0.37086 | not recorded | not recorded |
| **FiQA real** | **0.35569** | **0.42235** | **0.42596** |
| ArguAna parity | 0.50432 | 0.79161 | 0.41515 |
| **ArguAna real** | **0.47559** | **0.77240** | **0.38435** |

FiQA's real-leg figures come with a caveat that belongs beside them, not in a footnote: 38 of
FiQA's 57,638 corpus entries have an empty title and empty text and so yield no chunks, and one of
them (`117276`) is judged relevant — the real leg indexes 38 fewer documents than the parity leg,
so its Recall@10 is computed against a corpus missing a relevant document. See
[FiQA's real run, measured at last](#fiqas-real-run-measured-at-last).

And the corpora, from the downloaded archives rather than from a paper:

| | SciFact | FiQA | ArguAna |
|---|---:|---:|---:|
| Corpus documents | 5,183 | 57,638 | 8,674 |
| Queries in `queries.jsonl` | 1,109 | 6,648 | 1,406 |
| Queries retrieved and scored (judged, test split) | 300 | 648 | 1,406 |
| Unjudged — never retrieved, cannot be scored | 809 | 6,000 | 0 |
| Documents carrying a title | 5,183 | 0 | 2,699 |
| Self-hit excluded (`ignore_identical_ids`) | no | yes | yes |
| Documents over the 512-character chunk size | 99.2% | 51.0% | 87.3% |
| Units under real chunking | 20,155 | 121,236 | 24,003 |
| Most units from one document | 25 | 41 | 16 |
| Parity elapsed (CPU) | ~355 s | ~1 h 11 m | ~4 min per separator |

**The harness retrieves only for the judged queries** — the row above the unjudged one — because
`IrMetrics.Evaluate` scores exactly that set and a ranking retrieved for an unjudged query is
computed and thrown away. Only *membership* of the judged set reaches retrieval, never a judgement's
content, so this cannot leak relevance into the ranker. The nDCG figures are unchanged by
construction; what it changes is the **per-run counters and costs**: elapsed time, query embeddings,
and "queries that pooled" are over the judged set, so figures recorded before this (SciFact real
"pooled on 1,109 of 1,109", FiQA's parity leg embedding all 6,648 queries) count a superset that a
re-run no longer retrieves. ArguAna judges every query, so its counters are unaffected.

### Everything lands above published, by a little

All three parity runs score **above** the published figure, by 0.00085, 0.00219 and 0.00265. Each is
an order of magnitude inside the ±0.02 band, and none of them is a failure. But the *sign* is the
same three times out of three, and that is worth recording rather than rounding off: noise has no
preferred direction, so a consistent sign suggests a small difference in protocol rather than
sampling. The obvious candidates are tie-breaking between documents at equal scores and the exact
token at which truncation lands. **Neither has been checked, and neither is claimed as the cause.**

The band is two-sided precisely because scoring above a published reference is not good news — the
failure message for the upper edge says "leak, most likely qrels reaching the ranker". A leak that
size would be a strange leak, and the qrels never reach the ranker on any of the three paths, so this
reads as a protocol detail rather than as contamination. It stays an open observation.

(One ordering fact, offered to whoever picks this up and not as a finding: the smallest delta is
SciFact's, the one dataset that does *not* exclude the query's own document. Three points is not a
pattern.)

### What chunking does to the numbers, and it goes both ways

**Chunking helps SciFact and hurts ArguAna and FiQA, and the three runs are the same code over
three corpora.** That is the measurement's most useful result and it is a stronger one than any
delta alone, because a single dataset moving in one direction cannot tell you whether you are
looking at a property of chunking or a property of that dataset.

| | SciFact parity | SciFact real | FiQA parity | FiQA real | ArguAna parity | ArguAna real |
|---|---:|---:|---:|---:|---:|---:|
| nDCG@10 | 0.64593 | **0.67742** | 0.37086 | **0.35569** | 0.50432 | **0.47559** |
| Recall@10 | 0.78667 | 0.81322 | not recorded | 0.42235 | 0.79161 | 0.77240 |
| MRR@10 | 0.60483 | 0.63757 | not recorded | 0.42596 | 0.41515 | 0.38435 |
| Units indexed | 5,183 | 20,155 | 57,638 | 121,236 | 8,674 | 24,003 |
| Most units from one document | 1 | 25 | 1 | 41 | 1 | 16 |
| **Queries that pooled two or more units of one document** | **0** | **1,109 of 1,109** | **0** | **648 of 648** | **0** | **1,406 of 1,406** |
| **delta nDCG@10 vs our parity leg** | — | **+0.03148** | — | **−0.01517** | — | **−0.02873** |

The pooling row is what makes the rest verifiable rather than asserted. Under the parity protocol
max-pooling had nothing to pool on any query of any dataset; under Rag.NET's chunking it had
something to pool on **every** query of all three. FiQA's real leg indexes 121,236 units over
57,600 of its 57,638 documents — the 38 empty entries yield nothing, and one of them is judged
relevant ([below](#fiqas-two-protocols-do-not-index-the-same-number-of-documents)).

SciFact's "1,109 of 1,109" was counted when the harness still retrieved for every query in
`queries.jsonl`. It now retrieves only the 300 judged (see the corpora table above), so a re-run
reports the pooled count over those 300 — and since every one of the 1,109 pooled, every judged
one did. ArguAna's 1,406 are all judged, so its row is unchanged.

Read the supporting metrics alongside each delta, because they do not move the same way. On ArguAna,
Recall and MRR both fall with nDCG — documents are being **missed**, not reordered — though far less
than under the pre-3.16 fragmenting chunker, which drove Recall@10 down to 0.70057 where the packing
chunker holds it at 0.77240. On SciFact, Recall (0.78667 → 0.81322) and MRR (0.60483 → 0.63757) are
both clearly **up**: chunking now finds *more* of the relevant documents and ranks the ones it finds
higher. Under the fragmenting chunker SciFact's Recall was flat (0.78222) — the packing fix turned
"the same documents, better ordered" into "more documents, better ordered". On FiQA the parity leg
recorded no supporting metrics, so there is no per-metric before/after to read — only the nDCG
delta, plus the caveat that the real leg's Recall@10 (0.42235) is computed against a corpus missing
one relevant document (the 38 empty entries).

*Why*, as reasoning and not as measurement. The direction tracks whether relevance in the dataset is
**passage-level** or **document-level**:

- **SciFact is passage-level.** A claim is supported by a specific sentence or two inside an
  abstract, and 99.2% of those abstracts exceed the 512-character chunk size. The parity protocol
  embeds each abstract as one vector truncated at 256 tokens, so the supporting passage is averaged
  in with everything around it and, past the truncation point, is not embedded at all. A chunk that
  contains only the supporting sentences scores against the claim on its own terms, and max-pooling
  then promotes the abstract on the strength of its best passage. That is the shape of the higher
  MRR and, under the packing chunker, of the higher Recall too: chunks that carry a whole passage
  rather than a fragment of one both find and promote the right abstracts.
- **ArguAna is document-level.** The task is to retrieve the best counterargument to a whole
  argument. Its queries average 1,193 characters and its documents 1,030, with exactly one relevant
  document per query. Splitting a counterargument at 512 characters means the unit that best matches
  a whole-argument query is a fragment rather than the argument, and no amount of max-pooling
  recovers a similarity that was never computed against the whole text.
- **FiQA is document-level too.** Its documents are whole answer posts, and the answer post is the
  unit of relevance: splitting one at 512 characters leaves fragments whose best match to the
  question is a piece of the answer rather than the answer. Its delta (−0.01517) is about half of
  ArguAna's, consistent with its median document being 522 characters — barely over the chunk
  size, so half its corpus is never split at all.

**That explanation is the standing one, now supported by three corpora — and it was proposed
before the third existed, so FiQA is consistent with it rather than a test that could have refuted
it on its own.** Phase 3.12 proposed it when only two real deltas existed and could not separate
"ArguAna's relevance is document-level" from "the chunker was shredding ArguAna's documents into
~9.5 fragments each" — and Phase 3.16's packing fix tested exactly that split: if fragmentation was
most of the loss, packing the fragments back towards `MaxChunkSize` (9.5× → 2.8×) should recover
most of it. It did — −0.07839 → −0.02873, about 63% of the loss — which is what the phase's design
predicted. FiQA's real leg, measured after both, lands where the explanation says a document-level
corpus should: below its parity leg, by less than ArguAna, and it is recorded as consistent with
the explanation rather than as proof of it. The remaining negative deltas are the part packing
cannot touch: a whole-document query still scores against 512-character pieces, and no similarity
against the whole text is ever computed. The experiment that would test *that* residue is the
obvious one — vary `MaxChunkSize` and watch whether the deltas move apart.

**And do not read any sign as "chunking is better" or "chunking is worse".** Three datasets with
signs split two-to-one is exactly the evidence that the answer depends on the corpus and the task.
What was written here before SciFact's real run existed was that the helping case "is FiQA's, and
FiQA's real run has not been measured" — a speculation that guessed the wrong dataset, and one the
measurement has now settled in the other direction: chunking hurts FiQA by −0.01517.

### FiQA's real run, measured at last

**Deferred out of Phase 3.12, re-based by Phase 3.16's packing fix, and run for the first time by
Phase 3.15 (2026-08-02).** This run is the measurement — it replaces the derived estimate that
stood here rather than confirming it:

- **nDCG@10 = 0.35569**, Recall@10 = 0.42235, MRR@10 = 0.42596. The parity leg in the same run
  reproduced **0.37086** exactly, so the delta is **−0.01517**.
- **121,236 units over 57,600 of 57,638 documents**, at most 41 from one document — and **38
  documents contributed nothing**: 38 of FiQA's corpus entries have an empty title and an empty
  text, one of them (`117276`) judged relevant, so the real leg indexes 38 fewer documents than
  the parity leg and its Recall@10 is computed against a corpus missing a relevant document
  ([below](#fiqas-two-protocols-do-not-index-the-same-number-of-documents)).
- All **648 judged queries** — the only ones the harness retrieves for — pooled two or more units
  of one document.
- The real leg took **3,587.5 s (59.8 min)**; the whole test, both legs off the warm parity-vector
  cache, **1 h 4 m**.

The derived estimate this replaces said **~1.5–2 hours**, priced from the ~27 embeddings/s the
packed SciFact and ArguAna real legs observed over ~122,000 embeddings. **The measured cost was
~1 h 4 m, so the estimate was conservative** — it overshot, and it is recorded here as having
overshot rather than quietly replaced. The 8–9 hour figure before it priced the pre-3.16
fragmenting chunker's 429,850 chunks and died with them.

The figure is pinned by `BeirReproduction` at ±0.005 like the other real legs, and `BeirRunBudget`'s
FiQA real entry now carries the measured cost in place of the derivation. What this section said
while the run was pending — that FiQA adds a *third* corpus shape rather than the only evidence on
whether max-pooling helps or hurts — held: see
[what chunking does](#what-chunking-does-to-the-numbers-and-it-goes-both-ways) for what the third
delta says.

### The chunker emitted every split part as its own chunk, fixed in Phase 3.16

Measured while costing the real runs, recorded here as a probable library defect, and **since
confirmed and fixed**: `RecursiveChunkingStrategy` split a document and then never packed the short
parts back up towards `ChunkingOptions.MaxChunkSize`, so every part became its own chunk and a
document of short lines became one chunk per line. **Phase 3.16 taught the strategy to pack split
parts towards the limit**, and the counts collapsed on all three corpora:

| Corpus | Documents | Units before | Units now | Factor before | Factor now | Most from one doc before | now |
|---|---:|---:|---:|---:|---:|---:|---:|
| SciFact | 5,183 | 56,707 | **20,155** | 10.9× | **3.9×** | 221 | **25** |
| FiQA | 57,638 | 429,850 | **121,236** | 7.5× | **2.1×** | 1,723 | **41** |
| ArguAna | 8,674 | 82,618 | **24,003** | 9.5× | **2.8×** | 285 | **16** |

FiQA's median document is 522 characters against a 512-character chunk size, which suggests roughly
2×. It used to produce 7.5×, and that discrepancy is what opened the investigation; it now produces
**2.1×**, and the discrepancy is closed. The old behaviour inflated embedding cost, storage and
query-time sorting for **every user of the default chunker**, not only for this harness, and it is
why FiQA's real run was once estimated in the 8–9 hour range. Every real-run number on this page is
measured under the fixed chunker; the parity runs index one chunk per document, never call the split
path, and were verified unmoved to five decimal places — which is exactly what makes them the fix's
regression gate.

### FiQA's two protocols do not index the same number of documents

38 of FiQA's 57,638 corpus entries have an empty title **and** an empty text. One of them (`117276`)
is judged relevant. The chunker correctly yields nothing for empty input, so the real run indexes 38
fewer documents than the parity run — a genuine difference between the two legs rather than a
rounding detail, and one that would be invisible in an nDCG, because a document that was never
indexed looks exactly like a document that was indexed and ranked badly.

It is surfaced as `BeirRunResult.UnindexedDocumentCount` rather than papered over with a placeholder
chunk. It was recorded as a debt against Phase 3.15, and the debt is paid: Phase 3.15 produced
FiQA's real number, and the difference is stated alongside it
([above](#fiqas-real-run-measured-at-last)).

## The ablation table

**Four rows, three datasets, one protocol, measured by Phase 3.15 (2026-08-01/02).** Every cell
below is on the **parity** protocol — one chunk per document, truncated at 256 — evaluated over the
judged queries only, and the dense row *is* the parity run above, not a re-measurement of it. That
is not a cost decision: the dense row's whole value is that it is validated against a published
figure, so every delta in the table hangs off something external. Running the ablation on the real
leg would measure three techniques against an anchor that agrees with nothing published.

The row labels are the runs' own labels, verbatim, because two of them carry load: the BM25 row's
label is what stops its number being read as a comparison to published BM25 figures, and the
reranker's label states both truncation lengths so nobody infers that one of them is a mistake.
Deltas are against the dense anchor; each addition is measured **alone** on top of dense — the rows
are not cumulative.

| nDCG@10, parity protocol, judged queries only | SciFact | FiQA | ArguAna |
|---|---:|---:|---:|
| **dense** (the parity anchor) | 0.64593 | 0.37086 | 0.50432 |
| `+bm25 hybrid via RRF (incomparable to published BM25: no stemming or stopwords, k1=1.5 b=0.75)` | **0.69913** (+0.0532) | **0.35665** (−0.0142) | **0.51173** (+0.0074) |
| `+hyde (mean of 3 cached openai/gpt-4o-mini@t0.8 hypotheticals, L2-normalised; no LLM call — reads the frozen generation run)` | **0.70001** (+0.0541) | **0.36543** (−0.0054) | **0.50293** (−0.0014) |
| `+reranker (cross-encoder/ms-marco-MiniLM-L6-v2 over dense top-k; pairs truncated at 512 tokens, dense embeddings at 256)` | **0.68442** (+0.0385) | **0.38458** (+0.0137) | **0.47917** (−0.0252) |

**Four of the nine cells go down**, and that is the first thing to check before interpreting any of
them: the design named "a table that only ever goes up" as the signature of a table measuring
something other than what it claims. This one goes down where going down is the defensible answer.

The supporting metrics, printed by the same runs (dense from the parity runs above; FiQA's parity
leg did not record them):

| Dataset / row | nDCG@10 | Recall@10 | MRR@10 |
|---|---:|---:|---:|
| SciFact dense | 0.64593 | 0.78667 | 0.60483 |
| SciFact +bm25 hybrid | 0.69913 | 0.83933 | 0.65676 |
| SciFact +hyde | 0.70001 | 0.85033 | 0.65563 |
| SciFact +reranker | 0.68442 | 0.78667 | 0.65789 |
| FiQA dense | 0.37086 | not recorded | not recorded |
| FiQA +bm25 hybrid | 0.35665 | 0.43951 | 0.42914 |
| FiQA +hyde | 0.36543 | 0.44738 | 0.43124 |
| FiQA +reranker | 0.38458 | 0.44295 | 0.46744 |
| ArguAna dense | 0.50432 | 0.79161 | 0.41515 |
| ArguAna +bm25 hybrid | 0.51173 | 0.80228 | 0.42141 |
| ArguAna +hyde | 0.50293 | 0.79516 | 0.41258 |
| ArguAna +reranker | 0.47917 | 0.79374 | 0.38188 |

**Every row proves its mechanism did something before its number is read**, because a row whose
machinery silently did nothing is the dense ranking wearing another label — the failure shape this
milestone keeps finding. The counters from the published runs: HyDE's search vector produced a
ranking that diverged from dense on **300/300** SciFact, **648/648** FiQA and **1,405/1,406**
ArguAna queries; the reranker reordered the dense top-k on **648/648** FiQA and **1,406/1,406**
ArguAna queries, and on **1,108/1,109** in SciFact's pre-fix run — a counter quoted deliberately,
because that guard passing on a defective run is itself a finding
([below](#the-reranker-row-was-measured-twice-because-the-first-measurement-found-a-library-defect));
the BM25 row asserts per dataset that BM25 returned results and that the fused ranking diverged
from dense.

### Two of the three predictions failed, and are reported as failures

The design stated an expectation for every HyDE cell **before anything was measured**, precisely so
the table could be found wrong — the standard Phase 3.16 set when it predicted ArguAna's recovery
and had to be able to report that it had not happened. Two of the three predictions failed:

| Prediction (design §2, pre-measurement) | Measured | Outcome |
|---|---:|---|
| **FiQA: clear lift from HyDE** — the positive control | −0.0054 | **FAILED.** No lift. |
| **ArguAna: no lift, plausibly negative** — the negative control | −0.0014 | **HELD.** |
| **SciFact: modest lift, smaller than FiQA's** | +0.0541 | **FAILED.** Large, and the largest of the three. |

The design also named its own escape hatch: "FiQA shows no lift" was listed as the outcome that
would make the table uninterpretable, because a weak model and an unhelpful method are
indistinguishable in a run that is flat everywhere. **That escape hatch does not apply.** SciFact
gained +0.0541 from the same model, the same prompt and the same cache, so the model demonstrably
produces hypotheticals that help. FiQA's flat cell is a measurement, not an artefact.

Why the two predictions failed is a **post-hoc explanation, and it is recorded as post-hoc** — the
design did not foresee it. The design's reasoning was that HyDE helps where the query–corpus
vocabulary gap is widest. The numbers suggest a different variable: HyDE helps when the
*hypothetical* sits closer to the corpus register than the query does. A one-sentence SciFact claim
expands into abstract-like prose that resembles the corpus; a FiQA question expands into a clean
LLM answer, while FiQA's actual answers are messy StackExchange posts. **That is a hypothesis to
test, not a conclusion** — nothing in this run distinguishes it from other explanations of the same
three numbers.

### The negative control held, and with an observed mechanism

ArguAna's −0.0014 is the expected answer rather than a disappointing one, and there is an observed
mechanism behind it, not a hand-wave. Recorded during hypothetical generation, independently of the
measurement: ArguAna's hypotheticals are **compressed restatements of the input argument** — same
stance, recycling the argument's own statistics (the FAO 18% figure, the 100,000-litres-per-kilogram
figure). ArguAna asks for the best *counter*argument, so HyDE moves the search vector toward the
query's own position and away from the target.

### The reranker row was measured twice, because the first measurement found a library defect

**This is the most important finding on this page.** The first reranker measurement gave SciFact
**0.56693**, FiQA **0.34085**, ArguAna **0.41806** — the cross-encoder harming every dataset. That
was not what the cross-encoder does; it was what `OnnxReranker.TokenizePair` did. It was not a
WordPiece tokenizer: it split on whitespace and looked up whole lowercased words in the vocabulary,
mapping every miss to `[UNK]`. Measured over both corpora in full, **26.59% of SciFact's 1,112,417
words and 17.62% of FiQA's 7,660,017 words** reached the model as `[UNK]`; through WordPiece the
same corpora produce 0.01% and 0.10%.

Fixed in commit `a912187`, and the row was re-measured; the re-measured numbers are what the table
above publishes. The swing, **from tokenization alone**: **+0.117** on SciFact, **+0.061** on
ArguAna, **+0.044** on FiQA.

Two things are worth stating plainly:

- **No guard could have caught it.** `AssertRerankerReordered` proves the cross-encoder *moved* the
  ranking — and garbage-but-varying scores reorder every query, which is exactly what it observed:
  the defective run reordered 1,108 of SciFact's 1,109 queries and passed the guard. The guard
  added with the fix is of a different kind: a tokenizer round-trip test that fails on the old
  algorithm.
- **The out-of-domain prediction was right all along, masked by the defect.** With real tokens
  reaching the model, FiQA — the dataset most like the reranker's MS MARCO training data — gains
  (+0.0137, the table's only reranker lift), and ArguAna loses (−0.0252), because the best
  counterargument is not the passage a relevance model scores highest.

### What the reranker row does not measure

`TopK` equals the evaluation cutoff of 10, so the reranker permutes exactly the ten documents it
will be scored on and **Recall@10 is frozen by construction** — visible in the numbers: SciFact's
reranker Recall@10 is 0.78667, identical to dense. A real reranking pipeline retrieves ~100
candidates and reranks down to 10, which can also change what is *in* the top 10; this row cannot.
That is a design limitation of what was measured, not a defect — read the row as "what reordering
the dense top-10 does", never as the best a cross-encoder can do.

### The BM25 row is an internal comparison, and its label is the deliverable

The row is incomparable to any published BM25 figure, for the reason recorded before it was built:
Anserini — the source of BEIR's published BM25 numbers — applies Porter stemming and an English
stopword list at `k1=0.9, b=0.4`; `InMemoryBm25Index` lowercases and splits at `k1=1.5, b=0.75`.
Those are not two settings of one retriever, and tokenisation dominates BM25 scores. That is why
the incomparability is in the row's own label in the table above, not only in the prose here: a
`+bm25` row that looked comparable would read as validation of ours against the literature, which
is worse than no row at all. What the row *is* comparable to is the dense anchor beside it, and
that internal comparison is the row's whole claim.

### HyDE ran from a frozen cache, and the cache is not in this repository

The hypotheticals were generated once, by the one-time tool in
`benchmarks/Rag.NET.Benchmarks.Quality.Hypotheticals`: **7,062 generations for the 2,354 judged
queries** across the three datasets, at the library's own `HypothesisCount = 3`, by
`openai/gpt-4o-mini` at `HydeOptions.HypothesisTemperature` (0.8) — total cost **$0.66**, zero
failures. The table run never calls an LLM: the HyDE row reads the frozen generation run from a
content-addressed cache, and a missing entry is a refusal that names the key, never a silent
regeneration — a regeneration would blend two generations into one table with nothing saying so.
The cache identity is `openai/gpt-4o-mini@t0.8`, and the temperature is part of the key
deliberately: hypotheticals sampled at another temperature are another experiment.

**The cache is never committed.** It derives from BEIR's queries, and this project's standing
position ([Licences](#licences)) is that nothing downloaded here is redistributed — FiQA's upstream
restricts commercial use in so many words. Committing LLM text generated *from* those queries would
quietly reverse that position, so the cache gets the same treatment as the datasets and the model:
produced locally, cached, never vendored.

### Running the ablation

The cells are `BeirAblationTests`, gated through `BeirRunBudget` behind `RAGNET_BEIR_LONG_RUNS`
like every case the nightly cannot afford; each skips with its measured cost and the command that
runs it. Every cell's figure is pinned by `BeirReproduction` at ±0.005 — this machine's own
reproduction, since no published figure exists for any cell — so an opted-in re-run that drifts
fails rather than passing on a mechanism guard alone, and the numbers in the table above are
machine-checked rather than prose. The HyDE cells additionally need the hypothetical cache, which only the generation tool
produces — an opted-in run without it **fails**, naming the missing key, rather than skipping,
because a skip would read like a measurement from the summary.

## What the numbers do not mean

They are **three datasets, one embedding model, two protocols**, and only the parity column is
comparable to anything published. Read that column as evidence that the retrieval path computes what
BEIR computes — that the metrics, the pooling and the normalisation are right. Read the real column
as one measurement of what this library's defaults do to one corpus. Read either as nothing else.

In particular, none of these is:

- **A claim about your corpus.** SciFact is scientific claims against abstracts, with 1.1 relevant
  documents per query. FiQA is opinionated financial answers crawled from StackExchange, Reddit and
  StockTwits. ArguAna is counterargument retrieval where the query *is* an argument. Almost nothing
  about any of those shapes transfers to yours.
- **A claim about any other embedding model.** The numbers belong to `all-MiniLM-L6-v2` at
  `max_seq_length = 256`. Swapping the model swaps every one of them.
- **A claim about any other configuration.** Cosine over L2-normalised vectors, in-memory storage,
  top-k = 10; the parity runs at one chunk per document, the real run at `RecursiveChunkingStrategy`
  and stock `ChunkingOptions`. The parity and real columns are dense-only — what BM25 hybrid, HyDE
  and a reranker each do to the dense number is measured in
  [the ablation table](#the-ablation-table), one addition at a time, under the configurations its
  row labels state, and never in combination. Changing any of those settings measures something
  else.
- **A comparison against another library.** Comparative tables are legitimate work, but only credible
  with genuinely equivalent configuration, and that equivalence is the part such tables are usually
  attacked on. There is none here. → Phase 3.14.
- **A speed result.** ~355 s for SciFact and ~1 h 11 m for FiQA's parity leg are what an ONNX model
  takes to embed those corpora on a CPU. They are reported so you can budget a nightly run, not as
  performance figures.

What this *is*: a harness defect is inherited by every dataset added after it, and a subtly wrong
nDCG can still land inside a ±0.02 band while the headline number reads as evidence. One number
matching a published reference was worth more than five unvalidated ones, which is why SciFact came
first and alone.

## Six settings the band actually guards

The band is not loose enough to hide a defect, and landing inside it depends on several independent
decisions being simultaneously correct. Each is *also* pinned by its own test, because the parity
number alone cannot tell you which one broke:

1. **Mean pooling excludes padding.** Sequences in a batch are padded to the longest one. Pooling
   over the padding makes a text's vector depend on what it was batched with.
2. **`[CLS]` and `[SEP]` are included in the mean**, as sentence-transformers includes them.
   Excluding them is defensible and produces a different number.
3. **Truncation at 256 tokens**, matching `all-MiniLM-L6-v2`'s `max_seq_length`. Raising it measures
   a configuration the published figure did not come from.
4. **IDCG is summed over `min(|relevant|, k)` ideal gains, never over `k`.** 277 of SciFact's 300
   judged queries have exactly one relevant document, so for 92% of the dataset IDCG must equal
   exactly 1. Summing ten assumed gains instead scales almost every query down by the same factor —
   a uniform, plausible-looking collapse rather than a visible failure. FiQA is where this stops
   being a SciFact-shaped concern: only 220 of its 648 judged queries have a single relevant
   document, and the tail runs to 15.
5. **Only judged queries are scored.** SciFact ships 1,109 queries and judges 300 in the test split;
   FiQA judges 648 of 6,648. Scoring the rest as zero divides SciFact's mean by roughly 3.7 and
   FiQA's by ten, and reads as catastrophic retrieval failure rather than as a harness bug. ArguAna
   judges all 1,406 of its queries. The harness now also *retrieves* only for the judged queries —
   the unjudged ones were retrieved and discarded until Phase 3.15 — which cannot move the mean,
   for the same reason the exclusion rule exists: nothing retrieved for an unjudged query ever
   entered it.

**A sixth, added by FiQA and ArguAna: the query's own document is excluded from the ranking.** This
is MTEB's `ignore_identical_ids` and BEIR's `if corpus_id != query_id`, and it is a property of the
dataset rather than a preference — `mteb` sets it for ArguAna and FiQA and leaves it off for SciFact,
so it is carried per dataset on `BeirDatasetDescriptor.ExcludesSelfRetrievedDocument`. **ArguAna is
unrunnable without it:** 1,298 of its 1,406 queries are byte-identical to the corpus document sharing
their id, so a self-hit at cosine 1.0 takes rank 1 on 92% of the dataset and pushes the one relevant
counterargument to rank 2. That alone would cost roughly 1 − 1/log₂3 ≈ 0.37 of the ideal gain on
those queries — an order of magnitude outside the band, which is what makes it a setting the number
can see.

A seventh was recorded here and **was wrong**: BEIR's `SentenceBERT` joins a document's title and text
with a **single space** (`sep: str = " "` in `beir/retrieval/models/sentence_bert.py`), and this page
reported that measuring with a newline instead gave **0.64907** — a shift of 0.00314 — treating that
as a setting the number depends on.

> **Corrected by Phase 3.13 (2026-07-30).** The 0.00314 was a Rag.NET defect, not a property of the
> separator. The BERT tokenizer's normalizer **deleted** `\n` rather than folding it to a space, so
> the newline run merged each title's last word into its abstract's first word across all 5,183
> documents; the shift measured that merge. With the newline substituted to a space, both separators
> produce nDCG@10 = **0.64593** and the concatenation makes no difference to the number at all
> (re-measured: space unchanged at 0.64593, newline converged 0.64907 → 0.64593). Use the space,
> because upstream does — but not because the number can tell.

The separator is still worth pinning against upstream for the reason the original entry gave: both
values sat inside the band, so a green run could never have chosen between them. It just was not one
of the settings the number is sensitive to. It is now measured on the two titled corpora only —
FiQA titles none of its 57,638 documents, so `title + sep + text` trims back to identical bytes there
and a second FiQA case would spend an hour re-deriving a number that is equal by construction.

## Settings the band does *not* guard, and what does

This list matters more than the one above, because a number credited with catching a defect it
cannot catch is worse than a number nobody trusts. All of these are correct in the shipped harness
and all are pinned — just not by 0.64593, 0.37086 or 0.50432.

> **Phase 3.13 moved a third item onto this list: the title/text separator.** It was one of the
> numbered entries above until the newline defect it was really measuring was fixed. Both separators now produce
> 0.64593, so the parity number cannot see the concatenation at all; what keeps it correct is
> `BeirLoaderTests` and the upstream source, not this measurement.

- **Max-pooling chunks to documents *before* the top-*k* cut.** Pinned by `DocumentRankingTests`,
  **not by any parity number.** A parity run indexes one chunk per document — that is what BEIR's
  published figures embed — and retrieves with `TopK` equal to the cutoff, so ten hits are ten
  distinct documents and max-pooling is a literal no-op. On SciFact both orderings therefore pool the
  same ten hits and return the same ranking for every retrieved query (all 1,109 when that
  mutation run was made; the harness now retrieves only the 300 judged), so nDCG@10 is
  identically **0.64593** either way — checked rather than argued, by mutating `DocumentRanking` to
  cut-then-pool and re-running the whole measurement, which passes unchanged at both separators. What
  guards the order is a fixture where one document contributes four chunks among seven; against that,
  cut-then-pool fails **3 of `DocumentRankingTests`' 13 tests**, and the disagreement is documents
  going *missing* rather than being reordered.

  > **Corrected by Phase 3.12 (2026-07-31).** This entry used to end "FiQA is where the band starts
  > guarding this too, because FiQA is where a document stops being one chunk." **That is false, and
  > it will stay false for every dataset.** Max-pooling is a no-op under the *parity protocol*, not
  > because of anything about SciFact's documents, and every dataset is measured under that protocol
  > against its published figure. No parity band will ever guard the aggregation order. What does
  > exercise it is the **real run**, where ArguAna pooled on 1,406 of 1,406 queries and SciFact on
  > 1,109 of 1,109 (counted before retrieval was cut to the judged set; a SciFact re-run pools on
  > at most its 300 judged), both against a parity leg's 0 — and that run has no published
  > reference to be a band around, by design.

- **Recall's denominator is *every* relevant document, never `min(|relevant|, k)`.** Pinned by
  `IrMetricsTests`, not by these numbers. This is the exact opposite of setting 4 above, and that is
  precisely the trap: IDCG *must* cap at `min(|relevant|, k)` and Recall *must not*, so reusing the
  one rule for the other reports perfect recall for a run that found half the answer. On SciFact the
  mistake is invisible. The most-judged query has **5** relevant documents — the distribution is 277
  queries with 1, then 14, 4, 3 and 2 queries with 2, 3, 4 and 5 — so `min(|relevant|, 10)` equals
  `|relevant|` for all 300 of them, the wrong denominator is the right one, and Recall@10 stays
  0.78667 either way. ArguAna hides it completely: exactly one relevant document per query, so the
  two denominators are equal on all 1,406. **FiQA is the first dataset here where they are not**:
  **six** of its 648 judged queries have more than ten relevant documents, the largest 15. On that
  query `min(|relevant|, 10)` is 10 where the right denominator is 15, so the wrong rule reports its
  recall 50% too high. Six queries in 648 will not move a mean far, which is the point — a defect
  this list exists to catch would still be invisible in the headline number.

## Running it yourself

The runs are tests in `tests/Rag.NET.Benchmarks.Quality.IntegrationTests` — `BeirParityTests` for the
parity leg and `BeirRealChunkingTests` for the real one. They **skip** unless all three environment
variables are set and usable:

| Variable | What it points at |
|---|---|
| `RAGNET_ONNX_EMBED_MODEL` | An `all-MiniLM-L6-v2` ONNX export with **token-level** output (`last_hidden_state`, `[batch, sequence, dimension]`). A pre-pooled export is rejected — pooling that the generator did not do is pooling it cannot verify. |
| `RAGNET_ONNX_EMBED_VOCAB` | That model's WordPiece `vocab.txt` (one token per line, line index = token id). |
| `RAGNET_BEIR_CACHE` | A writable directory for the dataset cache **and the embedding cache**. |

```bash
export RAGNET_ONNX_EMBED_MODEL=/models/all-MiniLM-L6-v2/model.onnx
export RAGNET_ONNX_EMBED_VOCAB=/models/all-MiniLM-L6-v2/vocab.txt
export RAGNET_BEIR_CACHE=~/.cache/ragnet-beir

dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests
```

**Run one dataset at a time.** SciFact is minutes; FiQA's parity leg alone is over an hour. Select
with `--filter "DisplayName~arguana"`, and note that it must be `DisplayName` —
`FullyQualifiedName` stops at the method name and carries no theory arguments, so
`FullyQualifiedName~arguana` selects nothing whatsoever and reports that as "no test matches" rather
than as a failure. `Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments` needs no model and finishes
in seconds; it is where the unit counts on this page came from.

**The embedding cache** is what makes measuring a dataset twice affordable. It lives under
`RAGNET_BEIR_CACHE`, is keyed on the model identity **and** a hash of the exact text, and a corrupt or
truncated entry is treated as a miss rather than returned. The model identity is in the key
deliberately: a cache keyed on text alone hands you another model's vectors after a model change,
and every number downstream is then quietly wrong while every test stays green.

The first two variables are the existing `RAGNET_ONNX_*` precedents, shared with the late-chunking and
ONNX embedding suites. Neither the model nor the datasets nor the cached vectors are in this
repository, and none is downloaded by the build.

**The model.** Bring your own export, or use the one CI uses —
`huggingface.co/sentence-transformers/all-MiniLM-L6-v2`, files `onnx/model.onnx` (~86 MB) and
`vocab.txt` (~230 KB). Any `all-MiniLM-L6-v2` ONNX conversion with token-level output works; the
identity reported to the ingestion path comes from `OnnxEmbeddingOptions.ModelId`, which the tests
set explicitly so a file named `model.onnx` does not become every model's identity.

**The datasets.** SciFact, FiQA and ArguAna are **downloaded on demand** into `RAGNET_BEIR_CACHE` on
first run, from BEIR's published archives, and are **never vendored into this repository**. What
arrives is verified against the MD5 BEIR publishes before anything is scored against it: the download
lands on a `.partial` file that is deleted on any verification failure, so a truncated or
proxy-intercepted fetch fails loudly instead of extracting into a short corpus that scores badly and
looks exactly like a retrieval defect. Nothing under the cache path is tracked by git.

**Where it runs.** The project declares `<RequiresSecrets>true</RequiresSecrets>`, so `nightly.yml`
selects it rather than every push doing so — see [CI and Test Tiers](./ci.md). It is a project of its
own for that reason: the arithmetic tests in `Rag.NET.Benchmarks.Quality.Tests` (metrics, loaders,
caches) need no model, no corpus and no network, and putting the parity test beside them would have
carried all of them out of the gating tier. The arithmetic gates every pull request; the run that
needs an 86 MB model and hours of CPU runs nightly.

**That job used to select the whole project with no filter under a 120-minute timeout**, against
cases costing more than that — FiQA's parity leg alone is 1 h 11 m — so it would have reported a
timeout instead of a number. It no longer does. `BeirRunBudget` records what each dataset costs under
each protocol and gates the four cases the job cannot afford behind `RAGNET_BEIR_LONG_RUNS`, which
`nightly.yml` never sets; the SciFact and ArguAna parity legs still run unasked. See
[what the nightly actually measures](./ci.md#what-the-nightly-actually-measures-and-what-it-does-not)
for the per-case table.

**What that costs is on this page rather than in the workflow:** a gated number is re-checked by
nothing. FiQA's 0.37086 is guarded on a pull request — by `BeirDatasetDescriptorTests`, which pins
the published target against the source string quoting it, and by `BeirReproduction`, which pins the
measured figure — and by no run at all until someone sets the variable.

Selection is not execution, and for a while it was mistaken for it. `RAGNET_BEIR_CACHE` was never
supplied to that job at all, so the parity test skipped every night and the job passed having
measured nothing — and the two ONNX variables were held as repository *secrets* naming file paths
that no step ever created on a fresh runner, which fails the same way. The job now fetches
`all-MiniLM-L6-v2` from Hugging Face at a pinned revision, verifies a SHA-256, caches it, and points
all three variables at runner paths. None of the three is a credential and none of them is a secret
any more.

## Where the published figures come from

Every published figure on this page is MTEB's, read from
[the official results repository](https://github.com/embeddings-benchmark/results) rather than from
the rendered leaderboard, which carries no revision and rounds. The path is
`results/sentence-transformers__all-MiniLM-L6-v2/8b3219a92973c328a8e22fadcfa821b5dc75636a/`, and
**that last segment is the model's own Hugging Face commit** — so the figures are pinned to a
*revision of the model* and not merely to its name, which matters because a model can be re-uploaded
under the same name. All three were produced by `mteb_version 1.12.75`.

| Dataset | File, split | Published nDCG@10 | MTEB dataset revision |
|---|---|---:|---|
| SciFact | `SciFact.json`, test | 0.64508 | `0228b52cf27578f30900b9e5271d331663a030d7` |
| FiQA | `FiQA2018.json`, test | 0.36867 | `27a168819829fe9bcd655c2df245fb19452e8e06` |
| ArguAna | `ArguAna.json`, test | 0.50167 | `c22ab2a51041ffd869aaddef7af8d8215647e41a` |

Each figure and its source live together on `BeirDatasetDescriptor`, in one string per dataset, so a
number and its provenance cannot drift apart.

**The `test` split, and only it.** `FiQA2018.json` reports three splits, and the other two are close
enough to be mistaken for the right one and far enough to fail the band: `dev` is 0.38815 and `train`
0.36609.
Test is the split `qrels/test.tsv` holds and the one the counts on this page describe. ArguAna ships
nothing but `test`. ArguAna's MTEB run also sets `ignore_identical_ids = True`; its figure is not
reproducible without the self-exclusion described above.

**The BEIR paper is not a second opinion on any of them.** Thakur et al. 2021 evaluate ten systems and
`all-MiniLM-L6-v2` is not among them — the only MiniLM in the paper is
`cross-encoder/ms-marco-MiniLM-L-6-v2`, the BM25+CE re-ranker in its table of model links. So there is
one source here, not two that agree.

## Licences

BEIR itself publishes no licence for the datasets it repackages — its README says only that it
"downloaded and prepared public datasets" and that "it remains the user's responsibility to determine
whether you have permission to use the dataset under the dataset's license". So licences are recorded
per dataset, in `BeirDatasetDescriptor`, read from **upstream** rather than from a mirror.

| Dataset | Licence, as recorded | Upstream |
|---|---|---|
| **SciFact** | `corpus.jsonl`: **ODC-By 1.0**; `queries.jsonl` and `qrels/`: **CC BY 4.0** | [allenai/scifact `LICENSE.md`](https://github.com/allenai/scifact/blob/master/LICENSE.md) |
| **FiQA** | **No licence named.** "available only for non-commercial use" | [sites.google.com/view/fiqa](https://sites.google.com/view/fiqa/) |
| **ArguAna** | **CC BY 4.0** | Zenodo deposit `doi:10.5281/zenodo.3973258` ([Webis](https://webis.de/data/arguana-counterargs.html)) |

**All three disagree with their Hugging Face mirrors, and the disagreements are recorded rather than
resolved.** Upstream is treated as authoritative in every case. This harness downloads these datasets
into a cache and redistributes nothing; anyone who intends to redistribute should read both sides.

**SciFact is licensed in two pieces**, and the harness touches both. From the upstream LICENSE,
verbatim: "All claims and evidence annotations — in the files `claims_*.jsonl` — are released under
CC BY 4.0", and "The abstracts in the corpus — in the file `corpus.jsonl` — are part of the Semantic
Scholar S2ORC dataset and are licensed under ODC-By 1.0". Both require attribution; neither is public
domain. The repository's *code* is Apache 2.0, which does not apply to anything downloaded here.
`BeIR/scifact` declares a single `cc-by-sa-4.0`, which matches **neither** upstream licence and adds a
share-alike obligation upstream does not impose.

**ArguAna's disagreement is the same shape.** BEIR's README links
`http://argumentation.bplaced.net/arguana/data`, which now answers 404; the live upstream is the Webis
deposit on Zenodo, whose record declares **CC BY 4.0**. Both mirrors — `BeIR/arguana` *and*
`mteb/arguana` — declare `cc-by-sa-4.0`, again adding a share-alike obligation upstream does not
impose. The arguments are crawled from the debate portal idebate.org, whose own terms the deposit does
not restate and which are not asserted here.

**FiQA's disagreement is the material one.** The WWW'18 challenge site names **no licence at all**.
What it states, verbatim and twice, is that "The training data is available only for non-commercial
use" and "The testing data is available only for non-commercial use", over a collection "built by
crawling Stackexchange, Reddit and StockTwits" — three sources with terms of their own that the
challenge does not restate. `BeIR/fiqa` declares `cc-by-sa-4.0`, which **permits precisely the
commercial use upstream refuses**: it grants strictly more than upstream does, in the one direction
upstream explicitly rules out. `mteb/fiqa` declares `unknown`, which is at least honest.

**And the meta-finding, which is why none of the three mirror declarations is worth trusting.**
`BeIR/scifact`, `BeIR/fiqa` and `BeIR/arguana` all declare the same `cc-by-sa-4.0` — one blanket
declaration across the mirror rather than a per-dataset determination. That is why it disagrees with
all three upstreams at once, including a dataset that upstream restricts to non-commercial use and a
dataset that upstream licenses in two pieces, neither of which is `cc-by-sa-4.0`.

Cite: Wadden et al., "Fact or Fiction: Verifying Scientific Claims", EMNLP 2020 (SciFact); Maia et
al., "WWW'18 Open Challenge: Financial Opinion Mining and Question Answering", WWW 2018 Companion
(FiQA); Wachsmuth, Syed and Stein, "Retrieval of the Best Counterargument without Prior Topic
Knowledge", ACL 2018 (ArguAna).

## Not measured, and why

- **TREC-COVID and EnronQA.** TREC-COVID is the first graded-relevance dataset — `IrMetrics` uses
  `2^rel - 1` and has a graded fixture, but no graded *dataset* has been through it, which deserves its
  own attention. EnronQA is the private-corpus and multi-tenant story. Past FiQA the cost is embedding
  time rather than disk.
- **BM25 against anything published.** [The ablation table](#the-ablation-table) now carries a
  `+bm25 hybrid` row, but it is an **internal** comparison against the dense anchor and nothing
  else, for the reason that kept it out of earlier phases: `InMemoryBm25Index` lowercases and
  splits at `k1=1.5, b=0.75` while Anserini — which produced BEIR's published BM25 figures — stems
  and applies stopwords at `k1=0.9, b=0.4`. The debt was resolved by labelling the row incomparable
  in the table itself, not by making the retrievers comparable.
- **Comparative tables against other libraries.** A fair one needs a decided framing, not just
  equivalent configuration: matched-configuration comparisons mostly measure how carefully each
  library was configured and converge on near-identical numbers, because every library calls the same
  embedding model. → **Phase 3.14**, on each library's *defaults*.
