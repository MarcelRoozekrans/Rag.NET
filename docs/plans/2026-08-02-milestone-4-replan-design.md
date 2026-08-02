# Milestone 4 Replan — Design

**Date:** 2026-08-02
**Supersedes:** Milestone 4's Definition of Done and phase list as written before Milestone 3 ran
**Motivated by:** the 2026-08-02 Milestone 3 audit, and four defects that a green build did not detect

## 0. Why this replan exists

Milestone 4's Definition of Done asks for: all phases complete, 0 warnings from a clean restore,
all non-Docker unit test projects passing, CI producing NuGet packages, and a v1.0 tag.

**Every one of those was already true** while all four of these were live:

| Defect | How long it was latent | What eventually found it |
|---|---|---|
| Late chunking inert | since Phase 1.1 | Phase 3.7 provisioning a model for the first time |
| Default chunker emitting one chunk per word | unknown, pre-3.12 | Phase 3.12's embedding-cost arithmetic not adding up |
| `OnnxReranker` destroying 26% of every document as `[UNK]` | since the package existed | a **stated prediction being contradicted** |
| `features.md` advertising a `Rag.NET.Telemetry` package that does not exist | unknown | one deliberate read of the docs against the code |

**Not one was found by a test.** They were found by measuring something against reality for the
first time, or by an expectation being contradicted. A DoD built on "the tests pass" cannot detect
any of them, because the tests did pass, throughout.

The replan changes the DoD from a set of things that are *true* into a set of claims that could be
*false* — and adds the mechanisms that would falsify them.

## 1. The scale, measured

| | count |
|---|---:|
| Shippable packages | **72** |
| Features marked **✅ Done** in `features.md` | **54** |
| Feature matrix rows | 107 |
| Test projects | 69 |
| Test gating sites (`SkipWhen`/`SkipUnless`/`Fact(Skip)`/`#if`) | 29 |
| Milestone 3 open debts | 11 |
| Additional findings from the 2026-08-02 audit | 6 |

**One of the 54 "Done" claims has ever been checked against the code, and it was false.** That is the
whole argument for this milestone's shape. The OTel row was not caught by a process; an audit
happened to look. The other 53 remain unverified, and a hit rate of one-for-one does not predict
zero.

**Decision taken: v1.0 covers all 72 packages and all 54 claims.** No preview tier. That is the
larger commitment, and it makes verification the dominant cost of the milestone rather than a
footnote to it.

## 2. Phase 4.0 runs first, and it is a measurement

The strongest lesson of Milestone 3 is that **cheap measurement reorders expensive plans**. Phase
3.12 costed an embedding run and discovered the chunker; Phase 3.15 predicted a reranker result and
discovered a tokenizer. In both cases the finding changed what the next phase should be.

So Milestone 4 opens with a phase that builds no features and ships nothing:

**Phase 4.0 — Verification Ledger and Claim Agreement.** Three mechanical guards, all cheap, each
targeting a shape this milestone has already found:

**(a) `features.md` must agree with the code.** Parse the status table; for every row marked Done,
assert the package, type or method it names exists and is public. This is already a stated DoD
criterion with nothing behind it, which is exactly why the OTel row survived. Expect it to fail on
first run — **that is the point, and the count of failures is the phase's most valuable output.**

**(b) No test may be gated behind a condition nothing can satisfy.** Four such tests are known: three
env-gated guards whose secrets were never set, and one behind `#if ENABLE_OCR` that no workflow
defines, so it is not merely skipped but **not compiled**. Enumerate all 29 gating sites and assert
each gate is satisfiable somewhere — CI, the nightly, or a documented local procedure. A gate nothing
satisfies is a test that reports green while proving nothing.

**(c) Every package declares how it has been verified.** Extend the existing convention rather than
inventing one: `ci.yml` already selects test projects by `<RequiresDocker>` and `<RequiresSecrets>`,
and `TestProjectTierTests` already fails a project that does not declare its tier. Add a
`<VerifiedBy>` declaration to each of the 72 packages — `unit`, `container`, `recorded`, `live`, or
`none` — and assert none is `none` at release.

**The ledger converts an unbounded task into a tracked one.** "Exercise every package once" is not
schedulable; "no package may declare `none`" is. And a package sitting at `unit` is visible, which is
the state late chunking was in for five phases.

## 3. Recorded responses for the ~20 live packages

Roughly 20 packages talk to services no test can reach: twelve connectors (Jira, Slack, Notion,
Gmail, Confluence, Asana, Airtable, Bitbucket, Zendesk, Teams, GitHub, GitLab), the cloud vector
stores, and the hosted LLM and reranker providers.

**Each is hit once by hand, its real HTTP exchange recorded, scrubbed, and committed as a fixture the
tests replay.** The credential cost is paid once per service instead of every CI run, and the tests
then prove the code handles **what the service actually returns**.

That last property is the whole reason for choosing this over contract tests written against a
documented schema. A contract test verifies the code against *our belief* about the API; if the
belief is wrong the test agrees with the bug. This milestone has now hit that shape seven times, and
the reranker is the sharpest instance: its smoke test passed because common whole words survive a
naive tokenizer, so the test agreed with the defect.

Three things the recordings must get right:

- **Scrubbing is a correctness requirement, not hygiene.** Tokens, cookies, account ids and customer
  data must be removed before commit, and a test must assert no recording contains anything matching
  a credential pattern. A leaked token in a fixture is worse than no fixture.
- **Recordings state when and against what version they were taken.** An API that has drifted since
  recording produces a test that passes against a service that no longer exists. Staleness is not
  detectable from inside the recording, so it is recorded as metadata and reviewed at release.
- **A recording is evidence of one exchange, not of the API.** The ledger entry says `recorded`, not
  `live`, and the difference is meaningful.

## 4. Guards must assert correctness, not activity

`AssertRerankerReordered` passed while a quarter of every document reached the model as `[UNK]`,
because reordering is not reordering *correctly* — garbage-but-varying scores reorder every query.
The same shape is still present in `AssertBm25Contributed` and `AssertHydeDiverged`, and probably
elsewhere.

**Audit every guard that asserts something happened, and ask what it would take for the guard to pass
while the thing was wrong.** Where the answer is "very little", add an assertion about the result
rather than the event. This is a sweep, not a phase — but its findings are scheduled.

## 5. Where Milestone 3's leftovers land

Eleven open debts plus six audit findings. Three currently route to "Milestone 4" with **no owning
phase among 4.1–4.6**, which satisfies the house rule's letter and none of its purpose.

Each gets an owner:

| Debt | Owner |
|---|---|
| `features.md` OTel ghost package | **4.0** (found by the agreement test) then **4.4** |
| `BuildMetadata` drops `CreatedAt` — real defect, `TimeWeightedRetriever` reads it | **4.2**, with options work on that path |
| Nightly downloads an 87 MB reranker no test can reach | **4.0** (found by the gate-satisfiability test) |
| Two never-run live suites; OCR test not compiled | **4.0** ledger, resolved by §3 |
| `IrMetrics` vs the TREC-COVID debt on FiQA's qrels | **4.0** — one read of the qrels settles it |
| Duplicate RAGAS test suites | **4.1**, with the packaging pass |
| Security→Diagnostics decoration pinned by no test | **4.3**, with logging |
| Seven sidebar-unreachable guide pages; `docs.yml` | **4.5**, with samples and docs |
| Streamed prompt correlation; `MessageChild` union | **4.3** |
| Parser replacement API; connector field selections; `CreatedAt`; webhook parsers; cron polling | **4.2** |
| TREC-COVID / EnronQA | **stays in Milestone 3's scope**, not smuggled into 4 |

## 6. The new Definition of Done

Every criterion below can be false, and something checks it:

- [ ] All planned phases complete
- [ ] Full solution builds 0 warnings / 0 errors from a clean restore
- [ ] All test projects passing — **and no test is gated behind a condition nothing satisfies** (4.0b)
- [ ] **Every one of the 54 `features.md` Done claims names code that exists** (4.0a)
- [ ] **No package declares `VerifiedBy=none`** (4.0c)
- [ ] **Every package talking to a live service has a scrubbed, dated recording** (§3)
- [ ] CI pipeline builds, tests, and produces NuGet packages
- [ ] Release tagged v1.0

The three bold criteria are the ones the old DoD lacked, and each maps to a defect that shipped
undetected.

## 7. What this does not fix

Stated so the milestone does not claim more than it does.

- **A recording proves one exchange happened, once.** It does not prove the API still behaves that
  way, and it cannot detect drift.
- **The ledger proves a package was exercised, not that it was exercised well.** `VerifiedBy=unit` on
  a package with one trivial test satisfies the letter of it.
- **The agreement test checks that named code exists, not that it does what the row says.** It would
  have caught the OTel ghost, because that row names a package that does not exist. It would not
  catch a row describing a real method inaccurately.
- **None of this would have caught the reranker tokenizer.** That was found by a prediction being
  contradicted, and no mechanical guard substitutes for stating an expectation in advance and
  reporting honestly when it fails. **That practice is the finding of Milestone 3, and it is not
  automatable.**

## Out of scope

- **A preview/experimental tier.** Considered and rejected: v1.0 covers all 72 packages.
- **Re-measuring the ablation table at reranking depth** — Milestone 3's debt, optional improvement.
- **New features.** This milestone ships what exists and proves it works.
