# Milestone 3: Quality Hardening & Evaluation

**Status:** active
**Started:** 2026-07-27

## Goal

Close the evaluation-tooling gap and harden quality. Two of the features this milestone was
scoped around turn out to already exist in the solution but were never tested or documented, so
part of the work is finishing what was started rather than starting it.

Completed milestones are archived under `docs/planning/milestones/`.

## Definition of Done

- [ ] All planned phases complete
- [ ] No feature marked done in `features.md` lacks tests and docs — the detail sections and the
      summary matrix agree with each other and with the code
- [ ] Integration/vector-store suites run in CI (Dockerized)
- [ ] All tests passing; solution builds 0 warnings / 0 errors

## Scope correction found at milestone start (2026-07-27)

`docs/reference/features.md` contradicted itself, and the ROADMAP inherited the error.

The detail sections for **RAGAS-Style Metrics** and **Evaluation Dataset Builder** both read
`**Status:** ✅ Done`, while their rows in the summary matrix (`:1054`, `:1055`) read `[ ]`. The
ROADMAP is generated from *"the unchecked items in features.md"*, so it scheduled both as
greenfield Phase 3.1 and 3.2 work. They are not greenfield: `src/Rag.NET.Evaluation.Ragas`
(four metrics, suite, report) and `src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs` landed on
2026-04-11, three months before the ROADMAP was written, with a design doc and a plan.

**Correction (2026-07-27, found during Phase 3.1 Parts C and D).** This section originally said
both features had **no test coverage and no documentation**. Both claims were wrong, and they
share a cause: a truncated or narrowly-scoped search read as an exhaustive one.

- `tests/Rag.NET.Tests/Evaluation/` holds seven files and roughly 620 lines covering all four
  metrics, the suite and the dataset builder. The search was scoped to test *projects* matching
  `*Evaluation*` and missed a subfolder of the main test project.
- `docs/guide/evaluation.md` already carried a RAGAS section of about 160 lines. The heading
  survey that missed it was truncated at 20 results and stopped short of them.

The reality is worse than the claim it replaces, and it sharpens why this milestone exists. The
feature did not look half-finished from any angle: it had a suite, a guide section and a `Done`
marker, and **all three agreed with each other and were wrong together**. The guide stated
`precision = relevant / total` as the definition of Context Precision, which is not the RAGAS
metric. The tests certified the defects — `ScoreAsync_MalformedClaimsJson_ReturnsOneGracefully`
asserts that an unreadable model reply scores `1.0`, the best possible value, and calls it
*"gracefully"*; `ScoreAsync_EmptySourceChunks_ReturnsZero` asserts that "nothing was retrieved"
means "retrieval was maximally bad".

The only signal that anything was wrong was an unchecked checkbox.

That changes the shape of the work but not its necessity: these phases must **rewrite existing
assertions and an existing guide section**, not merely add missing ones — exactly the kind of
change nobody makes by accident.

An evaluator that is wrong does not fail loudly. It returns a plausible number, and a plausible
number is indistinguishable from a correct one whether nothing tests it or a test agrees with it.
Anything scored by this code until now should be treated as unverified.

**Consequence:** 3.1 and 3.2 are completion phases — audit against the metric definitions, fix
what is wrong, re-point the tests that pin the old behaviour, document, reconcile `features.md`.
Assume nothing works until a test says so *and the test is right*.

## Phases

1. Phase 3.1 — RAGAS Metrics: verify, test, document [complete — 2026-07-28]
2. Phase 3.2 — Evaluation Dataset Builder: verify, test, document [complete — 2026-07-28]
3. Phase 3.3 — A/B Testing Framework [complete — 2026-07-28]
4. Phase 3.4 — Pipeline Debugger / Trace Viewer [complete — 2026-07-28]
5. Phase 3.5 — CI Integration Coverage [complete — 2026-07-29]
6. Phase 3.6 — Email Parser Debt [complete — 2026-07-29]
7. Phase 3.9 — Email Traversal Flattening [complete — 2026-07-29] — ran out of numeric order. Reopened
   out of 3.6, which closed it on a premise its own review falsified: the recursion does not cross
   the `IDocumentParser` boundary on its dominant path, so an explicit stack drained LIFO does
   flatten it, at identical section ordering. Kept its number because three committed artifacts
   already reference it. (The `Stack<IAsyncEnumerator<DocumentSection>>` this entry named until the
   3.9 design is a type that cannot express descent at all — the unit is a traversal frame.)
8. Phase 3.11 — Duplicate Email Parser [complete — 2026-07-29] — a live defect found in the 3.9
   review, run ahead of 3.10 for that reason. Both `Rag.NET.Chunking.Templates` parsers claimed
   `application/octet-stream`, the fallback type for any unknown binary, so one `.eml` carrying one
   `payload.dat` threw out of the whole document parse. **Shipped:** the claim is gone from both
   `CanParse` implementations; `EmailAttachmentDispatcher` contains a throwing attachment parser to
   its own attachment, so the next parser to accept a type and then fail costs one attachment
   rather than the document; and the Templates type is now `EmailTemplateDocumentParser`, which
   settles the name collision with `Rag.NET.Parsers.Email`'s `EmailDocumentParser`.
   **Converted rather than fixed:** both parsers still claim `message/rfc822`. The phase
   deliberately did not pick a winner — they serve different purposes, and only the user can say
   which they want — so registering both is an `InvalidOperationException` at `AddRagNet` time
   naming both parsers, both registration calls and the way out, driven off a `ParserClaim` each
   registration declares. The whole-phase review found that guard blind to the two parsers
   `AddRagNETServices()` auto-registers, which made the same silent failure reachable with no
   third-party package at all, and moved the parser opt-out off the chunking options objects, where
   setting it silently discarded every other option on them.
9. Phase 3.10 — Archive Parser (ZIP) [complete — 2026-07-30] — raised while designing 3.9. A zipped attachment
   matches no parser today, so it is logged and dropped and never indexed. Runs straight after 3.9
   because it reuses that phase's traversal driver and descent policy for `zip → .eml → zip`.
   Stretches this milestone's "quality hardening" goal to a feature row, deliberately: the
   machinery is shared and building it twice is the more expensive choice.
10. Phase 3.7 — Retrieval Quality Benchmark Harness [complete — 2026-07-30] — public benchmarks with published
   reference numbers, so retrieval correctness is demonstrable rather than asserted. SciFact
   first, to prove parity before adding breadth. Distinct from Phase 3.2's synthetic builder,
   and from the existing speed benchmarks.
11. Phase 3.13 — Late Chunking Newline Defect [complete — 2026-07-30] — **a live production defect**,
   found only because 3.7 provisioned an ONNX model for the first time in this project's history.
   BertTokenizer deletes newline and tab characters, tripping `OnnxTokenEmbeddingGenerator`'s
   offset-alignment guard, and `LateChunkingStrategy` swallows it into chunks with
   `Embedding = null`. Late chunking had never worked on any document containing a paragraph break —
   shipped in 1.1, inert since, invisible behind a test nothing could run.
   **Shipped:** a length-preserving substitution of a space for `\n`, `\t` and `\r` in
   `BertOnnxPlumbing`, before every `EncodeToTokens` call rather than in the late-chunking path
   alone, and `LateChunkingIntegrationTests` now passes against a real model with a tab case added.
   Nightly is green again.
   **Broader and milder than recorded.** Broader: `\t`, `\r`, trailing newlines, other control
   characters, NFD text and **all CJK** were affected too, and it corrupted the *tokens* rather than
   only the offsets — `"alpha\n\nbeta gamma"` tokenized as `alphabet | ##a | gamma`, so an
   offsets-only fix would still have embedded a word the document never contained. `OnnxSpladeEncoder`
   and `OnnxEmbeddingGenerator` shared it and never tripped the guard, because they discard offsets.
   Milder: `EmbeddingBehavior` backfills empty embeddings, so the fallback degraded to ordinary
   embeddings rather than losing chunks — nothing was unretrievable; a configured feature silently
   did not apply.
   **CJK and NFD stay refused** — neither is length-preserving under normalization and CJK offsets
   go genuinely out of bounds — now with a message naming the cause and a documented limit in
   `docs/guide/chunking.md`. The phase also corrected 3.7's "the separator shifts the number by
   0.00314": that shift was this defect, not the separator.
12. Phase 3.12 — BEIR Expansion & Ablation Table [complete — 2026-07-31] — the datasets 3.7 deferred
   until parity held. **Scope split before the plan was written:** the two-run protocol, the
   embeddings cache and the two datasets shipped here; the ablation table, TREC-COVID, EnronQA and
   the BM25 comparability debt moved to **Phase 3.15**, whose rows need an `IChatClient` and a
   cross-encoder that nothing in this project had.
   **Shipped:** three parity numbers against three published references, all in band — SciFact
   **0.64593** vs 0.64508, FiQA **0.37086** vs 0.36867, ArguAna **0.50432** vs 0.50167, every figure
   looked up from MTEB's results repository at a pinned *model revision* rather than assumed. All
   three land above published by 0.001–0.003; three out of three in the same direction is recorded as
   an open observation with candidates named (tie-breaking, the truncation boundary) and neither
   checked nor claimed.
   **The real run is the first thing that has ever exercised chunk-to-document max-pooling against a
   corpus**, and the counters prove it rather than assert it: 0 queries pooled under the parity
   protocol on either dataset, and all 1,406 of ArguAna's and all 1,109 of SciFact's under Rag.NET's
   chunking.
   **The two real deltas have opposite signs, which is the phase's most useful result.** Default
   chunking **costs 0.0784 nDCG@10 on ArguAna** (0.50432 → 0.42594, Recall and MRR falling with it,
   so documents are missed rather than reordered) and **gains 0.0100 on SciFact** (0.64593 →
   0.65589, over 56,707 units from 5,183 documents and up to 221 from one, with Recall flat —
   0.78667 → 0.78222 — and MRR up 0.60483 → 0.62057, the same documents better ordered).
   [Re-measured by 3.16 under the packing chunker: SciFact 0.67742 (+0.03148 vs parity), ArguAna
   0.47559 (−0.02873) — both improved, both signs held, and ArguAna's ~63% recovery is what
   confirmed this entry's fragmentation explanation.] Offered as reasoning and not
   as measurement: the sign tracks whether relevance is passage-level, as a claim supported by two
   sentences inside an abstract is, or document-level, as a whole counterargument to a whole argument
   is. A single dataset could not have distinguished those.
   **FiQA's real run was deliberately not made**, with a measured basis: 429,850 chunks against a
   parity leg that took 1 h 11 m for 64,247 embeddings — eight to nine hours [re-based by 3.16:
   packing cuts the leg to 121,236 chunks and a derived ~1.5–2 h] → **Phase 3.15**, which
   needs a cached-embeddings artifact anyway and where FiQA adds a third corpus shape rather than the
   only evidence about pooling.
   **Corrected rather than silently rewritten:** the roadmap entry that scheduled this phase said
   FiQA is "the first dataset where max-pooling is not a no-op" and 3.7's said SciFact abstracts are
   "mostly single-chunk". Both are false — 99.2% of SciFact's abstracts exceed the chunk size against
   FiQA's 51.0%, and pooling is a no-op under the *parity protocol*, which every dataset is measured
   under, so no parity band will ever guard the aggregation order. Two source files still carried the
   same false premise and the whole-phase review corrected both: `BeirDatasetDescriptor.FiQA`'s
   remarks, knowingly deferred at the time, and `DocumentRanking`'s own summary, which nobody had
   listed.
   **Two debts recorded with their numbers:** `RecursiveChunkingStrategy` never merges short split
   parts back towards `MaxChunkSize` (FiQA 429,850 units from 57,638 documents, up to 1,723 from one)
   → **Phase 3.16** [closed there, 2026-07-31 — confirmed, and it was three faults rather than
   one]; and FiQA's 38 empty corpus entries, one judged relevant, which make the real leg
   index 38 fewer documents than the parity leg → **Phase 3.15**.
   **A third was found and closed inside the phase.** The nightly `run-secrets` job selected the
   whole integration project with no filter under a 120-minute timeout, against cases that now cost
   hours — so it would have failed on a timeout and reported on parity as little as skipping did.
   `BeirRunBudget` now records what every dataset costs under every protocol and gates the four the
   job cannot afford behind `RAGNET_BEIR_LONG_RUNS`, which `nightly.yml` never sets; each skips
   naming its measured cost and the command that runs it. The nightly keeps the SciFact and ArguAna
   parity legs and loses corpus-scale max-pooling, which is stated rather than buried.
   **What the gate costs is that a gated number is re-checked by nothing**, so the phase closed with
   the pins the review found missing: `BeirDatasetDescriptorTests` now pins FiQA's and ArguAna's
   parity targets and requires every target's digits to appear in the source string citing it, and
   `BeirReproduction` pins the **measured** figures — separately from the published band, because
   ±0.02 is wider than most defects and a cut-then-pool mutation of `DocumentRanking` passed both the
   band and the real run's 0.5×–1.5× envelope green.
13. Phase 3.14 — Library Comparison at Defaults [pending] — created by the 3.12 design, which decided
   the framing 3.7 left open: matched-configuration tables measure how carefully each library was
   configured and converge on rounding errors, because every entrant calls the same embedding model.
   The credible comparison is each library's **defaults**, same corpus and same model, every
   configuration published.
14. Phase 3.15 — Retrieval Ablation Table [pending] — §4–§5 of the 3.12 design: dense → +BM25 hybrid
   → +HyDE → +reranker, with each row labelled for what it is. Owns the BM25 comparability debt,
   because the `+BM25 hybrid` row is where it becomes live; owns FiQA's real run, TREC-COVID and
   EnronQA. Must be able to show **no** lift where none is expected (HyDE on ArguAna) — a table that
   only goes up is indistinguishable from one that cannot go down.
15. Phase 3.16 — Recursive Chunking Short-Part Merge [complete — 2026-07-31] — the "probable
   defect" 3.12 measured, with confirmation required before fixing. **Confirmed, and it was three
   faults rather than one:** the size limit was not consulted before splitting
   (`SplitRecursively` checked fit only on the branch where the current separator was absent, so a
   35-character section became 2 chunks against a 512-character limit); split parts were never
   packed back (with no sentence separator present the recursion reached the `" "` separator and
   emitted **one chunk per word** — 150 words became 150 chunks of 4 characters, which is what
   settled "is it deliberate?"); and `Split(". ")` destroyed sentence punctuation. Also fixed: a
   silent chunk-position fallback that reported a wrong position as a real one is now an
   exception, justified by 500 generated-input iterations proving it unreachable.
   **The existing tests asserted the defect and the docs drew it** —
   `ChunkAsync_SplitsByParagraphsFirst` asserted 2 chunks for a 35-character input and passed, and
   the chunking guide's flowchart had no merge step — the sixth instance in this milestone of
   code, tests and docs agreeing with each other and being wrong together.
   **Chunk counts at stock options:** SciFact 56,707 → **20,155** (10.9× → 3.9×, worst document
   221 → 25), FiQA 429,850 → **121,236** (7.5× → **2.1×**, worst 1,723 → 41 — closing the
   ~2×-suggested / 7.5×-measured discrepancy that opened the investigation), ArguAna 82,618 →
   **24,003** (9.5× → 2.8×, worst 285 → 16).
   **Parity runs unmoved — the phase's regression gate:** SciFact 0.64593, ArguAna 0.50432, both
   separators, identical to 3.12 to five decimal places; FiQA's parity 0.37086 not re-run (gated,
   and the parity protocol never calls the split path). **Both real runs improved:** SciFact
   0.65589 → **0.67742** (delta vs parity +0.00995 → +0.03148; Recall@10 0.81322, MRR@10 0.63757,
   1,109 queries pooled), ArguAna 0.42594 → **0.47559** (delta −0.07839 → −0.02873; Recall@10
   0.77240, MRR@10 0.38435, 1,406 queries pooled). **The design's falsifiable prediction held:**
   §6 said packing should substantially shrink ArguAna's loss if 3.12's fragmentation explanation
   was right, and that a flat ArguAna would mean correcting the roadmap instead. ArguAna
   recovered ~63% of the loss, so the explanation stands; the signs stay opposite, and the
   residual is what packing cannot touch — whole-argument queries scored against 512-character
   pieces. FiQA's real-leg cost is revised from an estimated 8–9 h to a **derived** ~1.5–2 h
   (121,236 chunk + 6,648 query embeddings at the ~27 embeddings/s the packed real legs
   observed) → still 3.15's run.
   **Three debts recorded in the roadmap's follow-up list, each with its origin:**
   `HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize` — the inverse defect, found by
   this phase's audit of the other strategies, and all three templates that delegate to it
   silently ignore the option → Milestone 4, with 4.1; the speed-benchmark page's Recursive rows
   predate packing → being closed by the re-measure running immediately after this phase; and one
   unreproduced failure in `Rag.NET.Benchmarks.Quality.Tests` — 86 clean runs since, ruled out as
   branch-related with evidence, not diagnosed, next occurrence to be captured with
   `--logger trx` before re-running → the next occurrence, backstopped by Milestone 4.
16. Phase 3.8 — A/B Shadow Mode [pending] — the production half of the A/B framework, deferred out
   of 3.3. Production traffic has no ground truth, so only the reference-free metrics apply; it
   also doubles spend per request and must never let a secondary failure reach a caller the
   primary already served.

## Explicitly not in scope

- **Rag.NET CLI tool** (`ragnet eval`) — belongs with the CLI in Milestone 4, Phase 4.6.
- **Sample applications** — Milestone 4, Phase 4.5.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
