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
8. Phase 3.11 — Duplicate Email Parser [pending] — a bug found in the 3.9 review, and the most
   urgent thing outstanding in this milestone. `Rag.NET.Chunking.Templates` holds a second
   `EmailDocumentParser`; it and `QAPairsDocumentParser` both claim `application/octet-stream`,
   the fallback type for any unknown extension. Registered alongside `AddEmailParser()`, one
   `.eml` carrying one unknown-extension attachment throws out of the whole document parse.
   Scheduled before 3.10 despite the higher number — it is a live defect, not new capability.
9. Phase 3.10 — Archive Parser (ZIP) [pending] — raised while designing 3.9. A zipped attachment
   matches no parser today, so it is logged and dropped and never indexed. Runs straight after 3.9
   because it reuses that phase's traversal driver and descent policy for `zip → .eml → zip`.
   Stretches this milestone's "quality hardening" goal to a feature row, deliberately: the
   machinery is shared and building it twice is the more expensive choice.
9. Phase 3.7 — Retrieval Quality Benchmark Harness [pending] — public benchmarks with published
   reference numbers, so retrieval correctness is demonstrable rather than asserted. SciFact
   first, to prove parity before adding breadth. Distinct from Phase 3.2's synthetic builder,
   and from the existing speed benchmarks.
10. Phase 3.8 — A/B Shadow Mode [pending] — the production half of the A/B framework, deferred out
   of 3.3. Production traffic has no ground truth, so only the reference-free metrics apply; it
   also doubles spend per request and must never let a secondary failure reach a caller the
   primary already served.

## Explicitly not in scope

- **Rag.NET CLI tool** (`ragnet eval`) — belongs with the CLI in Milestone 4, Phase 4.6.
- **Sample applications** — Milestone 4, Phase 4.5.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
