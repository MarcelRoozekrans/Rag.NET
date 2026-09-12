# Pre-Push Review — 2026-09-12 18:47

| Metric | Value |
|---|---|
| Date | 2026-09-12 18:47 |
| Branch | `feat/6243-fluent-entry` |
| Base Branch | `main` |
| Commits Reviewed | 3 |
| Files Changed | 5 |
| Lines Added | 259 |
| Lines Removed | 31 |
| Verdict | **PASS** |

Phase 6.2.43 — the entry point that was already fluent. **Scope reduced mid-phase, before any code
was written.** Does not close #184; comments it instead.

---

## Plan Adherence

**Design:** `docs/plans/2026-09-12-fluent-entry-design.md`
**No implementation plan document was written, deliberately** — see below.

| Design §5 scope item, as reduced | Status |
|---|---|
| 1. The ordering test | Done — `RegistrationOrderTests`, 3 facts |
| 2. The ordering-sentence correction | Done — `docs/getting-started.md:40` |
| 3. A comment on #184 | Done — [comment 5647268457](https://github.com/MarcelRoozekrans/Rag.NET/issues/184#issuecomment-5647268457) |

**The phase shrank twice, and both times before code.** That is the headline, not a footnote.

**First reduction — the design contradicted itself.** §3 asserted both that the new builder methods
would delegate to `AddChatClient` and that nothing new would enter core's dependency closure.
`AddChatClient` lives in `Microsoft.Extensions.AI`; `src/Rag.NET` references only
`Microsoft.Extensions.AI.Abstractions`. Those cannot both hold. Corrected in `c659ef2e`, with the
closure cost measured rather than estimated — one ~518 KB assembly, dependencies mostly
framework-provided on `net10.0`.

**Second reduction — the operator challenged the design as over-engineering, and it did not
survive.** The methods unified syntax without reducing decisions: same objects constructed, same
three things the caller must know exist, and the verbose part was never the registration but
`new OpenAIClient(key).GetChatClient(…).AsIChatClient()`, unchanged either way. Against a stated goal
of "fewest decisions to something working", the decision count was identical and only the punctuation
moved. The cost was a core package reference plus **two ways to register one service** — the trap the
design had rejected its own alternative for laying. Dropped in `6be441b0`.

**No implementation plan document was written.** For one test and one sentence, a plan document
following `writing-plans` would have been the same over-engineering the phase had just removed. The
design carries the reasoning; the commits carry the work. Recorded here because skipping a normal
step should be a visible decision rather than an omission.

**Unplanned changes:** none.

---

## Code Quality

**No blockers. No warnings.** The only source change is a new test file; there is no `src/` change at
all in this branch.

### The test earns its three facts

Resolving a pipeline in each order proves only that neither throws. It does **not** prove the two
orders agree — a registration-time read could capture a different instance and still produce a
pipeline. The third fact, `BothOrdersResolveTheSameRegisteredInstances`, asserts instance identity in
both directions, which is the property the documentation sentence was actually about.

### The result, and it is a negative one for the documentation

**Both orders pass.** `docs/getting-started.md` had instructed readers to *"Register them before
calling `AddRagNet`"* since before this phase. **That constraint does not exist.** Every consumption
of `IChatClient` and `IEmbeddingGenerator` in the registration path goes through `sp.GetService` or
`sp.GetRequiredService` inside a factory lambda, which runs when the pipeline is built rather than
when the services are registered.

### Scope limits are stated in the test rather than discovered later

Its remarks record what it does **not** cover: a provider whose own `Add…` helper eagerly reads the
container, and `AddRagNetPipelineFromConfiguration`, which registers both halves itself and therefore
cannot express the wrong order.

### Rule 6 — Naming and accuracy: no findings

`TheDocumentedOrderResolvesThePipeline`, `TheReversedOrderAlsoResolvesThePipeline` and
`BothOrdersResolveTheSameRegisteredInstances` each name the fact they establish rather than the
mechanism they use.

### Rule 1 — Security: no findings

No credentials; the diff adds no runtime code. Secret and conflict-marker scans over the branch diff:
**0 matches**.

---

## Commit Hygiene

**No findings.** 3 commits, conventional, all ≤ 100 characters (max 89). No nested parentheses inside
a parenthesised group in any body — the release-please trap. No session URLs. No secrets, no
unintended files: five files, all intended, and **none of the eleven older untracked
`docs/pre-push-review-*.md` files was swept in**.

The local `commit-msg` hook from 6.2.42 was active in this clone for all three commits.

---

## Regression Testing

| Suite | Baseline | Now |
|---|---|---|
| `Rag.NET.Tests` | 1499 | **1502** — +3, the new facts |
| `Rag.NET.RepoConventions.Tests` | 111 / 2 skipped | **111 / 2 skipped** |
| `Rag.NET.PackageValidation.Tests` | 23 | **23** |
| `dotnet build Rag.NET.slnx` | 0 warnings | **0 warnings, 0 errors** |

**`PackageValidation` failed twice during this phase and both times it was right.**
`EveryPackageCarriesTheVersionGitVersionDerives` compares each `.nupkg` against the version GitVersion
derives from the branch name, and `artifacts/packages` still held packages from `feat-6242`. Repacked
— 73 packages — and it went green. **This is the third branch switch this session to hit it**, which
is a local papercut worth an issue rather than a defect in this branch.

**Guard C named the failing test both times, in one command.** That is 6.2.42's CI log dump being
used outside the phase that built it, on a real failure, and it is the first evidence the guard pays
for itself.

**Not covered:** `Rag.NET.E2ETests` (`RequiresLlm`, nightly-only) and the BEIR-gated projects, neither
of which this branch touches — it adds no `src/` code.

---

## Verdict

**PASS** — 0 blockers, 0 warnings.

The phase set out to add a fluent entry point, discovered the entry point was already fluent,
discovered its own design contradicted itself on dependencies, was challenged as over-engineering and
agreed, and shipped the one thing that turned out to be real: **a documented constraint that does not
exist, deleted on the evidence of a test rather than on the strength of an argument.**
