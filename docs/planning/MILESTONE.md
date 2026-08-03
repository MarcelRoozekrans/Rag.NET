# Milestone 3: Quality Hardening & Evaluation

**Status:** complete — closed 2026-08-03 on the second close assessment below, after the
2026-08-02 assessment refused to close on two failing criteria and both were fixed with evidence
**Started:** 2026-07-27
**Completed:** 2026-08-03

## Goal

Close the evaluation-tooling gap and harden quality. Two of the features this milestone was
scoped around turn out to already exist in the solution but were never tested or documented, so
part of the work is finishing what was started rather than starting it.

Completed milestones are archived under `docs/planning/milestones/`.

## Definition of Done

- [x] All planned phases complete (all 16 as of 2026-08-02; Phase 3.14 closed last. The
      run-or-decline this box's tick was waiting on — TREC-COVID/EnronQA, kept in this
      milestone's scope by the Milestone 4 replan §5 — is decided: **explicitly declined at the
      close, 2026-08-03**, written on the ROADMAP debt entry as the replan required, not implied
      — see the close below)
- [x] No feature marked done in `features.md` lacks tests and docs — the detail sections and the
      summary matrix agree with each other and with the code
      **Holding as of 2026-08-03** (`81163af`; failing from the 2026-08-02 audit until then, and
      the history stays here because it is the milestone's shape in miniature). The full sweep
      this criterion needed ran on 2026-08-02, in Milestone 4's Phase 4.0: `FeatureClaimTests`
      checked all 54 then-`✅ Done` sections against the code — 73 package claims resolved,
      false-positive rate 0 of 73 — and found exactly **two** failures: the OTel ghost
      (`features.md:666-676` marked Done in `Rag.NET.Telemetry`, a package never built, against
      its own unchecked matrix row `:1135`), and `Rag.NET.Parsers.CSharp`, a real feature claimed
      under a package name that does not exist (it lives at `src/Rag.NET.Chunking.CSharp`).
      **Both were corrected at the close, ahead of the Phase 4.4/4.1 owners the 2026-08-02
      assessment assumed they had to wait for**, because the criterion's failure was
      documentation, not a missing feature: the OTel section is withdrawn from Done and now
      describes the real internal `RagTelemetry` instruments, naming first-class wiring as
      4.4's; the C# chunking section (and `docs/guide/chunking.md`, which repeated the wrong
      name) names the package that exists. `KnownFalseClaims` is **empty** — the staleness guard
      forces the deletion — and the parse is now 53 Done sections and 72 package claims, all
      verified directly, `FeatureClaimTests` 7 of 7 re-run at the close. What the sweep still
      does **not** establish: that the named code does what the row says — existence, not
      behaviour; that gap is Milestone 4's verification work, and its ledger says so.
- [x] Integration/vector-store suites run in CI (Dockerized) — holding as of 2026-08-02: `ci.yml`
      partitions the test projects into fast and Docker tiers with guards that fail a project
      landing in neither, and the latest `main` push run (30760759923, 2026-08-02) is green
      through the Docker tier
- [x] All tests passing; solution builds 0 warnings / 0 errors — **holding as of 2026-08-03**
      (failing 2026-08-02 on the first genuine execution of the post-3.15 nightly, run
      30735435427, whose one failure was the `BeirDatasetCache` download race — a harness
      concurrency defect, not a measurement; all four parity cases in the same job passed). The
      fix shipped in this milestone rather than riding to 4.1 as first routed, and found
      **three** same-shaped races, not one — the shared `.partial` download path, archive
      publication under a rival's open extraction handle, and in-place extraction itself — each
      fixed by work-under-a-GUID-name-then-rename-into-place and each mutation-verified, the
      publication collision reproducing on its test's opening attempt (`50a80cd`, `335710c`;
      the full story is in the ROADMAP's Closed debts). Proof on the condition no local run
      reproduces: nightly run **30789374909** (2026-08-03), on a cold runner cache, `env-gated`
      — the gating BEIR job — green in 19m01s. The solution builds 0 warnings / 0 errors from a
      clean restore (re-verified 2026-08-03) and push CI on `main` is green

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
13. Phase 3.14 — Library Comparison at Defaults [complete — 2026-08-02] — created by the 3.12 design,
   which decided the framing 3.7 left open: matched-configuration tables measure how carefully each
   library was configured and converge on rounding errors, because every entrant calls the same
   embedding model. The credible comparison is each library's **defaults**, same corpus and same
   model, every configuration published.
   **Shipped: five entrants on SciFact and ArguAna, one matched embedder (the pinned
   `all-MiniLM-L6-v2`), everything else at each library's own defaults, every row scored from a
   TREC run file by the one `IrMetrics` behind the published figures.** nDCG@10, SciFact /
   ArguAna: Rag.NET control **0.64593 / 0.50432**, Semantic Kernel 1.78.0 0.64593 / 0.50306,
   LangChain core 1.5.3 **0.64613 / 0.50450**, LlamaIndex core 0.14.23 0.64508 / **0.50450**,
   Haystack 3.0.0 0.62757 / 0.49715 — LangChain highest on SciFact, LangChain and LlamaIndex tied
   highest on ArguAna, published plainly. The control row reproduced the published parity figures
   exactly through the run-file boundary, which is what makes the other rows readable.
   **The headline is that defaults barely matter on these corpora**: everything except Haystack
   sits within thousandths, because most default chunk sizes exceed these documents — LangChain
   and LlamaIndex produced at most 3 and 2 units per document — so the four non-Haystack rows are
   published as **not separable** rather than ranked; **Haystack, the only entrant that chunks
   hard at its defaults (200 words → 8,042 / 11,342 units), is the only one measurably lower.**
   **Semantic Kernel has no default chunker at all** — no ingestion pipeline, `TextChunker`
   experimental and size-less — so its row *is* the parity protocol, which is why it scored
   identically to the control on SciFact. **Kernel Memory was dropped**: packages legacy, README
   "an archived research project", 0.98.250508.3 final — a finding recorded with no number
   attached. **LlamaIndex's default embedder validates an OpenAI API key at resolution**, so it
   will not run offline at its true defaults; all three Python libraries default to
   `text-embedding-ada-002`, each would-have-been embedder published beside its row. **The
   identity check found the two ecosystems' tokenizers disagreeing on accented text at their
   defaults** — HF strips accents, `Microsoft.ML.Tokenizers` does not (`müllerian` → `[UNK]`),
   0.166 max-abs apart until Python was pinned `strip_accents=False`, then all six battery
   strings bitwise identical — given a section of its own on the results page because anyone
   comparing this repository's BEIR figures against Python-stack numbers needs it. **FiQA is
   unrun for every entrant**, recorded NEVER RUN at a derived ~1 h each. Reproducible: every
   version pinned, `uv.lock` committed, run files re-scorable by an outsider's `trec_eval`,
   every figure pinned in `BeirReproduction` at ±0.005. Docs:
   `docs/reference/library-comparison.md` + `library-comparison-defaults.md`.
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
   release-readiness work. [**Re-pointed 2026-08-02 by the Milestone 4 replan, design §5:
   TREC-COVID and EnronQA stay in this milestone's scope** — run or explicitly declined before
   Milestone 3 closes, not smuggled into 4; the FiQA-qrels check recorded on the ROADMAP debt
   still comes first.]
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
16. Phase 3.8 — A/B Shadow Mode [complete — 2026-08-02] — the production half of the A/B framework,
   deferred out of 3.3 for four named failure modes, each now closed structurally.
   **Shipped:** `ShadowRagPipeline` wraps a live `IRagPipeline` via `UseShadow<TSecondary>` — the
   caller is served the primary's completed answer **before** anything shadow-related is scheduled,
   a bounded `DropWrite` queue that never blocks the enqueuer hands the work to a background
   consumer that runs the secondary and persists the pair, and `ShadowReplay.From` turns stored
   captures into `RagAbTester.CompareAsync`'s input. All in `src/Rag.NET.Evaluation/Shadow/`; the
   core package gains no Evaluation dependency.
   **The four failure modes:** *no ground truth* became the argument for capture over inline
   scoring — captures store no reference deliberately, and references supplied at replay time
   unlock all four RAGAS metrics where inline scoring is forever limited to two; *doubled spend*
   is off by default (`SampleRate` 0.0, out-of-range refused rather than clamped) with the
   secondary's spend recorded per capture over a dedicated ledger and the primary's honestly
   absent — it serves concurrent traffic on a shared ledger, so no per-request figure exists that
   would not be fabricated; *fire-and-forget loss* is counted, never silent — shutdown drains
   within a bounded timeout and `DroppedCount` plus `AbandonedCount` is the entire gap between
   the configured sample rate and the store; and *the secondary cannot break the primary*,
   verified by running the named wrong implementation (`try/catch` around an awaited secondary)
   against the suite: **5 of 12 decorator tests fail**, so the tests pin the structure, not just
   the exception path.
   **Four things the plan and design got wrong, recorded rather than absorbed:** the plan was
   missing the replay bridge entirely — the design's whole case for capturing is "score it
   offline with 3.3's harness" and nothing converted captures into its input; found by Task 1,
   added as Task 6b. Design §2's "per-variant spend is already solved" oversells —
   `RagAbTester.SpendAsync` measures a whole sequential run, which transfers to the consumer but
   not to a primary on a shared concurrent ledger. `BackgroundService.StartAsync` schedules
   `ExecuteAsync` deferred on the stopping token, so a fast stop can cancel it before it ever
   ran — measured at **1,921 of 2,000** immediate start→stop cycles — meaning a drain living only
   in `ExecuteAsync` loses everything silently even when it correctly avoids the stopping token;
   the drain lives in `StopAsync`. And `IRagPipeline` has a fifth member the plan's delegation
   list omitted: `AskStreamingAsync`, delegated and deliberately not shadowed.
   Captures hold production text **verbatim** by default; the sanitiser seam defaults to none and
   fails safe, and retention, encryption and deletion are named as the store implementer's.
   Documented in `docs/guide/shadow-mode.md`; features.md's A/B row updated in place, no new
   `KnownFalseClaims` entry.

## Close assessment (2026-08-02, at Phase 3.14's close) [superseded by the Close below, 2026-08-03]

Every DoD criterion checked against reality, the way the 2026-08-02 audit checked claims — because
an honestly open milestone is worth more than a ticked box:

1. **All planned phases complete — holds.** 16 of 16, Phase 3.14 last. Ticked above.
2. **features.md detail sections, matrix and code agree — fails, and has been recorded failing
   since the audit.** Phase 4.0's `FeatureClaimTests` holds exactly two live failures in
   `KnownFalseClaims`: `Rag.NET.Telemetry` (a package that does not exist, marked Done,
   `features.md:666-676`) → **Phase 4.4**, and `Rag.NET.Parsers.CSharp` (a real feature under a
   package name that does not exist; it lives at `src/Rag.NET.Chunking.CSharp`) → **Phase 4.1**.
   Phase 3.14 neither fixed them nor added to them. Both owners are Milestone 4 phases, so this
   criterion cannot become true inside Milestone 3 as currently scheduled — whoever closes this
   milestone either waits for those fixes or explicitly re-scopes the criterion, and that
   re-scope would have to be written here, not implied.
3. **Integration/vector-store suites run in CI (Dockerized) — holds.** Ticked above, with the run
   id it was checked against.
4. **All tests passing; 0 warnings / 0 errors — fails today, narrowly.** Build 0/0 and push CI
   green; but the first genuine execution of the post-3.15 nightly (2026-08-02) failed on the
   `BeirDatasetCache` download race — named test, named cause, recorded in the ROADMAP
   follow-up-debts list → Milestone 4, with 4.1. All four parity measurements in that job passed,
   so the failure is a harness defect, not a retrieval regression; the criterion still reads
   "all tests passing" and a red nightly on `main` fails it.

**Verdict: Milestone 3 does not close.** What remains, named and owned:

- **The two `KnownFalseClaims` entries** (criterion 2): OTel ghost → Phase 4.4,
  `Rag.NET.Parsers.CSharp` name → Phase 4.1.
- **A green nightly on `main`** (criterion 4): the `BeirDatasetCache` download race → Milestone 4,
  with 4.1.
- **TREC-COVID and EnronQA: run or explicitly declined before this milestone closes** (the
  Milestone 4 replan §5's rule — not smuggled into 4), the FiQA-qrels check first. No phase owns
  this; it sits with whoever closes the milestone, and a decline must be written into the ROADMAP
  debt entry, not implied.

## Close (2026-08-03)

The assessment above stands unedited — it refused to close on two failing criteria and named
three remaining items. Each is now resolved by evidence, on branch `fix/milestone-3-dod-blockers`,
not by re-reading the checkboxes:

1. **The two `KnownFalseClaims` entries** (criterion 2) — fixed by `81163af` and the allow-list
   emptied; the criterion's box above carries the details, and both debts have moved to the
   ROADMAP's Closed list. One correction to the assessment's own reasoning, recorded rather than
   absorbed: it said this criterion "cannot become true inside Milestone 3 as currently
   scheduled" because both owners were Milestone 4 phases. That was wrong — the criterion's
   failure was documentation drift, and a documentation fix needed neither 4.4's OTel wiring nor
   4.1's packaging pass; the ROADMAP's own OTel debt entry had said "or any documentation pass
   before it" all along.
2. **A green nightly** (criterion 4) — run **30789374909** (2026-08-03), triggered on the fix
   branch against the cold runner cache that exposed the race and that no warm-cache local run
   reproduces: `env-gated`, the gating BEIR job, green in 19m01s. The one recorded race turned
   out to be **three** — download, archive publication, extraction — each fixed by the same
   work-under-a-unique-name-then-rename shape and each mutation-verified (`50a80cd`, `335710c`);
   criterion 4's box and the ROADMAP's Closed list carry the numbers.

   > **Correction, 2026-08-03, recorded rather than absorbed.** Criterion 4 was ticked on that
   > green nightly while `Rag.NET.Benchmarks.Quality.Tests` was **failing on Windows** — 1–2
   > failures in every one of four consecutive local runs, found minutes after the milestone
   > closed. **The nightly runs on Linux, and could not see it.** The cause was not the race
   > fixes: on Windows, NTFS refuses to rename a directory while any handle is open beneath it,
   > and Defender's on-access scan holds one on just-written files — proven with a standalone
   > repro (up to 71/200 iterations denied; exclusive-open probes microseconds later found
   > nothing held, so no leaked handle of ours). The **same hazard hit `EmbeddingCache.Write`**,
   > code the race fixes never touched, so `main` was red at more than one site and had been
   > latent. Fixed in `55978b6` by a bounded retry at every publish-by-rename site, verified by
   > eight consecutive green runs by the implementer and five more independently.
   >
   > **The lesson is about the criterion, not the bug.** "All test projects passing" was read as
   > "CI is green", and CI is one operating system. A green Linux nightly is not evidence about
   > Windows, and this milestone's own record now says so — the milestone stays closed, because
   > every criterion is met on evidence today, but it was closed a few minutes early on a
   > platform nobody had checked.
3. **TREC-COVID and EnronQA — explicitly declined**, written into the ROADMAP debt entry as the
   replan required. The short form: neither verifies anything this milestone shipped — three
   corpora already answer its questions in both directions — and neither is a close-out task: no
   descriptor, no budget timing, no revision-pinned published reference, no licence
   determination exists for either. The graded-`2^rel − 1` gap stays stated on the published
   page's "Not measured, and why"; the run is re-routed to the next ablation-table re-measure
   with a Milestone 4 backstop; the FiQA-qrels check was **not** performed at the close (no warm
   BEIR cache was reachable from the closing session) and stays first on that entry.

**What this milestone set out to do, and what it did.** It was scoped as eight phases of
evaluation tooling and quality hardening — RAGAS metrics, dataset builder, A/B testing offline
and shadow, pipeline debugging, CI coverage, benchmark harness, email-parser debt — and closed
at **sixteen**, because each phase discovered the next: the day-one scope correction found the
evaluators certified-defective by their own tests; 3.5 found a test project in no solution;
3.6's close was falsified by its own review and became 3.9; 3.9's review found the duplicate
parser (3.11), whose design found the archive gap (3.10); 3.7's model provisioning turned late
chunking red for the first time since Phase 1.1 (3.13); 3.12's cost arithmetic found the chunker
emitting one chunk per word (3.16); 3.15's ablation table found the reranker sending 26% of
every document to the model as `[UNK]`. What it shipped is the ability to **demonstrate**
retrieval correctness rather than assert it: three corpora at parity with published figures,
real-protocol deltas in both directions with a supported explanation, a nine-cell ablation table
that can go down, a five-library comparison at defaults, and the harness, caches, budget table
and reproduction pins that re-check every figure on every push.

**The milestone's real lesson, stated where the next milestone will read it: not one of the
significant defects was found by a passing test.** Late chunking sat inert from Phase 1.1 to 3.7
and surfaced only when a model was provisioned; the chunker's one-chunk-per-word behaviour
surfaced because embedding-cost arithmetic did not add up; `OnnxReranker` destroying 26% of
every document as `[UNK]` surfaced because a stated prediction was contradicted by a row that
hurt where it should have helped; the nightly races surfaced only when a workflow finally ran on
a cold cache. Phase 4.0's guards now catch mechanical drift — a claim naming code that does not
exist, a gate nothing satisfies, a package no test exercises — but nothing automates stating an
expectation in advance and reporting honestly when reality disagrees. That practice, not any
artifact, is what this milestone hands to Milestone 4.

Archival to `docs/planning/milestones/` happens when this file is rewritten for the active
milestone, per house convention.

## Explicitly not in scope

- **Rag.NET CLI tool** (`ragnet eval`) — belongs with the CLI in Milestone 4, Phase 4.6.
- **Sample applications** — Milestone 4, Phase 4.5.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
| 2026-08-02 | **Every completion claim holds; the drift is in what nothing re-reads.** Two independent read-only audits. All 29 load-bearing claims checked across the 14 completed phases reproduce against code — 29 of 29. All 11 open follow-up debts are still real; none was silently fixed. No closed debt was closed on a false premise — the Phase 3.6 shape does not recur — and no debt quotes a figure invalidated by 3.15 or 3.16: the inline correction chains held. Status corrected to state 14 of 16 phases complete (3.8, 3.14 pending); no DoD box was ticked, so none needed unticking. | Six findings, all recorded 2026-08-02 in the ROADMAP follow-up-debts list unless noted. **(A)** `features.md:666-676` documents an OTel package (`Rag.NET.Telemetry`, `.UseTelemetry()`, `gen_ai.*`, `ragnet.retrieve.latency`) that does not exist while its matrix row `:1135` is unchecked — a **live DoD failure**, annotated on the DoD above → Phase 4.4. **(B)** `BuildMetadata` (`RagPipelineExtensions.cs:322-328`) drops `baseMetadata.CreatedAt`, so `TimeWeightedRetriever` scores provider-ingested documents as brand new — previously recorded only in a closed phase's design doc → new entry with a destination. **(C)** the post-3.15 `nightly.yml` has never executed, and its ~87 MB reranker download feeds no test — every consumer is behind `RAGNET_BEIR_LONG_RUNS`, which the job never sets → new entry; Phase 4.1 decides. **(D)** `IrMetrics.cs:31-32` ("FiQA and TREC-COVID are graded") contradicts the TREC-COVID debt; unverified both ways, settleable by reading FiQA's cached `qrels/test.tsv` → noted on that debt. **(E)** the flake debt's candidate mitigation shipped only in `HypotheticalCacheTests.cs` — the one filesystem test class the debt does not name — and its 110-test figure is stale (129) → noted on that debt. **(F)** smaller: duplicate RAGAS test suites (~650 vs ~1,570 lines); nothing pins the Security→Diagnostics decoration, so 3.4's claim is a cross-package inference; `AzureAISearchVectorStoreTests.cs:140`'s permanent skip was in no planning record (its Pinecone sibling `:359` is); four debts recorded somewhere but scheduled nowhere, now given destinations; three "→ Milestone 4" debts match no phase 4.1–4.6 owns; and the reranker-depth debt's "or labelled" exit is already satisfied by `retrieval-quality.md:406-413` — updated so it does not read as wholly open. | 
