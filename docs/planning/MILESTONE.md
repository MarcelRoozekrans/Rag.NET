# Milestone 3: Quality Hardening & Evaluation

**Status:** active — 14 of 16 phases complete; Phase 3.8 (A/B Shadow Mode) and Phase 3.14
(Library Comparison at Defaults) pending
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
      **Failing as of the 2026-08-02 audit, not merely unfinished:** `features.md:666-676` marks
      OpenTelemetry Tracing & Metrics `✅ Done` in a package (`Rag.NET.Telemetry`) that does not
      exist — no such package, no `UseTelemetry` anywhere in `src/`, no `gen_ai.*` attribute, and
      metric names that match nothing in `src/Rag.NET/Telemetry/RagTelemetry.cs` — while its own
      matrix row (`:1135`) is unchecked. Recorded in the ROADMAP follow-up-debts list → Phase 4.4.
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
   only evidence about pooling. [Measured there, 2026-08-02: nDCG@10 **0.35569** against parity
   0.37086, delta −0.01517, in 1 h 4 m — the derivation overshot.]
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
   index 38 fewer documents than the parity leg → **Phase 3.15** [closed there, 2026-08-02 —
   stated alongside the real number: 57,600 of 57,638 indexed].
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
14. Phase 3.15 — Retrieval Ablation Table [complete — 2026-08-02] — §4–§5 of the 3.12 design: dense
   → +BM25 hybrid → +HyDE → +reranker over SciFact, FiQA and ArguAna on the parity protocol
   (judged queries only), each row labelled for what it is, plus FiQA's long-deferred real leg.
   The table had to be able to show **no** lift where none is expected; it did — and it also went
   *down* where lift was predicted, which turned out to be the phase's headline.
   **Shipped: all nine ablation cells measured, and every technique helps somewhere and hurts
   somewhere** — SciFact 0.64593 → **0.69913** (+BM25) → **0.70001** (+HyDE) → **0.68442**
   (+reranker); FiQA 0.37086 → **0.35665** → **0.36543** → **0.38458**; ArguAna 0.50432 →
   **0.51173** → **0.50293** → **0.47917**. No row is free lift, which is what makes the table
   credible rather than promotional.
   **Two of the design's three pre-committed HyDE predictions failed, and are recorded as
   failed.** FiQA, the positive control, was flat (−0.0054); SciFact, predicted "modest, smaller
   than FiQA's", took the largest lift (+0.0541); ArguAna, the negative control, held (−0.0014).
   The design's escape hatch — "FiQA shows no lift" making the table uninterpretable, since a
   weak model and an unhelpful method are indistinguishable in a run flat everywhere — did not
   apply: SciFact gained +0.0541 from the same model, prompt and cache, so FiQA's flat cell is a
   measurement, not an artefact; the surviving explanation (HyDE helps when the hypothetical
   sits closer to the corpus register than the query does) is recorded as post-hoc. ArguAna's
   mechanism was observed during generation, independently of the measurement: its hypotheticals
   are compressed restatements of the input argument, recycling its own statistics, and ArguAna
   asks for the best *counter*argument — so HyDE moves the search vector toward the query's own
   position and away from the target.
   **Two library defects found and fixed, neither what the phase set out to measure.**
   `OnnxReranker.TokenizePair` was not a WordPiece tokenizer (`a912187`): it whitespace-split and
   mapped every whole-word miss to `[UNK]` — **26.59% of SciFact's 1,112,417 words and 17.62% of
   FiQA's 7,660,017 reached the model as `[UNK]`** (0.01% and 0.10% through WordPiece) — and the
   first reranker measurement harmed every dataset (SciFact 0.56693, FiQA 0.34085, ArguAna
   0.41806); the fixed row gains **0.117 / 0.061 / 0.044 from tokenization alone**. Found
   because the row hurt on FiQA too, the in-domain corpus, and uniform harm reads as a defect
   rather than a technique. No guard could have caught it — `AssertRerankerReordered` proves the
   ranking *moved*, and garbage-but-varying scores reorder every query — so the new guard is an
   offline tokenizer round-trip that fails on the old algorithm; the fix also corrected
   hardcoded `[UNK]`/`[CLS]`/`[SEP]` ids, a truncation rule that starved long queries, and a
   `MaxLength ≤ 3` case exceeding its own ceiling, with the shared plumbing in
   `src/Shared/BertWordPieceTokenization.cs`. And the harness retrieved unjudged queries
   (`339f3d6`): SciFact retrieved 1,109 to score 300, FiQA 6,648 to score 648 — waste
   everywhere, and it broke the HyDE row's refuse-on-miss cache on the first unjudged query,
   with ArguAna concealing it because all 1,406 of its queries are judged. Metrics unchanged by
   construction and verified: parity reproduced 0.64593 and 0.50432 exactly, and every recorded
   query counter was restated across nine files.
   **FiQA's real leg, measured at last:** nDCG@10 **0.35569** against parity 0.37086, delta
   **−0.01517** — 121,236 units over 57,600 of 57,638 documents, the 38 empty entries (one
   judged relevant) contributing nothing, stated with the number as 3.12's debt required; all
   648 judged queries pooled; **1 h 4 m against a derived ~1.5–2 h**, the estimate overshooting
   and recorded rather than quietly replaced. The three real deltas — SciFact **+0.03148**,
   ArguAna **−0.02873**, FiQA **−0.01517** — support "the sign tracks whether relevance is
   passage-level or document-level", now consistent with three corpora rather than newly proven.
   **Reproducibility:** 7,062 hypotheticals for all 2,354 judged queries at
   `HypothesisCount = 3`, `openai/gpt-4o-mini` at `HydeOptions.HypothesisTemperature` (0.8),
   **$0.66**, zero failures; the temperature is in the cache identity
   (`openai/gpt-4o-mini@t0.8`), the table run never calls an LLM, a cache miss fails naming the
   key, and the cache is never committed — it derives from BEIR queries and nothing is
   redistributed. All nine figures plus FiQA's real leg are pinned in `BeirReproduction` at
   ±0.005 (`899f4b2`), on the fast tier so a mutated figure fails on every push.
   **The BM25 comparability debt is closed by labelling:** the `+BM25 hybrid` row is published
   as a Rag.NET-internal comparison with no published reference; 3.7 §2's rejection of a
   benchmark-only analyzer stands.
   **Three debts recorded, each with its origin:** the reranker row permutes only the ten
   documents it is scored on — `TopK` equals the cutoff, so Recall@10 is frozen by construction,
   visible in SciFact's reranker Recall@10 of 0.78667, identical to dense; **a design flaw in
   this phase's own plan, not a defect in the code**, and the row understates a cross-encoder →
   the next re-measure of the table, backstopped by Milestone 4; `docs/reference/ci.md` counts
   "eleven cases" and does not list the nine ablation cells now gated in `BeirRunBudget` →
   Milestone 4, with 4.1; and TREC-COVID and EnronQA, deferred again unchanged from 3.12 — the
   `2^rel − 1` path has still never seen a graded *dataset* → Milestone 4, with the
   release-readiness work.
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
   observed) → still 3.15's run [measured there, 2026-08-02: **1 h 4 m** — the derivation
   overshot].
   **Three debts recorded in the roadmap's follow-up list, each with its origin:**
   `HierarchicalMergerChunkingStrategy` never reads `MaxChunkSize` — the inverse defect, found by
   this phase's audit of the other strategies, and all three templates that delegate to it
   silently ignore the option → Milestone 4, with 4.1; the speed-benchmark page's Recursive rows
   predate packing → closed by the re-measure immediately after this phase, `cfea8e9` — packing
   made Recursive faster at every size (512 → 188 ns, 5.0 → 4.0 μs, 47.3 → 38.5 μs) with
   allocation down at 500 characters and up at 50 KB, where the `StringBuilder` joins outweigh
   the chunk objects they save; and a failure in `Rag.NET.Benchmarks.Quality.Tests` — seen once,
   86 clean runs, then **seen a second time during the whole-phase review** (`Failed: 1,
   Passed: 109`, then 110/110 on nine runs, four against a byte-identical binary) and **again
   unnamed**, because the run logged summary-only; not diagnosed, the `Directory.Delete` shape
   still a candidate, and the standing instruction stands vindicated: capture the next occurrence
   with `--logger trx` before re-running → the next occurrence, backstopped by Milestone 4.
   **The whole-phase review also found and closed a test gap:** every chunk was proven a
   substring of the source, but nothing proved the source's text all ends up in some chunk — a
   mutation deleting `SplitParts`' mid-stream flush silently discarded every run of short parts
   preceding an oversize sibling and all 1,340 core plus 110 quality tests stayed green (under
   the mutation: FiQA 121,236 → 119,279 units, SciFact 20,155 → 19,958, ArguAna 24,003 →
   23,626). Closed by `9682967`: a coverage property — every character not covered by a chunk
   span at `Overlap = 0` must be whitespace or a `'.'` on a pack boundary, the only two things
   the chunker may drop — plus a deterministic short-run-then-oversize-sibling case, both
   verified to fail under the mutation; the suite is now **1,342**. The shipped code never
   dropped anything — across 500 generated shapes and 20,000 randomized inputs every uncovered
   character was whitespace or `.` — a missing test, not a shipped bug.
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
| 2026-08-02 | **Every completion claim holds; the drift is in what nothing re-reads.** Two independent read-only audits. All 29 load-bearing claims checked across the 14 completed phases reproduce against code — 29 of 29. All 11 open follow-up debts are still real; none was silently fixed. No closed debt was closed on a false premise — the Phase 3.6 shape does not recur — and no debt quotes a figure invalidated by 3.15 or 3.16: the inline correction chains held. Status corrected to state 14 of 16 phases complete (3.8, 3.14 pending); no DoD box was ticked, so none needed unticking. | Six findings, all recorded 2026-08-02 in the ROADMAP follow-up-debts list unless noted. **(A)** `features.md:666-676` documents an OTel package (`Rag.NET.Telemetry`, `.UseTelemetry()`, `gen_ai.*`, `ragnet.retrieve.latency`) that does not exist while its matrix row `:1135` is unchecked — a **live DoD failure**, annotated on the DoD above → Phase 4.4. **(B)** `BuildMetadata` (`RagPipelineExtensions.cs:322-328`) drops `baseMetadata.CreatedAt`, so `TimeWeightedRetriever` scores provider-ingested documents as brand new — previously recorded only in a closed phase's design doc → new entry with a destination. **(C)** the post-3.15 `nightly.yml` has never executed, and its ~87 MB reranker download feeds no test — every consumer is behind `RAGNET_BEIR_LONG_RUNS`, which the job never sets → new entry; Phase 4.1 decides. **(D)** `IrMetrics.cs:31-32` ("FiQA and TREC-COVID are graded") contradicts the TREC-COVID debt; unverified both ways, settleable by reading FiQA's cached `qrels/test.tsv` → noted on that debt. **(E)** the flake debt's candidate mitigation shipped only in `HypotheticalCacheTests.cs` — the one filesystem test class the debt does not name — and its 110-test figure is stale (129) → noted on that debt. **(F)** smaller: duplicate RAGAS test suites (~650 vs ~1,570 lines); nothing pins the Security→Diagnostics decoration, so 3.4's claim is a cross-package inference; `AzureAISearchVectorStoreTests.cs:140`'s permanent skip was in no planning record (its Pinecone sibling `:359` is); four debts recorded somewhere but scheduled nowhere, now given destinations; three "→ Milestone 4" debts match no phase 4.1–4.6 owns; and the reranker-depth debt's "or labelled" exit is already satisfied by `retrieval-quality.md:406-413` — updated so it does not read as wholly open. | 
