# LLM-as-Judge Evaluation Design

**Date:** 2026-03-20
**Package:** `Rag.NET.Evaluation`
**Status:** Approved

---

## Goal

Add an `LlmJudgeEvaluator` to `Rag.NET.Evaluation` that uses an `IChatClient` to score RAG answers against named criteria (correctness, faithfulness, relevance). One LLM call per sample; results carry per-criterion scores and reasoning text.

## Motivation

`EmbeddingDistanceEvaluator` gives a single blunt signal — semantic similarity between predicted and reference answer. It cannot detect hallucinations, off-topic answers, or factual errors phrased similarly to the reference. LLM-as-judge closes this gap: it evaluates whether answers are *actually correct*, *faithful to context*, and *relevant to the question* — with a reasoning string that explains each verdict.

---

## Architecture

All new types live in `Rag.NET.Evaluation`. No new package.

```
EvaluationSample (extended)
    ↓
LlmJudgeEvaluator.EvaluateAsync()
    ↓  (one IChatClient call per sample, all in parallel)
JSON response per sample
    ↓
LlmJudgeResult
  └── IReadOnlyList<SampleJudgement>
        └── IReadOnlyDictionary<string, CriterionScore>
              └── (Score: double, Reasoning: string)
```

---

## Types

### `JudgeCriterion`

```csharp
public sealed record JudgeCriterion(string Name, string Description)
{
    public static readonly JudgeCriterion Correctness = new("correctness",
        "Is the predicted answer factually correct given the reference answer?");
    public static readonly JudgeCriterion Faithfulness = new("faithfulness",
        "Does the predicted answer stay grounded in the retrieved context without hallucinating facts not present in the context?");
    public static readonly JudgeCriterion Relevance = new("relevance",
        "Does the predicted answer directly and completely address the question?");
}
```

### `EvaluationSample` (updated)

```csharp
public sealed record EvaluationSample(
    string Question,
    string PredictedAnswer,
    string ReferenceAnswer,
    IReadOnlyList<string>? SourceChunks = null);  // new optional property
```

Backwards-compatible — existing callers without `SourceChunks` continue to work. When `SourceChunks` is null or empty, the faithfulness criterion is excluded from the prompt and result.

> **Note:** The implementation uses `IReadOnlyList<string>? SourceChunks` rather than `IReadOnlyList<SearchResult>? Sources` to keep `Rag.NET.Evaluation` free of a dependency on the core `Rag.NET` package. Callers extract `.Chunk.Text` themselves before constructing `EvaluationSample`.

### Result types

```csharp
public sealed record CriterionScore(double Score, string Reasoning);

public sealed record SampleJudgement(
    string Question,
    IReadOnlyDictionary<string, CriterionScore> Criteria);

public sealed record LlmJudgeResult(IReadOnlyList<SampleJudgement> Samples)
{
    // Mean score across all samples for the named criterion
    public double MeanScore(string criterion);

    // True if every sample's score for the criterion meets the threshold
    public bool AllPass(string criterion, double threshold);
}
```

### `LlmJudgeEvaluator`

```csharp
public sealed class LlmJudgeEvaluator(
    IChatClient chatClient,
    IReadOnlyList<JudgeCriterion>? criteria = null)  // defaults to Correctness + Faithfulness + Relevance
{
    public Task<LlmJudgeResult> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default);
}
```

Does **not** implement `IRagEvaluator` — the result type is genuinely different and forcing it would mean null-checking optional fields. The CI gate pattern is covered by `LlmJudgeResult.MeanScore()` and `AllPass()`.

---

## LLM Call Design

**Parallelism:** All samples evaluated concurrently via `Task.WhenAll`. No artificial concurrency limit — the `IChatClient` implementation handles its own throttling.

**System message** (static per evaluator instance):
```
You are an expert evaluator of RAG system outputs.
Score the predicted answer against each criterion on a scale of 0.0–1.0.
Respond with valid JSON only — no markdown, no explanation outside the JSON.
```

**User message** (dynamic per sample):
```
Question: {question}
Predicted Answer: {predictedAnswer}
Reference Answer: {referenceAnswer}
[Retrieved Context:
  [1] {sources[0].Chunk.Text}
  [2] {sources[1].Chunk.Text}
  ...]                          ← only included when Sources != null

Evaluate against these criteria:
- correctness: {criterion.Description}
- relevance:   {criterion.Description}
[- faithfulness: {criterion.Description}]   ← only included when Sources != null

Respond with this exact JSON shape:
{
  "correctness": { "score": 0.85, "reasoning": "..." },
  "relevance":   { "score": 1.00, "reasoning": "..." }
}
```

**JSON parsing:**
1. Deserialise response text via `System.Text.Json` into internal DTO records.
2. If deserialisation fails, strip markdown code fences (` ```json ... ``` `) and retry once.
3. If still failing, throw `LlmJudgeException(string message, string rawResponse)`.
4. Scores are clamped to `[0.0, 1.0]` after parsing.

---

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| Empty `samples` list | `ArgumentException` |
| Malformed JSON (after fence-strip retry) | `LlmJudgeException` with `RawResponse` property |
| Missing criterion key in JSON | `LlmJudgeException` with `RawResponse` property |
| `Sources` is null | Faithfulness excluded from prompt and result; no error |
| Cancellation | Propagates through `Task.WhenAll` |

---

## Testing

All tests in `Rag.NET.Evaluation.Tests` using a mock `IChatClient`. No live LLM calls.

| Test | What it verifies |
|------|-----------------|
| All three criteria returned when sources provided | Happy path, full mapping |
| Faithfulness absent when `Sources` is null | Criterion skipping |
| `MeanScore("correctness")` correct | Convenience method |
| `AllPass("relevance", 0.7)` correct | Convenience method |
| Markdown-fenced JSON parsed successfully | Fence-strip fallback |
| Malformed JSON throws `LlmJudgeException` with `RawResponse` | Error path |
| Empty sample list throws `ArgumentException` | Guard |
| Scores outside `[0, 1]` are clamped | Defensive clamping |

---

## What is NOT in scope

- Streaming evaluation (LLM judge requires the full JSON before parsing)
- Caching of judge results
- Custom prompt templates (description on `JudgeCriterion` is the extension point)
- Parallel concurrency limiting (delegated to the `IChatClient` implementation)
