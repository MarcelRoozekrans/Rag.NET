# Prove the Prompt Contract — Design

**Date:** 2026-08-07
**Origin:** [issue #56](https://github.com/MarcelRoozekrans/Rag.NET/issues/56) — *"SystemPrompt ?"*

## 0. What happened, and what the real defect is

A user reported that `RagOptions.SystemPrompt` "does not work" against Azure OpenAI GPT-5: their
prompt asked for the exact sentence *"I cannot find any relevant information."* and the model
returned a paraphrase, followed by *"Sources used: Source 1, Source 2…"*.

**`SystemPrompt` works.** It is applied in all four engines — `ChatAnswerEngine`
(`opts.SystemPrompt ?? DefaultSystemPrompt`), `MapReduceAnswerEngine`, `RefineAnswerEngine`,
`FlareAnswerEngine` — and preserved by `PromptHardeningAnswerEngineDecorator`. A passing test
already asserts the custom prompt is the system message the client receives.

Two things explain the report, neither a bug:

- **The citation line is not an instruction we inject.** Context is formatted as
  `[Source 1]\n<text>\n\n---\n\n[Source 2]…`. Those labels are delimiters; the model saw labelled
  sources and cited them unprompted. Supplying a custom system prompt does **not** suppress that,
  because the labels themselves invite it.
- **The model paraphrased a canned string** — ordinary behaviour, especially for newer models.

**The actual defect is that none of this could be established without reading the source.** The
prompt contract is real, load-bearing, and untested at the boundary where users meet it. This phase
changes **no behaviour**; it makes existing behaviour provable and visible.

## 1. Three layers of proof, each proving something different

| Layer | Proves | Tier |
|---|---|---|
| Mock, engine-level *(exists)* | the message list is built correctly | gating |
| **Mock, full pipeline** *(new)* | `RagOptions.SystemPrompt` survives `RagPipeline.AskAsync` | gating |
| **Real model** *(new)* | the prompt reaches the provider **and changes output** | nightly `RequiresLlm` |

Today only the first exists. A user configures `RagOptions` and calls `RagPipeline.AskAsync` —
nothing tests that path, so a future change swallowing the prompt would pass CI.

## 2. The streaming path

`AskStreamingAsync` builds messages through the same helper, but nothing asserts the prompt reaches
the client there. **Streaming is where users are most likely to hit this and least likely to have a
debugger attached**, so it is covered to the same standard as the non-streaming path.

## 3. The ordering, pinned — and an existing test that passes for the wrong reason

With a `ConversationHistory` beginning with a system message, the caller's `SystemPrompt` is message
**`[1]`, not `[0]`** — deliberately, so a host-injected prompt-hardening prefix is not shadowed by a
per-request prompt.

**Decided 2026-08-07: pin this behaviour, do not change it.** It is a defensible security-first
choice, and a testing phase is the wrong place to revisit it.

Two consequences:

- The existing test asserts `msgs[0].Text == "Custom prompt"`, which **holds only because its
  fixture has no history**. It passes for the wrong reason. It should assert by role and content,
  not by position.
- A test must cover the with-history case explicitly, so the ordering is locked rather than
  incidental.

## 4. Documentation

- **The `[Source N]` context format.** It shapes model output — it is why the reporter saw
  citations — and is invisible from outside. Documented, with the explicit note that a custom
  system prompt does not suppress citation behaviour.
- **`IPromptObserver`.** The seam that receives the complete message list immediately before it is
  sent — the diagnostic that would have let the reporter answer this himself in one run. It exists
  and is **undocumented**. Adding it turns a support round-trip into self-service.

## 5. The real-model test, and why it uses a marker

`tests/Rag.NET.Testing/TestChatClientFactory` already returns an **OpenRouter**-backed `IChatClient`
when `OPENROUTER_API_KEY` is set, falling back to the Ollama fixture otherwise. No new
infrastructure is needed: the test runs against OpenRouter locally and Ollama in the nightly.

The system prompt under test carries an **unambiguous marker instruction** — *"end every answer with
`<<RAGNET>>`"* — and the test asserts the marker appears.

**It deliberately does not assert the reporter's own case.** Their prompt requested an exact
sentence and the model paraphrased; asserting exact text would make the test flaky for precisely the
reason the issue exists. A marker instruction is followed reliably across models and still proves
the whole chain: options → pipeline → engine → provider → output.

## 6. Out of scope

- **Changing the message ordering** — decided in §3.
- **`Temperature` and newer models.** Several recent OpenAI reasoning models reject or ignore
  `temperature`; the reporter was asked to check it. If real, that is a separate finding needing its
  own evidence.
- **Changing the `[Source N]` format.** Documenting it is this phase; whether it is the right format
  is a retrieval-quality question.
- **Suppressing citations by default.** The reporter may want it, but changing default output
  shape needs its own decision.
