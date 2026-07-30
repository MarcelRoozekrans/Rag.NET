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

**SciFact, `all-MiniLM-L6-v2`, dense retrieval, measured 2026-07-30.** 5,183 documents embedded,
stored and retrieved through Rag.NET's own path — `OnnxEmbeddingGenerator` embeds,
`InMemoryVectorStore` stores and scores cosine, `DocumentRanking` aggregates chunks to documents,
`IrMetrics` scores. No component was built for the benchmark.

| Metric | Measured | Published reference |
|---|---:|---:|
| **nDCG@10** | **0.64593** | ≈ 0.645 |
| Recall@10 | 0.78667 | — |
| MRR@10 | 0.60483 | — |

| | |
|---|---|
| Queries evaluated | 300 |
| Queries excluded as unjudged | 809 |
| Corpus | 5,183 documents |
| Elapsed | ~355 s (CPU) |

The parity band asserted by the test is **0.625 ≤ nDCG@10 ≤ 0.665** — ±0.02, two-sided. Two-sided on
purpose: scoring materially *above* the published figure is not good news. Nothing in this harness
should be able to beat the model's own published number, so a high score indicates a leak, most
likely qrels reaching the ranker.

## What the number does not mean

It is **one dataset, one embedding model, one configuration**. Read it as evidence that the retrieval
path computes what BEIR computes — that the metrics, the pooling and the normalisation are right.
Not the chunk-to-document aggregation, which this dataset cannot exercise; see
[what the band does not guard](#two-settings-the-band-does-not-guard-and-what-does). Read it as
nothing else.

In particular, 0.64593 is not:

- **A claim about your corpus.** SciFact is scientific claims against abstracts. Short documents,
  binary relevance, 1.1 relevant documents per query. Almost nothing about that shape transfers to
  long-document corpora, conversational queries, or graded relevance.
- **A claim about any other embedding model.** The number belongs to `all-MiniLM-L6-v2` at
  `max_seq_length = 256`. Swapping the model swaps the number.
- **A claim about any other configuration.** One chunk per document, cosine over L2-normalised
  vectors, in-memory storage, top-k = 10, no hybrid search, no HyDE, no reranker. Changing any of
  those measures something else.
- **A comparison against another library.** Comparative tables are legitimate work, but only
  credible with genuinely equivalent configuration — same model, same chunk size, same top-k — and
  that equivalence is the part such tables are usually attacked on. There is none here.
- **A speed result.** ~355 seconds is what an ONNX model takes to embed 5,183 documents and 1,109
  queries on a CPU. It is reported so you can budget a nightly run, not as a performance figure.

What it *is*: a harness defect is inherited by every dataset added after it, and a subtly wrong nDCG
can still land inside a ±0.02 band while the headline number reads as evidence. One number matching
a published reference is worth more than five unvalidated ones. That is the whole argument for this
page existing before FiQA, ArguAna, TREC-COVID or an ablation table exist.

## Five settings the band actually guards

The band is not loose enough to hide a defect, and landing inside it depends on five independent
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
   a uniform, plausible-looking collapse rather than a visible failure.
5. **Only judged queries are scored.** SciFact ships 1,109 queries and judges 300 in the test split.
   Scoring the other 809 as zero divides the mean by roughly 3.7 and reads as catastrophic retrieval
   failure rather than as a harness bug.

A sixth is smaller but real: BEIR's `SentenceBERT` joins a document's title and text with a **single
space** (`sep: str = " "` in `beir/retrieval/models/sentence_bert.py`). Measured with a newline
instead, nDCG@10 is **0.64907** — a shift of 0.00314. Both land inside the band; the space is closer
to published and is the default.

## Two settings the band does *not* guard, and what does

This list matters more than the one above, because a number credited with catching a defect it
cannot catch is worse than a number nobody trusts. Both of these are correct in the shipped harness
and both are pinned — just not by 0.64593.

- **Max-pooling chunks to documents *before* the top-*k* cut.** Pinned by `DocumentRankingTests`,
  **not by this number.** The parity run indexes one chunk per document — that is what BEIR's
  published figures embed — and retrieves with `TopK` equal to the cutoff, so ten chunks are ten
  distinct documents and max-pooling is a literal no-op. Both orderings therefore pool the same ten
  hits and return the same ranking for every one of the 1,109 queries, so nDCG@10 is identically
  **0.64593** either way — checked rather than argued, by mutating `DocumentRanking` to cut-then-pool
  and re-running the whole measurement, which passes unchanged at both separators. What guards the
  order is a fixture where one document contributes four chunks among seven; against that,
  cut-then-pool fails **3 of `DocumentRankingTests`' 13 tests**, and the disagreement is documents
  going *missing* rather than being reordered. FiQA is where the band starts guarding this too,
  because FiQA is where a document stops being one chunk.
- **Recall's denominator is *every* relevant document, never `min(|relevant|, k)`.** Pinned by
  `IrMetricsTests`, not by this number. This is the exact opposite of setting 4 above, and that is
  precisely the trap: IDCG *must* cap at `min(|relevant|, k)` and Recall *must not*, so reusing the
  one rule for the other reports perfect recall for a run that found half the answer. On SciFact the
  mistake is invisible. The most-judged query has **5** relevant documents — the distribution is 277
  queries with 1, then 14, 4, 3 and 2 queries with 2, 3, 4 and 5 — so `min(|relevant|, 10)` equals
  `|relevant|` for all 300 of them, the wrong denominator is the right one, and Recall@10 stays
  0.78667 either way. A dataset with more than ten relevant documents for some query is what would
  make this observable.

## Running it yourself

The parity run is a test, `SciFactParityTests`, in
`tests/Rag.NET.Benchmarks.Quality.IntegrationTests`. It **skips** unless all three environment
variables are set and usable:

| Variable | What it points at |
|---|---|
| `RAGNET_ONNX_EMBED_MODEL` | An `all-MiniLM-L6-v2` ONNX export with **token-level** output (`last_hidden_state`, `[batch, sequence, dimension]`). A pre-pooled export is rejected — pooling that the generator did not do is pooling it cannot verify. |
| `RAGNET_ONNX_EMBED_VOCAB` | That model's WordPiece `vocab.txt` (one token per line, line index = token id). |
| `RAGNET_BEIR_CACHE` | A writable directory for the dataset cache. |

```bash
export RAGNET_ONNX_EMBED_MODEL=/models/all-MiniLM-L6-v2/model.onnx
export RAGNET_ONNX_EMBED_VOCAB=/models/all-MiniLM-L6-v2/vocab.txt
export RAGNET_BEIR_CACHE=~/.cache/ragnet-beir

dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests
```

The first two are the existing `RAGNET_ONNX_*` precedents, shared with the late-chunking and ONNX
embedding suites. Neither the model nor the dataset is in this repository, and neither is downloaded
by the build.

**The model.** Bring your own export, or use the one CI uses —
`huggingface.co/sentence-transformers/all-MiniLM-L6-v2`, files `onnx/model.onnx` (~86 MB) and
`vocab.txt` (~230 KB). Any `all-MiniLM-L6-v2` ONNX conversion with token-level output works; the
identity reported to the ingestion path comes from `OnnxEmbeddingOptions.ModelId`, which the parity
test sets explicitly so a file named `model.onnx` does not become every model's identity.

**The dataset.** SciFact is **downloaded on demand** into `RAGNET_BEIR_CACHE` on first run, from
BEIR's published archive, and is **never vendored into this repository**. What arrives is verified
against the MD5 BEIR publishes before anything is scored against it: the download lands on a
`.partial` file that is deleted on any verification failure, so a truncated or proxy-intercepted
fetch fails loudly instead of extracting into a short corpus that scores badly and looks exactly like
a retrieval defect. Nothing under the cache path is tracked by git.

**Where it runs.** The project declares `<RequiresSecrets>true</RequiresSecrets>`, so `nightly.yml`
selects it rather than every push doing so — see [CI and Test Tiers](./ci.md). It is a project of its
own for that reason: the 70 arithmetic tests in `Rag.NET.Benchmarks.Quality.Tests` (metrics, loaders,
cache) need no model, no corpus and no network, and putting the parity test beside them would have
carried all 70 out of the gating tier. The arithmetic gates every pull request; the run that needs an
86 MB model and several minutes of CPU runs nightly.

Selection is not execution, and for a while it was mistaken for it. `RAGNET_BEIR_CACHE` was never
supplied to that job at all, so the parity test skipped every night and the job passed having
measured nothing — and the two ONNX variables were held as repository *secrets* naming file paths
that no step ever created on a fresh runner, which fails the same way. The job now fetches
`all-MiniLM-L6-v2` from Hugging Face at a pinned revision, verifies a SHA-256, caches it, and points
all three variables at runner paths. None of the three is a credential and none of them is a secret
any more.

## Licences

BEIR itself publishes no licence for the datasets it repackages — its README says only that it
"downloaded and prepared public datasets" and that "it remains the user's responsibility to determine
whether you have permission to use the dataset under the dataset's license". So licences are recorded
per dataset, in `BeirDatasetDescriptor`, read from upstream rather than assumed.

**SciFact is licensed in two pieces**, and the harness touches both:

| Part | Licence | Source |
|---|---|---|
| `corpus.jsonl` | **ODC-By 1.0** | Semantic Scholar S2ORC abstracts |
| `queries.jsonl`, `qrels/` | **CC BY 4.0** | SciFact claims and evidence annotations |

Both require attribution; neither is public domain. From
[the upstream LICENSE](https://github.com/allenai/scifact/blob/master/LICENSE.md), verbatim: "All
claims and evidence annotations — in the files `claims_*.jsonl` — are released under CC BY 4.0", and
"The abstracts in the corpus — in the file `corpus.jsonl` — are part of the Semantic Scholar S2ORC
dataset and are licensed under ODC-By 1.0". The repository's *code* is Apache 2.0, which does not
apply to anything downloaded here.

**A disagreement, recorded rather than resolved.** The Hugging Face mirror `BeIR/scifact` declares a
single `cc-by-sa-4.0` for the whole dataset. That matches **neither** upstream licence, and it adds a
share-alike obligation upstream does not impose. **Upstream is treated as authoritative here.**
Anyone redistributing this data — as opposed to downloading it into a cache, which is all this
harness does — should read both.

Cite: Wadden et al., "Fact or Fiction: Verifying Scientific Claims", EMNLP 2020.

## Not measured, and why

- **Every dataset except SciFact.** FiQA (long documents, where HyDE should show lift), ArguAna as a
  negative control (HyDE should *not* help there; a harness that shows lift everywhere is broken),
  TREC-COVID and EnronQA all wait until parity holds — which it now does. Past SciFact the cost is
  embedding time rather than disk, so anything larger needs a cached-embeddings artifact.
- **The ablation table** — baseline dense → +BM25 hybrid → +HyDE → +reranker. Needs more than one
  dataset, and its BM25 row needs the caveat below resolved first.
- **BM25, at all.** This phase is dense-only, and that is not an oversight:
  `InMemoryBm25Index` lowercases and splits, while Anserini — which produced BEIR's published BM25
  figures — applies Porter stemming and an English stopword list, and BEIR runs it at
  `k1=0.9, b=0.4` where ours hard-codes Lucene's `k1=1.5, b=0.75`. Tokenisation dominates BM25
  scores. A `+BM25 hybrid` row published today would be incomparable to any published BM25 reference
  **while looking like validation of ours**, which is worse than not publishing it. Recorded as a
  scheduled debt in the roadmap with those numbers, so whoever builds the table knows before they
  publish it.
- **Comparative tables against other libraries.** See above — a fair one needs equivalent
  configuration, and that is separate work.
