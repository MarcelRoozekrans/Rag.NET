# A/B Shadow Mode Implementation Plan (Phase 3.8)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Wrap a live pipeline so the caller always gets the primary answer, while a sampled share of requests also runs a secondary out-of-band and persists the pair for offline comparison.

**Architecture:** A decorator over `IRagPipeline` that returns the primary's response **before** scheduling anything, a bounded channel that **drops and counts** rather than blocking, and a `BackgroundService` that **drains on shutdown within a timeout and reports what it lost**. Nothing is scored on the request path.

**Tech Stack:** .NET 10, `System.Threading.Channels`, `Microsoft.Extensions.Hosting`, xUnit v3.

**Design:** `docs/plans/2026-08-02-ab-shadow-mode-design.md`. Read §0 and §1 before writing code — §0 is why this captures instead of scoring, §1 is the isolation contract and is the part that must not be got wrong.

---

## The neighbouring pattern is the wrong answer, twice

`src/Rag.NET.DataProviders/EventDriven/` already has a channel-plus-background-service queue. **Read it, then deliberately diverge from it in two places**, because copying it would introduce exactly what this design forbids:

| It does | Shadow mode must | Why |
|---|---|---|
| `BoundedChannelFullMode.Wait` — a full queue **blocks the enqueuer** | **Drop and count** | Blocking couples the primary's latency to the secondary's. A slow secondary would throttle the very requests it is supposed to be invisible to. This is design §1's whole point. |
| `ExecuteAsync` treats cancellation as clean exit — queued work is **abandoned** | **Drain within a bounded timeout, then report what remains** | This is the fire-and-forget loss the roadmap names. Silent abandonment makes the capture rate quietly lower than the sample rate, and every offline comparison then rests on a denominator nobody can reconstruct. |

If you find yourself writing `FullMode.Wait` or letting the token cancel a drain, stop — you have copied the wrong half.

---

## Two properties that must be true of the finished thing

**1. A failing or slow secondary cannot affect the primary.** `IRagPipeline.AskAsync` returns `Task<RagResponse>` and **throws** rather than returning a `Result`, so a secondary exception surfacing on the primary's task breaks a request the caller was already served.

**2. Nothing is lost silently.** Every dropped or abandoned capture is counted and observable.

Both get direct tests. **A test that merely shows the happy path works proves neither.**

---

## Conventions

- Warnings are errors: MA0051 (≤60-line methods), MA0015, MA0048 (one public type per file, name matches file), MA0006, MA0008, MA0009, MA0023, MA0132, MA0140, ZA0601, ZA0501, EPS05/EPS06, **EPC12/EPC13 (a catch reading only `ex.Message` is an error — relevant, you are writing failure paths)**, HLQ001 (no boxing), HLQ003/HLQ004/HLQ006/HLQ012/HLQ013, NU1510, RCS1194, CA2022, MA0060, MA0025. **No `#pragma` or `SuppressMessage`.**
- xUnit v3, `TestContext.Current.CancellationToken`, **no sleeps** — this is a concurrency feature and `Task.Delay` in a test is how you get a suite that fails on a loaded CI box. Use `TaskCompletionSource`, `Channel`, or a controllable clock.
- No central package management; inline floating versions.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. One per task.
- **Never `git add -A`** — explicit paths.
- **Timestamp trap:** build without `--no-build`, confirm the recompile line.
- **Any new package must declare `<VerifiedBy>`** — Phase 4.0's ledger test will fail otherwise. Declare it honestly (`unit` unless you genuinely exercise it against a real dependency).
- **The core `Rag.NET` package must not gain a dependency on `Rag.NET.Evaluation`.** Decide where this lives and say why in your report.

**Baselines:** `Rag.NET.Tests` **1342**, `Rag.NET.RepoConventions.Tests` **30** (29 passed + 1 by-design skip), `Rag.NET.Benchmarks.Quality.Tests` **129**.

**Existing types to build on:** `AbVariant` (`src/Rag.NET.Evaluation/AbVariant.cs`), `VariantFailure` (`.../Internal/VariantFailure.cs` — internal, and the right type for a secondary that threw), `RagAbTester` (offline scorer, Phase 3.3), `CostTrackingChatClient` / `CostAccounting` (`src/Rag.NET/Resilience/`), `RagResponse` (`Answer`, `Sources`).

---

## Task 1: the captured pair and its store seam

**Files:**
- Create: the capture record, the store interface, and an in-memory implementation
- Create: matching tests

What is captured (design §4): the question, both answers, both context sets, both spends, both latencies, the variant labels, a timestamp — **and failures**. A secondary that threw is a result; dropping it biases the comparison toward whatever the secondary handles well.

**The store is a seam with an in-memory default.** Persisting production traffic is an application decision — file, table, blob, queue. Ship the interface and the trivial implementation, not a storage engine.

Enough must be captured for `RagAbTester` to run offline over it. **Read `RagAbTester.CompareAsync`'s inputs and make sure the record actually satisfies them** — a capture that cannot be fed to the scorer defeats the phase.

**Commit:** `feat(evaluation): the captured shadow pair and its store seam`

---

## Task 2: the queue drops and counts

**Files:**
- Create: the bounded queue and its options
- Create: tests

`Channel.CreateBounded` with **`BoundedChannelFullMode.DropWrite`** (or an explicit `TryWrite`-returns-false path — your choice, justify it), a capacity option, and a **counter of dropped captures**.

**Tests that matter:**
- Enqueueing to a full queue **returns immediately** and does not block. Prove it without sleeping — a `TaskCompletionSource` the consumer never completes, plus an assertion that the enqueue task is already completed, is the shape.
- A drop **increments the counter**.
- The counter is observable (see Task 6).

**Mutation to verify your test:** change the full mode to `Wait` and confirm the non-blocking test fails. If it still passes, the test does not test what it claims and the whole isolation contract rests on nothing.

**Commit:** `feat(evaluation): a shadow queue that drops rather than blocking`

---

## Task 3: the consumer drains on shutdown and reports what it lost

**Files:**
- Create: the `BackgroundService` consumer
- Create: tests

On `StopAsync`: stop accepting, drain what is queued **within a bounded timeout**, then **report how many items were still queued** when the timeout expired.

- Draining without a timeout hangs shutdown.
- Draining without reporting is the same silent loss the roadmap names, in slower clothing.

**Tests:**
- Items queued at shutdown are processed rather than abandoned.
- A drain that cannot finish within the timeout **reports the remainder** rather than exiting quietly.
- A capture whose processing throws is recorded as a `VariantFailure` and does **not** kill the consumer — one poisoned capture must not stop every later one.

**Commit:** `feat(evaluation): drain shadow captures on shutdown, and report what is lost`

---

## Task 4: the decorator — the isolation contract

**Files:**
- Create: the `IRagPipeline` decorator
- Create: tests

**This is the task the phase turns on.** Three properties, in the order design §1 gives them:

1. **The primary's response is returned before the shadow is scheduled.** Not concurrently — before.
2. **Scheduling is non-blocking and cannot throw into the caller.**
3. **The shadow runs on the background consumer**, where an exception becomes a `VariantFailure`.

**Write these tests, and write them first:**

- **A secondary that throws does not affect the primary.** The caller receives the primary's answer normally.
- **A secondary that never completes does not delay the primary.** No sleeps: give the secondary a `TaskCompletionSource` you never complete, and assert the primary's task completes.
- **A full queue does not delay or fail the primary.**
- **The primary's answer is returned even when the store throws.**

**The wrong implementation to avoid, named explicitly:** `try { await secondary; } catch { }` around an awaited call. It catches the exception but still couples the primary's latency to the secondary's, so property 2 fails while every "does it throw" test passes. **If your test suite would pass against that implementation, the suite is wrong** — check this deliberately and report the result.

**Commit:** `feat(evaluation): shadow the secondary without coupling it to the primary`

---

## Task 5: sampling, off by default, and registration

**Files:**
- Create/modify: options and the builder extension

`SampleRate` defaults to **0.0** — registering shadow mode does nothing until someone sets a number. The secondary costs real money on someone else's bill; nobody should discover doubled spend by upgrading a package.

```csharp
services.AddRagNet(rag => rag
    .UseShadow<SecondaryPipeline>(o => o.SampleRate = 0.05));
```

**Tests:** rate 0 shadows nothing; rate 1 shadows everything; an out-of-range rate is rejected at registration rather than silently clamped. **Sampling must be deterministic under test** — inject the randomness rather than calling `Random.Shared` inside the decorator, or you get a flaky suite and no way to assert the 0 and 1 cases.

**Also record both variants' spend into the capture.** `CostTrackingChatClient` and `CostAccounting` exist; `RagAbTester.SpendAsync` already reports per-variant cost. The doubled cost must be visible per request, not as an unexplained rise on a bill.

**Commit:** `feat(evaluation): shadow sampling, off unless asked for`

---

## Task 6: the counters are observable, and the payload can be sanitised

**Files:**
- Modify: the queue/consumer to surface counters
- Modify: the capture path for the sanitiser seam

**Counters** — captures enqueued, dropped, processed, failed, abandoned at shutdown. `RagTelemetry` (`src/Rag.NET/Telemetry/`) is the existing meter; follow its naming rather than inventing a scheme. A counter nobody can read is not observability.

**Sanitiser seam (design §5).** Captured payloads contain production questions and retrieved document text **verbatim** — a data-protection concern the offline harness never had, because Phase 3.3 compared a question set the operator wrote. `Rag.NET.Security` ships `PiiChunkSanitiser`, `RegexChunkSanitiser` and `LlmPiiChunkSanitiser`.

Run captures through an optional sanitiser seam. **Do not take a dependency from the evaluation path onto `Rag.NET.Security`** — a seam the application fills, defaulting to none.

**Commit:** `feat(evaluation): shadow counters and a sanitiser seam for captured payloads`

---

## Task 7: documentation

**Files:**
- Create/modify: a guide page, and `docs/reference/features.md`

State plainly, next to the switch rather than in a page nobody reads:

- **Enabling shadow mode roughly doubles pipeline spend** on sampled requests.
- **It persists user input.** Whatever real users typed, plus retrieved document text. Retention, encryption and deletion belong to whoever implements the store — say so rather than implying protection the default does not provide.
- **Two of four metrics are available offline**, because Context Precision and Context Recall throw on an empty `ReferenceAnswer` and production has none. Adding reference answers later makes all four available — that is the reason capture beats inline scoring.
- **No significance testing.** Two averages over ten captured pairs is not a result, and the docs say so rather than the code pretending to prevent it.

**`features.md` is now machine-checked** (Phase 4.0): if you mark anything `✅ Done`, the package it names must exist, or `FeatureClaimTests` fails. Do not create a third entry in `KnownFalseClaims`.

**Commit:** `docs(evaluation): shadow mode, its cost, and what it persists`

---

## Task 8: close the phase

**Files:**
- Modify: `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`

Flip 3.8 to complete in **both files in the same commit**. Record which of the roadmap's four named failure modes are closed and how, and that Milestone 3 is now 15 of 16 with only 3.14 outstanding.

**Commit:** `docs(planning): close phase 3.8`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 Warning(s), 0 Error(s).
2. Baselines hold; RepoConventions still 30 (or higher if you added a package, which the ledger test will demand a `<VerifiedBy>` for).
3. No new `#pragma` or `SuppressMessage`, no new `KnownFalseClaims` entry.
4. `git status` clean.

**Report:** every commit hash, verbatim build and test output, **where you put the code and why the core package did not gain an Evaluation dependency**, the mutation results for Tasks 2 and 4, **whether your Task 4 suite would pass against the `try/catch`-around-an-await implementation**, and everything this plan got wrong.

That last item is not a formality. Every phase in this milestone has had a plan asserting something the code did not do — Phase 3.16's plan specified a mathematically impossible assertion, Phase 3.15's design was wrong about which side truncation starved, and Phase 4.0's plan mis-stated the gating-site count. All three were caught by an agent checking the claim against the code rather than trusting it.
