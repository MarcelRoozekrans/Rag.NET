# Phase 6.2 coverage audit: half the allowlist already meets its bar

**Date:** 2026-08-16
**Phase:** 6.2, Milestone 6
**Method:** for each package — find every test project that references it, list its files
recursively, and **read the tests**. Not grep-and-infer; that produced three wrong answers in this
phase alone (§4).

---

## The result

**15 of the 30 packages 6.2 owns already satisfy the bar §2 sets for their kind.** The allowlist
says none of them do. Every error is in the same direction: the ledger understates existing
coverage.

| Package | §2 kind | Verdict | Evidence |
|---|---|---|---|
| `Api` | (d) | ✅ meets | `TestServer`; 401 without key, 200 with, body asserted |
| `Api.Client` | (d) | ✅ meets | `TestServer` + `CreateClient`, 5 tests |
| `Api.Grpc` | (d) | ✅ meets | `TestServer` + `GrpcChannel.ForAddress` |
| `Api.Grpc.Client` | (d) | ✅ meets | same, 4 tests |
| `Diagnostics.AspNetCore` | (d) | ✅ meets | `GetTestClient()`, real GETs, 401/404/200 |
| `Security.AspNetCore` | (d) | ✅ meets | `UseTestServer()` + `SendAsync` |
| `Storage.Sqlite` | (b) | ✅ meets | all **six** stores: real temp file, write, reopen, assert survival |
| `Caching` | (b) | ✅ meets | real `HybridCache` round trip **with a control** |
| `Diagnostics` | (c) | ✅ meets | real `Activity` spans, real collector, span assertions |
| `Mediator` | (c) | ✅ meets | real DI container; dispatch reaches the handler's dependency |
| `Telemetry` | (c) | ✅ meets | real `MeterProvider`; metric emitted and read back **exported** |
| `Evaluation` | (c) | ✅ meets | 61 real `EvaluateAsync` runs |
| `Evaluation.Ragas` | (c) | ✅ meets | per-metric evaluator runs (precision, recall, faithfulness, relevance) |
| `DataProviders` | (c) | ✅ meets | real channel queue, real job processor, real polling trigger |
| `Parsers.Office` | (a) | ✅ meets | real Word/Excel/PowerPoint fixtures (#258) |

| Package | §2 kind | Verdict | What is owed |
|---|---|---|---|
| `Memory` | (b) | ✅ **now meets** | had no test project; three added (#262) |
| `Cli` | (d) | ✅ **now meets** | real process, stdout/exit codes (#261) |
| `Hosting` | (d) | ✅ **now meets** | real `IHost`, started, scoped resolve (#261) |
| `Mcp` | (d) | ✅ **now meets** | real registration, discovery + schema (#261) |
| `Mcp.Tool` | (d) | ✅ **now meets** | real process, real stdio JSON-RPC (#261) |
| **`Resilience`** | (c) | ⚠️ **partial** | retry **count** asserted; **delay never** — see §3 |
| `Parsers.Archive/Email/Epub/Html/Pdf` | (a) | ◻ not audited | this audit covers (b), (c), (d) |
| `Chunking.CSharp`, `Chunking.Templates` | (a) | ◻ not audited | — |
| `Abstractions`, `Benchmarks.Quality` | (e) | ◻ reason-only | step 5 |

---

## 2. What this means for the phase

**Phase 6.2's remaining work is a fraction of what the allowlist implies.** Of the thirty packages:

- **15** already met their bar before this phase started
- **5** were genuinely owed a run and now have one (#261, #262)
- **1** is partially met (`Resilience`)
- **9** are the parser/chunker and reason-only groups, not covered by this audit

**Nothing here reduces the value of 6.0.** The allowlist is what made this audit possible: it forced
every package to name an owner in prose that could be read and falsified. An empty ledger field
would have hidden all fifteen. What the allowlist got wrong was its *annotations*, and it got them
wrong in a way that made the phase look bigger, not smaller — the safe direction to be wrong in.

**The bottleneck is not testing, it is the ledger.** All twenty of the packages above with a ✅ are
blocked from leaving the allowlist by the same thing: `VerificationLevels` is
`unit/container/benchmark/recorded/live/none`, and none means "exercised against real input". That
single missing value is now the only obstacle for two-thirds of the phase.

---

## 3. The one real gap: `Resilience`'s back-off is never exercised

§2(c) gives `Resilience` the sharpest bar — *"inject a real failure and assert the retry count and
the delay, because a resilience policy that silently does nothing is indistinguishable from one that
works until it is needed."*

**The count half is well covered.** `ConfigureResilienceTests` asserts 3 attempts (2 failures + 1
success), 4 attempts, and exactly 1 attempt for non-retryable exceptions.

**The delay half is not covered at all, deliberately.** The test class says so:

> *Hand-written counter fakes throughout (never a delay) … Retry tests use a zero-delay custom
> pipeline for the same reason — the default policy's 1 s exponential back-off would make a retried
> assertion a sleep.*

Every retry test supplies its own `Delay = TimeSpan.Zero` pipeline. **So the shipped default —
`MaxRetryAttempts = 3`, `Delay = 1s`, `BackoffType = Exponential`, `UseJitter = true` — is never
executed by any test.** A default that retried once, or not at all, or with no back-off, would pass
the entire suite.

That is precisely the shape of the defect this milestone exists to catch: the thing that is
configured, believed, and never run.

**It cannot be fixed from the test side alone.** `BuildPipeline` is `private static` and constructs
`RetryStrategyOptions` inline; `ResiliencePipelineBuilder.TimeProvider` is never set from
configuration or DI, so a test has no seam to substitute a `FakeTimeProvider`
(`Microsoft.Extensions.TimeProvider.Testing`, already in the local package cache but not in
`Directory.Packages.props`). The alternatives are both bad: a real ~7 s wall-clock sleep, which is
flaky and violates the fast tier, or asserting the constants, which tests nothing.

**Filed rather than fixed** — 6.2 measures, and adding the seam is a `src/` change. Recorded so the
gap is visible instead of hidden behind a green suite.

---

## 4. Method, and why it is stated

This phase produced **six** wrong answers the same way, and the count is the point. Each one was a
confident conclusion drawn from an instrument that could not see the thing it was being asked about.

| # | The instrument | What it could not see | The wrong conclusion |
|---|---|---|---|
| 1 | `tests/<project>/*.cs` — a non-recursive glob | every integration test, which lives one directory down | "nine of ten packages have no real host coverage" (#259) — six had it |
| 2 | keyword search for cache API calls | a round trip observed through a substitute's **call count** | "`Caching` is registration-only" — it has a control, and a better test than I would have written |
| 3 | `ls tests/Rag.NET.Caching.Tests/*.cs` | a file under `DependencyInjection/` | "the project has no tests" |
| 4 | `grep '"[0-9]"'` over an HTTP response body | that a **404's XML** carries a `RequestId` and a timestamp — digits | "73 of 73 packages are published" — it is 71, and the two absences were correct |
| 5 | `ReadOperationCount` / `ReadTransferCount` | **memory-mapped reads**, which do not increment either counter | "the benchmark harness is hung" — it was loading an mmap-backed ONNX model and SQLite store, and I killed it |
| 6 | a 300-second `LockDuration` reasoned about, never measured | that the premise was untested in both directions | "#246 is lock expiry, and PT5M fixes it" — the emulator honours the setting, so expiry was never the mechanism |

**All six are one error.** Not "I searched badly" — *I chose an instrument, got silence, and read the
silence as evidence.* Silence from an instrument that cannot perceive the phenomenon is not a
measurement; it is the absence of one, and the two are indistinguishable from inside the tool.

Note what the errors have in common beyond that. Every one of them was **cheap to falsify** and
would have cost less to check than the wrong conclusion cost to unwind: one recursive `grep`, one
file read, one HTTP status code instead of a body scrape, one CPU-delta sample instead of an I/O
one, one 75-second probe against the emulator. #5 cost 32 minutes of a running job and #6 cost a
merged commit with a false claim in its message.

**The rules this leaves, in the order they were learned:**

1. A keyword search can prove **presence**. It cannot prove **absence**. Absence needs the file read.
2. Any claim of the form *"X has no Y"* must come from a search that recurses, quoted so it can be
   re-run.
3. A check that cannot distinguish *"nothing there"* from *"cannot see"* proves nothing either way.
   Before believing a negative, ask what the instrument would show if the answer were the opposite.
4. Before shipping a fix, verify the **mechanism**, not only the symptom. #246 was "fixed" twice
   against mechanisms nobody had demonstrated were operating.

That this is the most repeated finding of the milestone — six instances in one phase, by the same
hand, after writing the rule down — is itself the argument for the guards this phase built. The
allowlist, the staleness twin, and `EveryPublishedDocumentationFileIsCheckedBySomething` all exist
because a person checking carefully is not a control. **They are all instruments that fail loudly
rather than quietly**, which is the only property that distinguishes them from the six above.
