# Retrieval Ablation Table — Design (Phase 3.15)

**Date:** 2026-08-01
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.15
**Carries:** §4–§5 of `docs/plans/2026-07-31-beir-expansion-ablation-design.md`, split out of 3.12 before its plan was written

§4–§5 of the 3.12 design already settled **what each row is** — that was the expensive part, and this
phase starts from it rather than rediscovering it. What follows designs only the three pieces 3.12
deferred, plus the two items the roadmap carried into this phase.

## 0. What is already decided, and is not reopened here

- **dense** — the anchor, validated against a published figure.
- **+BM25 hybrid** — **incomparable to any published BM25** and labelled as such *in the table
  itself*. Anserini, the source of BEIR's published BM25 figures, applies Porter stemming and an
  English stopword list at `k1=0.9, b=0.4`; `InMemoryBm25Index` lowercases and splits at
  `k1=1.5, b=0.75`. These are not two settings of one retriever. `IHybridSearchable` is implemented
  only by the Azure AI Search and Weaviate stores, so in-memory this row is `InMemoryBm25Index`
  combined with dense results via RRF. **The roadmap says this decision is due before the row is
  published: it is published as an internal comparison, and 3.7 §2's rejection of a benchmark-only
  analyzer stands.**
- **+HyDE** — the only nondeterministic row.
- **+reranker** — `OnnxReranker`, not `CohereReranker`: local, free, deterministic, no API key or
  per-call cost in a table meant to be re-runnable.

## 1. The table is 12 cells, and it runs on the parity protocol

Three datasets × four rows, all on the **parity** leg — one chunk per document, truncated at 256.

That is not a cost decision. The dense row's whole value is that it is validated against a published
figure, and only the parity protocol produces a number comparable to one. Running the ablation on
the real leg would measure four techniques against an anchor that agrees with nothing external, so
each row's delta would rest on nothing. **The anchor must be the validated number.**

FiQA's real leg (§5) is a separate question and is not part of the table.

## 2. Every cell needs an expectation stated before it is measured

The 3.12 design gave FiQA and ArguAna theirs. Running all three datasets means SciFact needs one
too, otherwise its row is a number nobody can interpret — and an uninterpretable number in a table
of interpretable ones is worse than a gap, because it reads as evidence.

| Dataset | HyDE expectation | Why |
|---|---|---|
| **FiQA** | **clear lift** — the positive control | Colloquial financial questions against expository answers. The vocabulary gap between "how do I..." and the text that answers it is exactly what a hypothetical document closes. |
| **ArguAna** | **no lift, plausibly negative** — the negative control | The query *is* an argument, ~1,190 characters of it. HyDE replaces a long, information-rich query with a generated one, discarding more than it adds. |
| **SciFact** | **modest lift, smaller than FiQA's** | A claim and an abstract are already in the same scientific register, so there is less gap to close than FiQA has. |

**These are predictions, not descriptions.** Each can fail, and the phase reports failure as a
finding rather than adjusting the prose afterwards — the standard 3.16 set when it predicted
ArguAna's recovery and had to be able to report that it had not happened.

**The positive control does real work here.** If FiQA shows no lift, the phase cannot conclude "HyDE
does not help" — the alternative is that the LLM is too weak or the prompt is wrong, and a run
showing no lift *anywhere* cannot distinguish those. That is precisely why the LLM choice below is
not a matter of taste.

## 3. The LLM, and why the cache is the deliverable

**Cost.** 2,354 evaluated queries (SciFact 300, FiQA 648, ArguAna 1,406) × `HypothesisCount = 3` —
the library's own default, which averages three hypotheses specifically to smooth single-hypothesis
variance — is **7,062 generations**, roughly 1.4M tokens. On a cheap hosted model that is one to two
dollars, paid once.

`HypothesisCount` stays at its default. Lowering it to 1 would cut the cost threefold and measure a
configuration nobody ships with — the mistake the two-run protocol exists to avoid.

**The generation is a one-time, local, manual step. The table run never calls an LLM.** This is the
whole reproducibility mechanism. Hosted models are not bit-deterministic even at temperature 0, so
"regenerate and compare" is not available; the cached text *is* the experiment.

Three consequences the implementation must honour:

**A cache miss during a table run is a failure, not a silent regeneration.** If a missing key
quietly triggered a call, a partially-warm cache would blend two generations into one table and
nothing would say so. The run refuses and names the missing key.

**The cache key covers the model identity, the prompt template, the query, and the hypothesis
index.** Mirror `EmbeddingCache`'s length-prefixed construction — 3.12 built that specifically so
`("ab","c")` cannot collide with `("a","bc")`. Changing the prompt must miss, not silently reuse
hypotheticals generated from a different instruction.

**The cache is provisioned, never committed.** This is not only about size (~5 MB). The
hypotheticals are generated *from BEIR queries*, and 3.12 established that FiQA names no licence and
restricts commercial use twice in upstream's own words, with the project's position being that
**nothing is redistributed**. Committing text derived from those queries would quietly reverse that
position. Same treatment as the datasets and the model: fetched, verified, cached, never vendored.

**No test may be gated on an API key.** 3.12 found three inert guards — tests whose gate can never
be satisfied on a runner, reporting green while proving nothing. A key-gated test is that shape
exactly. The generation step is a documented local operation; CI runs the table from the cache or
skips with a message naming what is missing and how to produce it.

## 4. The cross-encoder

`OnnxRerankerOptions` already takes a `ModelPath` and a `VocabPath`, so the reranker needs no code —
it needs a provisioned model, and provisioning it is a solved problem in this repository. The
embedder is fetched at a **pinned Hugging Face revision** with a **SHA-256 verified** download and a
cached artifact; the cross-encoder follows the same path, pinned the same way.

The model is the standard BEIR reranker, `cross-encoder/ms-marco-MiniLM-L-6-v2`, in an ONNX export
pinned by revision **and** checked by digest. **The exact revision and digest are looked up during
implementation and recorded in the descriptor, not guessed here** — the same refusal 3.12's plan
made about published nDCG figures, and for the same reason: a wrong pin is worse than no pin.

One thing to verify rather than assume: the reranker's `MaxLength` defaults to 512 tokens while the
embedder truncates at 256. They are different models doing different jobs and need not agree, but
the phase should state which each uses rather than leaving a reader to infer that one of them is a
mistake.

## 5. FiQA's real leg, and the 38 documents that are not there

Deferred out of 3.12 with a measured basis, re-based by 3.16: **121,236 chunk embeddings plus 6,648
query embeddings at the ~27 embeddings/s observed across the two packed real legs — a derived
~1.5–2 hours.** Derived, not measured. **The first run is the measurement**, and it replaces the
estimate rather than confirming it.

It adds a third corpus shape: documents long and heterogeneous in their own right. ArguAna's fan-out
turned out to be mostly the chunker's short-part defect (9.5× before packing, 2.8× after — 3.16
confirmed the attribution) and SciFact's abstracts are uniform. FiQA is neither.

**38 of FiQA's 57,638 corpus entries have an empty title and an empty text, and one of them
(`117276`) is judged relevant.** The real leg therefore indexes 38 fewer documents than the parity
leg. That is a genuine protocol difference, already surfaced as `UnindexedDocumentCount` rather than
papered over with a placeholder chunk, and it **must be stated alongside FiQA's real number** — a
recall figure computed against a corpus missing a relevant document needs the reader to know.

## 6. Budget and gating

`BeirRunBudget` records what each dataset costs under each protocol and **throws on an unmeasured
dataset/protocol pair** — deliberately, so a new case cannot silently default into or out of the
nightly. Every new cell is a new pair. All twelve ablation cells and FiQA's real leg need entries,
and the entries must say whether each cost is **measured, derived, or estimated**.

The nightly keeps its 120-minute budget. Nothing in this phase is added to it unasked; the same
`RAGNET_BEIR_LONG_RUNS` gate applies, and each gated case skips with its cost and the command that
runs it.

## 7. What would make this table untrustworthy

Recorded as the failure modes to test for, not as a list of worries:

- **A table that only goes up.** If every row lifts on every dataset, the negative control has
  failed and the table is measuring something other than what it claims. ArguAna is the check.
- **A partially-warm hypothetical cache** blending two generations — §3's refusal-on-miss.
- **The reranker reordering nothing.** A cross-encoder that returns the input order produces a row
  identical to the one above it. Assert the ordering actually changed, the way 3.12 asserted pooling
  actually pooled rather than trusting the number.
- **RRF hiding a broken BM25.** If `InMemoryBm25Index` returned nothing, RRF would degrade to the
  dense ranking and the hybrid row would quietly equal the dense row. Assert BM25 contributed.

## Out of scope

- **TREC-COVID** — still the first graded-relevance dataset, and `IrMetrics`' `2^rel - 1` path has
  never seen a graded *dataset*. Unchanged from 3.12.
- **EnronQA**, for the private-corpus and multi-tenant story.
- **Changing `InMemoryBm25Index` for comparability** — §0; 3.7 §2's objection is unchanged.
- **A hosted reranker.** `CohereReranker` exists and stays out, for the reason §0 gives.
