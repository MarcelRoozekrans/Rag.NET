# Evaluation Dataset Builder — Verify, Test, Document (Phase 3.2) — Design

**Date:** 2026-07-28
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.2
**Covers:** the `Evaluation Dataset Builder` row in `features.md`

## The same shape as Phase 3.1

`EvaluationDatasetBuilder` shipped on 2026-04-11, marked `✅ Done` in its `features.md` detail
section with an unchecked summary-matrix row. It has tests and it has a guide section. As in
3.1, that is the problem rather than the reassurance.

`tests/Rag.NET.Tests/Evaluation/EvaluationDatasetBuilderTests.cs:87-101`:

```csharp
public async Task BuildAsync_WhenLlmReturnsEmptyText_HandlesGracefully()
    Assert.Single(samples);
    Assert.Equal(string.Empty, samples[0].Question);
```

That is the third test written that day whose name promises grace and whose body certifies a
defect — after `ScoreAsync_MalformedClaimsJson_ReturnsOneGracefully` and
`ScoreAsync_MalformedStatementsJson_ReturnsOneGracefully`, both fixed in 3.1. In all three,
*gracefully* means **the failure was swallowed and something that looks fine was produced**.

A sample with an empty question is not a sample. It enters the dataset as valid, and every
evaluator downstream then scores it — Answer Relevance embeds `""` and returns a cosine
similarity like any other. The corruption is invisible at every subsequent step.

## 1. What the audit found

### The dataset cannot be regenerated

`EvaluationDatasetBuilder.cs:46`:

```csharp
var sampled = allChunks.OrderBy(_ => Random.Shared.Next()).Take(sampleCount).ToList();
```

No seed. Build a dataset, measure a pipeline, change the chunking, rebuild, measure again — and
the two runs used **different questions**, so the delta measures the question set as much as the
change. For a tool whose only output is a measurement baseline, that is the defect that matters
most: it silently invalidates the before/after comparison the tool exists to support.

It belongs to the same family as the BEIR chunk-versus-document trap recorded for Phase 3.7 —
numbers that look comparable and are not.

`OrderBy` over a random key is also an O(n log n) sort of the entire corpus to take `k` items,
and is very slightly biased when two `Int32` keys collide.

### An empty generation becomes a sample

`GenerateQuestionAsync` returns `string.Empty` when the model replies with nothing
(`:82`), and `GenerateSampleAsync` wraps it in an `EvaluationSample` regardless (`:65-69`). In
`QuestionAndAnswer` mode an empty *answer* is equally admitted, which additionally makes the
sample unusable with Context Precision and Context Recall — both of which throw on an empty
`ReferenceAnswer`.

### The whole corpus is materialised to sample from it

`:36-42` calls `GetDocumentsAsync`, then `GetChunksAsync` per document, accumulating **every
chunk of every document** into one list in order to pick `SampleCount` of them. Sampling five
chunks from a 100k-document corpus reads and holds all of it.

### Unbounded concurrency

`:49-50` fans out one task per sampled chunk through `Task.WhenAll` with no ceiling. In
`QuestionAndAnswer` mode each task makes two sequential LLM calls, so `SampleCount = 500` is up
to 500 concurrent chains. This is the identical defect 3.1 removed from the RAGAS metrics.

### No cost recording

The RAGAS metrics record chat and embedding spend as of 3.1. The builder — which is pure LLM
spend, one or two calls per sample — records nothing.

## 2. Scope decisions (agreed)

1. **A failed generation is not a sample.** Exclude it and report the exclusion.
2. **Seeded, reproducible sampling.**
3. **Reservoir sampling**, so memory is proportional to the sample rather than the corpus.
4. **Bounded concurrency and cost recording**, matching what 3.1 gave the metrics.
5. **The shared plumbing moves down rather than being copied** (§3).

## 3. Move the shared core down; do not copy it

Bounded concurrency and cost recording already exist, in `RagasJudge`. They are unreachable from
here: `RagasJudge` lives in `Rag.NET.Evaluation.Ragas`, which *references*
`Rag.NET.Evaluation`, so the dependency points the wrong way.

Copying it would recreate 3.1's central failure exactly. The parse defect there existed twice —
once in Faithfulness and once in Context Recall — **because the plumbing had been copied**, and
the whole structural fix was to give it one home. Making a second copy now, in the same
milestone, for the same reason, would be a poor lesson to have learned.

So the throttle-and-cost core moves **down** into `Rag.NET.Evaluation` as an internal
`EvaluationChatCaller`: one shared `SemaphoreSlim`, one chat call, one `CostKind.Chat` entry
written only when the provider reports **both** token counters. `RagasJudge` composes it and
keeps what is genuinely RAGAS-specific — verdict parsing, JSON extraction, fence stripping. The
builder composes it for question and answer generation.

The call options follow: an `EvaluationCallOptions` base carrying `MaxConcurrentCalls` and the
token prices, which `RagasOptions` and `EvaluationDatasetBuilderOptions` both extend. Property
names are unchanged, so this is source-compatible for RAGAS callers.

This touches code merged hours ago, which is the argument against it. The argument for it is that
151 tests already stand behind that code, so the refactor is verifiable in a way it will never be
cheaper to attempt.

## 4. Reproducibility, and its honest limits

`EvaluationDatasetBuilderOptions` gains `Seed`. Null keeps today's non-deterministic behaviour;
set, the same seed over the same corpus selects the same chunks.

**The limit must be documented rather than glossed.** A seed fixes the *sampling*, not the
result. Two things still vary:

- **The corpus.** Ingesting or deleting documents changes what is there to sample. Reproducibility
  holds for a fixed corpus, not across ingestion.
- **The model.** Question generation is an LLM call; at any temperature above zero the same chunk
  yields different questions. Seeding selects the same chunks, not the same text.

So the guarantee is precisely: *the same seed and the same corpus sample the same chunks.* That
is enough to make a before/after comparison meaningful, and claiming more would be the kind of
overstatement this milestone exists to remove.

Reservoir sampling (Algorithm R) over the streamed chunks gives this without accumulating the
corpus, provided enumeration order is stable — which is a property of `IRagDataManager`, and is
stated as a condition rather than assumed.

**Corrected after the Parts A+B review:** this section originally said "in O(k) memory", which
overstates it. `IRagDataManager` exposes no `IAsyncEnumerable` overload — `GetDocumentsAsync` and
`GetChunksAsync` both return fully materialised `IReadOnlyList<T>` — so a build's peak is *every
`DocumentSummary` in the corpus, plus the chunks of the largest single document, plus the sample*.
The win is still large: every chunk's **text** across the whole corpus is genuinely no longer held,
which for a 100k-document corpus is nearly all of the old footprint. O(k) would need a streaming
overload on the data manager, which is an `Rag.NET.Abstractions` change and out of scope for this
phase.

## 5. A failed generation is excluded and counted

`BuildAsync` returns an `EvaluationDataset` rather than a bare list: the samples, plus how many
were requested and how many were dropped and why. A caller who asks for 50 and receives 47 can
see that three generations came back empty, instead of quietly evaluating against a smaller set —
or worse, against three corrupt ones.

This mirrors `RagasReport.UnscoreableSamples` from 3.1, and the same principle drives it: the
failure is surfaced where it happened rather than folded into a number that looks fine.

In `QuestionAndAnswer` mode an empty reference answer drops the sample too. Emitting it would
produce a sample that Context Precision and Context Recall reject at evaluation time, which moves
the error to somewhere it cannot be explained.

**Deliberately not doing:** retrying a failed generation. It is speculative, it doubles the cost
model, and nobody has asked for it.

## 6. Testing

- **Reproducibility**: the same seed over the same corpus selects the same chunks; different
  seeds generally select different ones. Pinned with a deterministic fake, not a live model.
- **Exclusion**: an empty question drops the sample and increments the count — replacing
  `BuildAsync_WhenLlmReturnsEmptyText_HandlesGracefully`, which currently asserts the opposite.
  The old assertion is re-pointed with a comment recording what it used to claim.
- **Reservoir sampling**: selects `k` uniformly, and does not materialise the corpus. The second
  half is checked by counting `GetChunksAsync` calls and asserting nothing accumulates.
- **The ceiling**: peak observed concurrency, not a total call count — a total proves nothing
  about whether a ceiling held. The same harness 3.1 built for the suite applies here.
- **Cost**: an entry per call when the provider reports both counters; nothing when it does not.

## 7. Documentation

`docs/guide/evaluation.md`'s dataset-builder section: the reproducibility guarantee and its two
limits, the exclusion behaviour and how to read the dropped count, `MaxConcurrentCalls`, and cost
recording. `features.md` gets the matrix row ticked and the Status prose corrected.

Scores and datasets built before this phase are not reproducible and may contain empty-question
samples. Say so.

## Out of scope

- Retrying failed generations (§5).
- Filtering generated questions for answerability, which the RAGAS papers do as a separate step.
- Public benchmark datasets — that is Phase 3.7, and a different feature: this builder synthesises
  questions from *your* corpus with no ground truth to check them against, which is why it can
  show that a change moved a number but never that the number is right.
