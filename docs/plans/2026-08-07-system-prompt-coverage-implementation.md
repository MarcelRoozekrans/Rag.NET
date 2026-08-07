# Prove the Prompt Contract — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `RagOptions.SystemPrompt`'s behaviour provable at the boundary users meet it, so issue #56's question can be answered by a test rather than by reading source.

**Architecture:** Three layers — engine-level mocks (exist, one needs fixing), a full-pipeline mock (new), and a real-model test in the existing `RequiresLlm` tier. No behaviour changes.

**Tech Stack:** .NET 10, xUnit v3, NSubstitute, `TestChatClientFactory` (OpenRouter, falling back to Ollama).

**Design:** `docs/plans/2026-08-07-system-prompt-coverage-design.md`

---

## Context

Issue #56 reported `SystemPrompt` "not working". It works — all four engines apply it. The defect is that **this could only be established by reading source**. Two things explained the report: the `[Source N]` context labels invite citations, and the model paraphrased a canned string.

**This phase changes no production behaviour.** If you find yourself editing an engine, stop and report why.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`.** MA0051 (≤60-line methods), MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits **with bodies**, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **Subject under 100 characters** — commitlint enforces it.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- **An incremental build is not a measurement** — `--no-incremental` for any quoted count.
- A file watcher edits `.csproj` and `.slnx` concurrently — **`git status` before committing**; it has previously removed a project from the solution mid-rebase.

**Baselines:** `Rag.NET.Tests` **1181**, `RepoConventions` **44 + 1 skip**.

---

## Task 1: Fix the test that passes for the wrong reason

**File:** `tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs:39-52`

`AskAsync_WithCustomSystemPrompt_UsesIt` asserts `msgs[0].Text == "Custom prompt"`. That holds **only because its fixture supplies no `ConversationHistory`**. `ChatAnswerEngine` deliberately places leading history system messages *before* the primary prompt, so with history the caller's prompt is at index 1.

**Step 1:** Change the assertion to match by **role and content**, not position — e.g. the message list contains a `ChatRole.System` message whose text is `"Custom prompt"`.

**Step 2:** Run it. It must still pass — this is a strengthening, not a behaviour change.

**Do this first.** Adding coverage on top of an assertion that passes for the wrong reason compounds the problem.

---

## Task 2: Pin the ordering

**File:** same

Add a test for the case the fixed assertion now permits: a `ConversationHistory` **beginning with a system message**, plus a custom `SystemPrompt`.

Assert the exact order:

1. `[0]` — the history's system message
2. `[1]` — the caller's `SystemPrompt`
3. then remaining history turns, then the `Context:`/`Question:` user turn

**This is deliberate behaviour** — a host-injected prompt-hardening prefix must not be shadowed by a per-request prompt (`ChatAnswerEngine.cs`, the comment above the `historyStart` loop). **Pin it; do not change it.**

---

## Task 3: The full-pipeline mock test

**Files:** `tests/Rag.NET.Tests/` — place it beside the existing pipeline tests, not in `AnswerGeneration/`.

Today nothing asserts that `RagOptions.SystemPrompt` survives `RagPipeline.AskAsync`. A user configures options and calls the pipeline; a future change swallowing the prompt would pass CI.

Build a pipeline with a substituted `IChatClient`, call `AskAsync` with a custom `SystemPrompt`, and assert the client received it as a system message.

**Cover `AskStreamingAsync` too** — it builds messages through the same helper, but nothing asserts the prompt arrives there. **Streaming is where users are least likely to have a debugger attached.**

---

## Task 4: The real-model test

**File:** `tests/Rag.NET.E2ETests/` — the existing `RequiresLlm` tier.

`TestChatClientFactory.Create(ollamaFixture)` already returns an **OpenRouter**-backed client when `OPENROUTER_API_KEY` is set and falls back to Ollama otherwise. **Use it; add no new infrastructure.**

Existing E2E tests use `[Collection("Ollama")]` with `OllamaFixture` and `PgVectorFixture`. **Reuse that harness rather than standing up a second one** — and if the test needs no vector store, say so and keep it lighter.

**The assertion must be a marker, not exact text.** Set a system prompt instructing the model to end every answer with a distinctive marker (e.g. `<<RAGNET>>`), then assert the marker appears in the response.

**Why:** the reporter's prompt asked for an exact sentence and the model paraphrased it. Asserting exact text would make this test flaky **for the very reason the issue exists**. A marker instruction is followed reliably across models and still proves the whole chain — options → pipeline → engine → provider → output.

**If the marker proves unreliable against the fallback model**, report it rather than loosening the assertion until it passes. A test that passes because it asserts almost nothing is worse than no test.

---

## Task 5: Documentation

**File:** `docs/guide/retrieval.md` — it already documents `SystemPrompt` around lines 870 and 886, so extend there rather than starting a new page.

**The `[Source N]` context format.** Show the actual shape the model receives:

```
Context:
[Source 1]
<chunk text>

---

[Source 2]
<chunk text>

Question: <the query>
```

State plainly that **a custom system prompt does not suppress citation behaviour** — the labels themselves invite it, which is exactly what issue #56 observed. If a caller does not want citations, they must say so in their prompt.

**`IPromptObserver`.** It receives the complete message list immediately before it is sent, and is **undocumented**. It is the diagnostic that would have let the reporter answer his own question in one run. Include a short worked example.

**Also document the ordering** pinned in Task 2 — that leading `ConversationHistory` system messages precede the caller's `SystemPrompt`, and why.

---

## Task 6: Close

Record in `docs/planning/ROADMAP.md`:

- The origin (issue #56) and that **`SystemPrompt` was never broken** — the defect was untested, invisible behaviour.
- **The existing test passed for the wrong reason**, and what that would have hidden.
- The three coverage layers and which tier each runs in.
- The `Temperature`-on-newer-models question, **left open** and awaiting the reporter's answer — do not record it as fact.

**Do not tick a DoD box this did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
```

The E2E test runs in the nightly tier; **if you can run it locally with `OPENROUTER_API_KEY` set, do, and report the model used and the result.**

**The deliverable is that issue #56's question is answerable by pointing at a test.**
