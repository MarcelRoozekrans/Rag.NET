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

**Three datasets, `all-MiniLM-L6-v2`, dense retrieval.** SciFact's parity leg measured 2026-07-30,
FiQA, ArguAna and SciFact's real leg 2026-07-31. Every number below came out of Rag.NET's own path —
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
| **SciFact** | **0.64593** | 0.64508 | +0.00085 | **0.65589** | **+0.00995** |
| **FiQA** | **0.37086** | 0.36867 | +0.00219 | [not run](#what-fiqas-real-run-would-cost-and-why-it-has-not-run) | — |
| **ArguAna** | **0.50432** | 0.50167 | +0.00265 | **0.42594** | **−0.07839** |

**The right-hand column is compared to the left-hand one and to nothing else.** There is no published
nDCG@10 for "chunked with Rag.NET's defaults and max-pooled", none is invented here, and nothing in
the literature says what chunking ought to do to nDCG on these corpora — which is why the run exists.

**The two real deltas have opposite signs, and that is the phase's most useful result:** Rag.NET's
default chunking **helps SciFact by +0.0100 and hurts ArguAna by −0.0784**. See
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
| **SciFact real** | **0.65589** | **0.78222** | **0.62057** |
| FiQA parity | 0.37086 | not recorded | not recorded |
| ArguAna parity | 0.50432 | 0.79161 | 0.41515 |
| **ArguAna real** | **0.42594** | **0.70057** | **0.34147** |

And the corpora, from the downloaded archives rather than from a paper:

| | SciFact | FiQA | ArguAna |
|---|---:|---:|---:|
| Corpus documents | 5,183 | 57,638 | 8,674 |
| Queries in `queries.jsonl` | 1,109 | 6,648 | 1,406 |
| Queries scored (judged, test split) | 300 | 648 | 1,406 |
| Excluded as unjudged | 809 | 6,000 | 0 |
| Documents carrying a title | 5,183 | 0 | 2,699 |
| Self-hit excluded (`ignore_identical_ids`) | no | yes | yes |
| Documents over the 512-character chunk size | 99.2% | 51.0% | 87.3% |
| Units under real chunking | 56,707 | 429,850 | 82,618 |
| Most units from one document | 221 | 1,723 | 285 |
| Parity elapsed (CPU) | ~355 s | ~1 h 11 m | ~4 min per separator |

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

**Chunking helps SciFact and hurts ArguAna, and the two runs are the same code over two corpora.**
That is the phase's most useful result and it is a stronger one than either delta alone, because a
single dataset moving in one direction cannot tell you whether you are looking at a property of
chunking or a property of that dataset.

| | SciFact parity | SciFact real | ArguAna parity | ArguAna real |
|---|---:|---:|---:|---:|
| nDCG@10 | 0.64593 | **0.65589** | 0.50432 | **0.42594** |
| Recall@10 | 0.78667 | 0.78222 | 0.79161 | 0.70057 |
| MRR@10 | 0.60483 | 0.62057 | 0.41515 | 0.34147 |
| Units indexed | 5,183 | 56,707 | 8,674 | 82,618 |
| Most units from one document | 1 | 221 | 1 | 285 |
| **Queries that pooled two or more units of one document** | **0** | **1,109 of 1,109** | **0** | **1,406 of 1,406** |
| **delta nDCG@10 vs our parity leg** | — | **+0.00995** | — | **−0.07839** |

The pooling row is what makes the rest verifiable rather than asserted. Under the parity protocol
max-pooling had nothing to pool on any query of either dataset; under Rag.NET's chunking it had
something to pool on **every** query of both. This is the first time chunk-to-document max-pooling
has been exercised against a corpus at all, rather than against `DocumentRankingTests`' seven-hit
fixture.

Read the supporting metrics alongside each delta, because they do not move the same way. On ArguAna,
Recall and MRR both fall with nDCG — documents are being **missed**, not reordered. On SciFact,
Recall is flat to slightly *down* (0.78667 → 0.78222) while MRR is clearly **up** (0.60483 →
0.62057): chunking finds fractionally fewer of the relevant documents and ranks the ones it finds
higher.

*Why*, as reasoning and not as measurement. The direction tracks whether relevance in the dataset is
**passage-level** or **document-level**:

- **SciFact is passage-level.** A claim is supported by a specific sentence or two inside an
  abstract, and 99.2% of those abstracts exceed the 512-character chunk size. The parity protocol
  embeds each abstract as one vector truncated at 256 tokens, so the supporting passage is averaged
  in with everything around it and, past the truncation point, is not embedded at all. A chunk that
  contains only the supporting sentences scores against the claim on its own terms, and max-pooling
  then promotes the abstract on the strength of its best passage. That is the shape of both the
  higher MRR and the flat Recall: the same documents, better ordered.
- **ArguAna is document-level.** The task is to retrieve the best counterargument to a whole
  argument. Its queries average 1,193 characters and its documents 1,030, with exactly one relevant
  document per query. Splitting a counterargument at 512 characters means the unit that best matches
  a whole-argument query is a fragment rather than the argument, and no amount of max-pooling
  recovers a similarity that was never computed against the whole text.

**Nothing here measures that explanation.** It is the shape the two numbers have, offered so the next
person knows which experiment would test it — the obvious one being to vary `MaxChunkSize` and watch
whether the two deltas move apart.

**And do not read either sign as "chunking is better" or "chunking is worse".** Two datasets with
opposite signs is exactly the evidence that the answer depends on the corpus and the task. What was
written here before SciFact's real run existed was that the helping case "is FiQA's, and FiQA's real
run has not been measured" — a speculation where there is now a measurement, and one that guessed the
wrong dataset.

### What FiQA's real run would cost, and why it has not run

**Deliberately deferred, with a measured basis rather than an estimate.** FiQA's parity leg took
**1 h 11 m** for 64,247 distinct embeddings. Its real leg is **429,850 chunks** — roughly 7.5× the
corpus — so the embedding alone is on the order of eight to nine hours, and on top of that
`InMemoryVectorStore` sorts 429,850 scored entries for each of 6,648 queries.

It is still the run worth having, and that is why it is scheduled rather than dropped — but **it is
no longer the only thing that can answer "does max-pooling help or hurt"**, and this section used to
say it was. SciFact's real leg answers it in the affirmative (+0.00995) and ArguAna's in the negative
(−0.07839), which is a two-sided answer that neither alone could give. What FiQA adds is a *third*
corpus shape: documents that are long and heterogeneous in their own right, where ArguAna's 9.5×
fan-out comes largely from the chunker's short-part behaviour
([below](#the-chunker-emits-every-split-part-as-its-own-chunk)) rather than from documents that are
long in any interesting way, and SciFact's abstracts are uniform.

It lands in **Phase 3.15**, which needs a cached-embeddings artifact for the ablation table anyway
and is therefore its natural home.

### The chunker emits every split part as its own chunk

Measured while costing the real runs, and **a probable library defect independent of anything on this
page**: `RecursiveChunkingStrategy` splits a document and then never merges the short parts back up
towards `ChunkingOptions.MaxChunkSize`. Every part becomes its own chunk, so a document of short
lines becomes one chunk per line.

| Corpus | Documents | Units at stock 512-character chunking | Factor | Most from one document |
|---|---:|---:|---:|---:|
| SciFact | 5,183 | 56,707 | 10.9× | 221 |
| FiQA | 57,638 | **429,850** | 7.5× | **1,723** |
| ArguAna | 8,674 | 82,618 | 9.5× | 285 |

FiQA's median document is 522 characters against a 512-character chunk size, which suggests roughly
2×. It produces 7.5×. This inflates embedding cost, storage and query-time sorting for **every user
of the default chunker**, not only for this harness, and it is why FiQA's real run is measured in
hours. Recorded as a scheduled debt in the roadmap with these numbers.

### FiQA's two protocols do not index the same number of documents

38 of FiQA's 57,638 corpus entries have an empty title **and** an empty text. One of them (`117276`)
is judged relevant. The chunker correctly yields nothing for empty input, so the real run indexes 38
fewer documents than the parity run — a genuine difference between the two legs rather than a
rounding detail, and one that would be invisible in an nDCG, because a document that was never
indexed looks exactly like a document that was indexed and ranked badly.

It is surfaced as `BeirRunResult.UnindexedDocumentCount` rather than papered over with a placeholder
chunk. Recorded as a debt against Phase 3.15, where FiQA's real number will be produced and where the
difference has to be stated alongside it.

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
  top-k = 10, no hybrid search, no HyDE, no reranker; the parity runs at one chunk per document, the
  real run at `RecursiveChunkingStrategy` and stock `ChunkingOptions`. Changing any of those measures
  something else.
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
   judges all 1,406 of its queries, so its excluded count must be exactly 0 — a non-zero one there is
   a loading defect rather than the usual dilution.

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
  same ten hits and return the same ranking for every one of the 1,109 queries, so nDCG@10 is
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
  > 1,109 of 1,109, both against a parity leg's 0 — and that run has no published reference to be a
  > band around, by design.

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

- **FiQA under real chunking.** Deferred with a measured cost basis rather than dropped — roughly
  eight to nine hours of embedding plus 429,850 entries sorted per query across 6,648 queries.
  SciFact's and ArguAna's real legs already answer whether max-pooling helps or hurts, in both
  directions; FiQA adds a third corpus shape rather than the only evidence. → **Phase 3.15**, which
  needs a cached-embeddings artifact anyway.
- **TREC-COVID and EnronQA.** TREC-COVID is the first graded-relevance dataset — `IrMetrics` uses
  `2^rel - 1` and has a graded fixture, but no graded *dataset* has been through it, which deserves its
  own attention. EnronQA is the private-corpus and multi-tenant story. Past FiQA the cost is embedding
  time rather than disk.
- **The ablation table** — baseline dense → +BM25 hybrid → +HyDE → +reranker. It needs an `IChatClient`
  for HyDE and a cross-encoder for the reranker, and its BM25 row needs the caveat below resolved
  first. ArguAna is its negative control: HyDE should *not* help there, and a table that only ever goes
  up is indistinguishable from a table that cannot go down. → **Phase 3.15**.
- **BM25, at all.** Everything here is dense-only, and that is not an oversight: `InMemoryBm25Index`
  lowercases and splits, while Anserini — which produced BEIR's published BM25 figures — applies
  Porter stemming and an English stopword list, and BEIR runs it at `k1=0.9, b=0.4` where ours
  hard-codes Lucene's `k1=1.5, b=0.75`. Tokenisation dominates BM25 scores. A `+BM25 hybrid` row
  published today would be incomparable to any published BM25 reference **while looking like
  validation of ours**, which is worse than not publishing it. Recorded as a scheduled debt in the
  roadmap with those numbers, so whoever builds the table knows before they publish it.
- **Comparative tables against other libraries.** A fair one needs a decided framing, not just
  equivalent configuration: matched-configuration comparisons mostly measure how carefully each
  library was configured and converge on near-identical numbers, because every library calls the same
  embedding model. → **Phase 3.14**, on each library's *defaults*.
