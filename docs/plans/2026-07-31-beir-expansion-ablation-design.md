# BEIR Expansion & Ablation Table — Design (Phase 3.12)

**Date:** 2026-07-31
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.12
**Covers:** the datasets and the ablation table Phase 3.7 deferred until parity held

Parity holds — SciFact nDCG@10 = 0.64593 against a published ≈ 0.645 — which was the precondition
3.7 attached to everything here. The harness is built and verified; this phase spends it.

> **Scope split, decided after this design was approved and before the plan was written.** As
> designed, this phase is four independent pieces: the two-run protocol, the embeddings cache, two
> datasets, and an ablation table whose rows need an `IChatClient` and a cross-encoder model. That is
> larger than Phase 3.7, which built the entire harness plus a production embedder in six tasks.
>
> **§1–§3 stay in 3.12** — the two-run protocol, the cache, FiQA and ArguAna, with parity numbers for
> both. That part stands entirely on published references and needs no new model dependency.
>
> **§4 and §5 move to Phase 3.15**, the ablation table. It needs the cache 3.12 builds, and its three
> awkward parts — provisioning an LLM, caching hypotheticals so the table is reproducible, and
> obtaining a cross-encoder — deserve designing rather than bolting onto a phase that already has two
> datasets. ArguAna is still its negative control; 3.12 simply lands the dataset first.
>
> Both sections are kept here rather than moved, because the reasoning in §4 about what each row *is*
> was the expensive part to work out and 3.15 should start from it rather than rediscover it.

## 0. A contradiction in the entry that scheduled this phase

The roadmap says FiQA is "the first dataset where chunk-to-document max-pooling is not a no-op".
**That is only true if we chunk FiQA**, and chunking is exactly what makes a number incomparable to
BEIR's published figures.

3.7 indexed one chunk per document for SciFact because that is what the published numbers embed:
each corpus entry as a single sequence truncated at `max_seq_length`. Applying the same protocol to
FiQA truncates long documents at 256 tokens, discards the rest, and leaves max-pooling a no-op
exactly as on SciFact. The two goals — comparability and exercising the aggregation — cannot both
be met by one run.

## 1. Two runs per dataset

| Run | Protocol | Compared against |
|---|---|---|
| **parity** | truncate at 256, one chunk per document, BEIR's own setup | the **published** figure |
| **real** | Rag.NET's actual chunking, max-pool to documents | **our own parity run** |

The parity run answers *is the harness correct*. The real run answers *what does this library
actually do*, and it is the first thing that has ever exercised max-pooling against a corpus rather
than against `DocumentRankingTests`' fixture.

The real run's number is deliberately **not** compared to anyone's published figure. Doing so would
be the error this phase exists to avoid: a number produced under one protocol judged against a
reference produced under another.

## 2. Datasets: FiQA and ArguAna

**FiQA** — long documents, where the aggregation stops being theoretical and where HyDE is expected
to help.

**ArguAna — the negative control, and the most valuable single item in this phase.** HyDE should
*not* help there. A harness that shows lift everywhere is broken, and without a case whose expected
answer is "no change", nothing distinguishes a working ablation from an optimistic one. It validates
the *method* rather than adding a number, which no further dataset does.

TREC-COVID (the first graded-relevance dataset) and EnronQA stay deferred.

## 3. Cached embeddings

Not an optimisation. SciFact's 5,183 documents take ~355 s; FiQA is roughly an order of magnitude
larger, and the ablation multiplies that across rows. Every row after the first re-embeds the same
corpus, so without a cache the table costs hours per run.

Keyed on **model revision plus text hash**, cached beside the datasets in `RAGNET_BEIR_CACHE`, never
vendored. Only the real run needs its own vectors, because its text units differ.

## 4. The ablation rows are not uniform, and each is labelled for what it is

The roadmap says the table uses "the behaviours that already exist". Two of the four need
infrastructure that does not.

| Row | Character |
|---|---|
| **dense** | free, deterministic, validated against published — the anchor |
| **+BM25 hybrid** | free, deterministic, **incomparable to any published BM25** |
| **+HyDE** | **needs an LLM** — the only nondeterministic row |
| **+reranker** | needs a **cross-encoder model** |

**+BM25 hybrid.** `InMemoryBm25Index` lowercases and splits; Anserini — the source of BEIR's
published BM25 figures — applies Porter stemming and an English stopword list at `k1=0.9, b=0.4`
against our hard-coded `k1=1.5, b=0.75`. These are not two settings of one retriever. The row is an
**internal** comparison and is labelled as such in the table itself, because it otherwise sits in a
table whose first row *is* validated against a published reference and will read as validation of
our BM25 to every reader, including whoever wrote it.

Also note `IHybridSearchable` is implemented only by the Azure AI Search and Weaviate stores. The
in-memory store is not hybrid, so this row combines `InMemoryBm25Index` with dense results via RRF.

**+HyDE.** `LlmHypotheticalDocumentGenerator` requires an `IChatClient`. Re-running would give
different hypotheticals and therefore different numbers, which is not an ablation — it is noise with
a table around it. **The generated hypotheticals are cached alongside the embeddings**: generated
once, reused thereafter, so the row is reproducible and its cost is paid once.

**+reranker.** `OnnxReranker` rather than `CohereReranker` — local, free and deterministic, and
provisioned the same way the embedder already is. `CohereReranker` would put an API key and a
per-call cost into a table meant to be re-runnable.

## 5. What the ablation must be able to show

- **Lift where lift is expected**: HyDE on FiQA.
- **No lift where none is expected**: HyDE on ArguAna. If this shows lift, the harness is wrong and
  the rest of the table is untrustworthy.

A table that only ever goes up is indistinguishable from a table that cannot go down.

## Out of scope

- **TREC-COVID and EnronQA.** TREC-COVID is the first graded-relevance dataset — `IrMetrics` uses
  `2^rel - 1` and has a graded fixture, but no graded *dataset* has been through it, which deserves
  its own attention.
- **Comparative tables against other libraries** — now **Phase 3.14**, with a framing decided here:
  matched-configuration comparisons measure how carefully each library was configured, not the
  libraries, and converge on near-identical numbers because they all call the same embedding model.
  The credible comparison is **each library's defaults**, same corpus and same model, every
  configuration published. That measures the decisions a library makes on your behalf, which is a
  real difference rather than a rounding error.
- **Changing `InMemoryBm25Index` for comparability.** §2 of the 3.7 design rejected building a
  benchmark-only analyzer for the dense path; the objection is unchanged here.
