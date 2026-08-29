# FLARE's fragment protocol, and the harness guard that was disarmed

**Date:** 2026-08-29
**Phase:** 6.2.1 — Retrieval & Answer Sweep (the answer-engine thread)
**Status:** design, approved 2026-08-29

## What happened

The 10-query answer-engine pilot was re-run twice on 2026-08-29 to validate #418's
extraction-contract fix. **Both runs failed identically** — four `TaskCanceledException`s each, every
one inside `FlareAnswerEngine.GenerateSentenceAsync`, against OpenRouter. Neither run reached the
judging stage, so **the extraction-contract question #418 was meant to settle is still unanswered.**

The failure is deterministic, not flaky. A third run would fail the same way.

### The evidence

One cached response from the first run is **86,091 bytes** containing the contract sentence

> The answer to the question is "yes, there was a change in the nature of the events reported…"

repeated **256 times**, alternating with one companion sentence. Across the 47,151 answer-cache
entries written before today, the largest response ever recorded is **3,747 bytes**. Today's runs
produced one 23× larger than anything in the cache's history.

### The mechanism

Three things compose, and no one of them is sufficient alone.

1. **#418 gives every engine arm `MultiHopRagAnswerJudge.AnswerInstruction` as
   `RagOptions.SystemPrompt`.** That instruction is *terminal*: "End your reply with exactly this
   sentence, filling in the answer: …".

2. **`FlareAnswerEngine` replaces its own system prompt with the caller's** —
   `new(ChatRole.System, opts.SystemPrompt ?? DefaultSystemPrompt)` (`FlareAnswerEngine.cs:302`) —
   on **every per-sentence call**. FLARE generates one sentence at a time
   (`FlareAnswerEngine.cs:83-88`), feeding the growing answer back in as
   `"Answer so far:\n{partialAnswer}"` (`:296`), and its user message asks the model to
   *"Continue the answer with EXACTLY ONE additional sentence. If the answer is complete, reply with
   only: `<DONE>`"*.

   A terminal instruction and a continue-with-one-fragment instruction are in direct conflict. The
   model resolves it by emitting the closing sentence, which becomes "answer so far", and emitting it
   again — and it **never emits `<DONE>`**, so the loop also loses its early exit and runs the full
   `MaxSentences`.

3. **`CachedGraphRagClient` discards the caller's `ChatOptions`.** `GetResponseAsync` accepts an
   `options` argument and never reads it; `CallOnceAsync` sends `_options` — the client's own
   `new ChatOptions { Temperature = temperature }` (`CachedGraphRagClient.cs:78, :295`). So FLARE's
   `MaxOutputTokens = 150` never reaches the model.

   That cap exists precisely for this failure. Its comment says so: *"MaxOutputTokens bounds rambling
   models — only one sentence is kept per call, so anything beyond ~150 tokens is discarded output
   paid for nothing"* (`FlareAnswerEngine.cs:309-311`). **The guard written for this exact hazard is
   disarmed by the harness**, which is why a bad prompt became an 86 KB runaway instead of a
   150-token oddity, and then a client-timeout failure.

### What this is an instance of

**The third time in this phase that a fix caused the next defect.** 6.2.12 had #390's fix deadlock
Blazor (#396), whose fix hung on host singletons (#400). Now #418 — itself a fix for a defect the
pilot found — causes this. Each was found only by running the thing; the suite was green throughout.

The generalisable rule, and it is the sibling of #418's own: **#418's lesson was "when one arm is
exempted from a shared apparatus, check what the apparatus was doing for it". This one is "when an
instruction is moved into a shared apparatus, check what each consumer does with it."** A terminal
output contract is meaningful to an engine that emits one complete reply and actively harmful to one
that emits fragments.

## The three fixes

### Fix 1 — `FlareAnswerEngine` composes the caller's system prompt rather than being replaced by it

**This is a defect in a shipped package, not a harness problem.** Any FLARE user whose
`RagOptions.SystemPrompt` carries a formatting or terminal instruction can trigger unbounded
generation and a loop that never terminates early. `MaxOutputTokens` bounds the per-call damage in
the product (where it is honoured) but does not resolve the conflict, and nothing bounds the loop.

FLARE's fragment protocol is **mechanism, not style**: "one sentence", `<DONE>`, and "answer so far"
are how the engine works. The caller's `SystemPrompt` legitimately sets voice, persona and content
constraints. The fix is to compose the two with the fragment protocol authoritative, so a caller
instruction can never displace it.

This is the same shape as #333: a defect reachable at shipped defaults, in a published package, that
no test caught because no test drove the engine with a conflicting system prompt.

### Fix 2 — `CachedGraphRagClient` honours the caller's `ChatOptions`

Merge the caller's options over the client's baseline and send the result. The baseline's model and
temperature remain authoritative for cache identity; the caller's constraints are additive.

**Cache key.** The per-entry key becomes `(rendered messages, caller-option deviations)`. Deviations
are rendered canonically and **render empty when the caller passed no options**.

Every one of the 86,510 existing entries — 47,322 answers, 35,176 extractions, 4,012 reports — was
written by a call that passed no caller options, so an empty rendering is a *faithful* encoding of
what those entries are, not a compatibility accommodation. Existing entries keep their keys and
their meaning; **zero regeneration**.

FLARE's calls acquire new keys, which is correct: with `MaxOutputTokens` honoured they are
genuinely different requests than the ones cached under the old semantics.

#### Why this is not a compromise (verified, not assumed)

`GraphExtractionCache.ComputeKey` already hashes **two length-prefixed, NUL-terminated fields**:

```text
SHA256( len(identity) ‖ identity ‖ NUL ‖ len(prompt) ‖ prompt ‖ NUL )
```

`identity` is `openai/gpt-4o-mini@t0.0` — **model and temperature are already in every key.** The
constructor's own documentation states the guarantee: *"Hashed into every key, so two identities
never share an entry."*

A cache key needs exactly one property: injectivity over everything that can change the response.
Measured against that, the two candidate designs are **equally injective**. The "full key"
alternative would add model and temperature — which are already there — so it buys no additional
safety and costs roughly $9 and several hours to regenerate all 86,510 entries. Baseline-relative
adds the one input genuinely missing after Fix 2: the caller's options.

This also refutes the hazard that motivated considering the full key. A baseline-relative key does
**not** risk silently reusing entries after a baseline change: altering the temperature alters
`_modelIdentity`, which alters every key, and the cache partitions on it already.

#### Implementation constraint: omit the field, never append it empty

**Appending a zero-length third field still orphans the whole cache.** The buffer is sized
`(2 * (sizeof(int) + 1)) + identity.Length + promptBytes.Length`; a third field adds 5 bytes — an
`int32` length plus the NUL — even when the field is empty, changing the hash of *every* entry.

So the options field must be **omitted entirely** when the caller passed no options: two fields when
unconstrained, three when constrained.

The variable field count is safe, and for a reason this code already relies on: fields are
length-prefixed *and* NUL-terminated, so a buffer parses to exactly one field sequence and a
two-field buffer can never be byte-identical to a three-field one. `ComputeKey`'s existing remarks
reason about precisely this class of bug — *"identity `ab` with prompt `c` and identity `a` with
prompt `bc` hash the same bytes"*.

Done naively — appending an empty field — this fix would have invalidated 86,510 entries while
appearing to work. A test asserting that existing keys are unchanged is what catches it, and is why
it is in the definition of done.

### Fix 3 — the extraction contract applies to FLARE's completed answer, not to each fragment

Fixes 1 and 2 stop the runaway; they do not make FLARE meet the judge's contract, and that is the
question the pilot exists to answer. FLARE assembles its answer by joining sentences
(`FlareAnswerEngine.cs:114`) with no final model call, so there is no natural point at which a
terminal contract applies.

The harness's FLARE arms therefore make **one final formatting call** after the loop, applying
`AnswerInstruction` to the assembled answer. Every arm stays under one shared contract — what #418
established, and the thing that removes the format-versus-reasoning confound — while no fragment
call ever carries a terminal instruction.

Cost: one extra call per query per FLARE arm. ~18 in the pilot, ~5,112 in the full 2,556-query sweep
across both FLARE arms. Negligible against the sweep's $5–10.

**Rejected:** relying on FLARE's last generated sentence to carry the contract (unreliable — nothing
makes the final fragment terminal); and relaxing the judge for FLARE (reintroduces exactly the
confound #418 fixed).

## Cache hygiene

The 86 KB degenerate entry is on disk and would be replayed as a legitimate answer and scored by the
judge. It is deleted as part of this work. Contamination is otherwise narrow: of the 171 entries
written today, only two exceed the historical p90 of 1,025 bytes.

*(A suspicion worth recording as refuted: 169 of today's 171 entries are 77–79 bytes, which looked
like empty responses being cached. They are not — 636 entries of exactly those sizes exist across
the pre-existing cache. The size is a normal shape here.)*

## What is verified by reading, and what is not

- The mechanism is **confirmed by cached evidence** — the 256-fold repetition — plus the two source
  sites that produce it. It is not a hypothesis.
- **One inference is labelled as such:** the 86 KB response *completed* and was cached; the four
  timeouts are calls that never finished, so their content cannot be read. That they share the
  mechanism is inferred from their location (`GenerateSentenceAsync`, both runs) and from the shape
  of the one runaway that did survive. Strong, but an inference.

## Definition of done

- [ ] FLARE's fragment protocol survives a caller `SystemPrompt` that contains a terminal
      instruction, proven by a test that **fails against the current code**.
- [ ] `CachedGraphRagClient` sends the caller's options, proven by a test asserting the options
      reach the inner client.
- [ ] Existing cache entries resolve to their existing keys, proven by a test — this is what makes
      "zero regeneration" a checked claim rather than an assertion.
- [ ] The FLARE arms meet the judge's extraction contract in a re-run pilot.
- [ ] The 86 KB entry is deleted.
- [ ] `dotnet build Rag.NET.slnx` clean; RepoConventions green.

**Not in scope:** the full 2,556-query sweep. This restores the pilot; the sweep is a separate
decision with its own cost.
