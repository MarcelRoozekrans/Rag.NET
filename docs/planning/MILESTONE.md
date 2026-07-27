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

Neither is documented: `docs/guide/evaluation.md` has no section for either. So the matrix row
was the honest one — the code shipped, the feature did not.

**Correction (2026-07-27, found during Phase 3.1 Part C).** This section originally said both
features had **no test coverage**. That was wrong. `tests/Rag.NET.Tests/Evaluation/` holds seven
files and roughly 620 lines covering all four metrics, the suite and the dataset builder; the
original search was scoped to test *projects* matching `*Evaluation*` and missed a subfolder of
the main test project.

The reality is worse than the claim it replaces. Those tests **certify the defects**:
`ScoreAsync_MalformedClaimsJson_ReturnsOneGracefully` asserts that a malformed model reply scores
`1.0` — the best possible value — and calls it *"gracefully"*. `ScoreAsync_EmptySourceChunks_ReturnsZero`
asserts that "nothing was retrieved" means "retrieval was maximally bad".

That changes the shape of the work but not its necessity. A green suite agreeing with wrong
behaviour is a stronger illusion of correctness than no suite at all, and it means these phases
must **rewrite existing assertions**, not merely add missing ones — which is exactly the kind of
change nobody makes by accident.

An evaluator that is wrong does not fail loudly. It returns a plausible number, and a plausible
number is indistinguishable from a correct one whether nothing tests it or a test agrees with it.
Anything scored by this code until now should be treated as unverified.

**Consequence:** 3.1 and 3.2 are completion phases — audit against the metric definitions, fix
what is wrong, re-point the tests that pin the old behaviour, document, reconcile `features.md`.
Assume nothing works until a test says so *and the test is right*.

## Phases

1. Phase 3.1 — RAGAS Metrics: verify, test, document [pending]
2. Phase 3.2 — Evaluation Dataset Builder: verify, test, document [pending]
3. Phase 3.3 — A/B Testing Framework [pending]
4. Phase 3.4 — Pipeline Debugger / Trace Viewer [pending]
5. Phase 3.5 — CI Integration Coverage [pending]
6. Phase 3.6 — Email Parser Debt [pending]

## Explicitly not in scope

- **Rag.NET CLI tool** (`ragnet eval`) — belongs with the CLI in Milestone 4, Phase 4.6.
- **Sample applications** — Milestone 4, Phase 4.5.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
