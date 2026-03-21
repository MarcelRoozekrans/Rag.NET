# Design: Map-Reduce and Refine Answer Synthesis

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

Two complementary answer synthesis strategies that handle context-window overflow — the gap in the current `ChatAnswerEngine`, which stuffs all retrieved chunks into a single LLM context:

- **Map-Reduce** — parallel LLM call per chunk (map), then one combining call (reduce). Best for large independent chunk sets where parallelism matters.
- **Refine** — sequential: generate an initial answer from the first chunk, then iteratively refine it with each subsequent chunk. Best for sequential coherence tasks with lower parallelism tolerance.

Both are activated per-call via `RagOptions.SynthesisStrategy`. Default behaviour is unchanged.

---

## Architecture

### `SynthesisStrategy` enum

```csharp
public enum SynthesisStrategy { Default, MapReduce, Refine }
```

Added to `src/Rag.NET/Models/Options/SynthesisStrategy.cs`.

### `RagOptions`

```csharp
public SynthesisStrategy SynthesisStrategy { get; set; } = SynthesisStrategy.Default;
public MapReduceOptions? MapReduceOptions { get; set; }
public RefineOptions? RefineOptions { get; set; }
```

### `DispatchingAnswerEngine`

Replaces `ChatAnswerEngine` as the registered `IAnswerEngine`. Reads `RagOptions.SynthesisStrategy` and delegates to the appropriate engine:

```
DispatchingAnswerEngine
  ├── Default   → ChatAnswerEngine      (existing, unchanged logic)
  ├── MapReduce → MapReduceAnswerEngine (new)
  └── Refine    → RefineAnswerEngine    (new)
```

All three engines are injected into `DispatchingAnswerEngine` via constructor. `ChatAnswerEngine` remains in the codebase unchanged — it is now an internal implementation detail rather than the registered service.

### File layout

```
src/Rag.NET/AnswerGeneration/
  ChatAnswerEngine.cs              (existing — no changes)
  DispatchingAnswerEngine.cs       (new)
  MapReduceAnswerEngine.cs         (new)
  RefineAnswerEngine.cs            (new)
src/Rag.NET/Models/Options/
  SynthesisStrategy.cs             (new)
  MapReduceOptions.cs              (new)
  RefineOptions.cs                 (new)
  RagOptions.cs                    (modified)
```

---

## Map-Reduce Strategy

### Map step

One LLM call per source chunk, executed in parallel up to `MapReduceOptions.MapConcurrency` (default 5).

Default map prompt:
```
Using only the following text, answer this question as best you can.
If the text doesn't contain relevant information, say "not found".

Text:
{chunk}

Question: {query}
```

Chunks whose map response is empty or contains only "not found" (case-insensitive trim) are filtered out before the reduce step.

### Reduce step

One LLM call combining surviving partial answers.

Default reduce prompt:
```
Synthesize the following partial answers into a single coherent response.
Discard redundant or contradictory information.

Partial answers:
{partials}

Question: {query}
```

If all map calls produce "not found", reduce receives an empty partials list and returns a graceful "no relevant information found" response.

### `MapReduceOptions`

```csharp
public sealed class MapReduceOptions
{
    public int MapConcurrency { get; init; } = 5;
    public string? MapPromptTemplate { get; init; }    // null = default above
    public string? ReducePromptTemplate { get; init; } // null = default above
}
```

---

## Refine Strategy

Processes chunks sequentially. The first chunk produces an initial answer; each subsequent chunk optionally refines it.

Default initial prompt:
```
Answer this question using only the following context.

Context:
{chunk}

Question: {query}
```

Default refine prompt:
```
Given the existing answer below and new context, refine the answer if the new
context adds useful information. If it adds nothing new, return the existing
answer unchanged.

Existing answer: {answer}

New context:
{chunk}

Question: {query}
```

### `RefineOptions`

```csharp
public sealed class RefineOptions
{
    public string? InitialPromptTemplate { get; init; }
    public string? RefinePromptTemplate { get; init; }
}
```

---

## Shared behaviour

Both strategies:
- Propagate `SystemPrompt`, `Temperature`, and `ConversationHistory` from `RagOptions` to every LLM call.
- Implement `AskStreamingAsync` by calling `AskAsync` internally and yielding two updates: sources first, then a single text delta. This is honest about the non-streaming nature of both strategies.

---

## Error Handling

### Map-Reduce

- Map call throws (non-cancellation): chunk treated as "not found", Warning logged, remaining chunks continue.
- All map calls fail: reduce called with empty partials, returns graceful fallback.
- Reduce call throws: exception propagates to caller.
- `OperationCanceledException`: always re-thrown immediately.

### Refine

- Refinement call throws (non-cancellation): running answer from previous step preserved, Warning logged, loop continues with next chunk.
- Initial call throws: exception propagates to caller.
- `OperationCanceledException`: always re-thrown immediately.

---

## DI Registration

`DispatchingAnswerEngine` is registered in `ServiceCollectionExtensions` replacing the direct `ChatAnswerEngine` registration. No new extension methods are needed — `SynthesisStrategy` is a per-call `RagOptions` field.

---

## Testing

| Scenario | Strategy | Expected |
|----------|----------|----------|
| 3 sources, all relevant | MapReduce | 3 map calls + 1 reduce; answer from reduce |
| 1 source returns "not found" | MapReduce | 2 partials fed to reduce |
| All sources return "not found" | MapReduce | Reduce with empty partials, graceful response |
| Map call throws | MapReduce | Chunk skipped, Warning logged, others proceed |
| 3 sources sequential | Refine | Initial + 2 refinement calls; final answer returned |
| Refinement call throws | Refine | Previous answer preserved, Warning logged |
| `SynthesisStrategy.Default` | Dispatching | Delegates to `ChatAnswerEngine` unchanged |
| `AskStreamingAsync` with MapReduce | MapReduce | Sources yielded first, then single text delta |
| `AskStreamingAsync` with Refine | Refine | Sources yielded first, then single text delta |

---

## Out of Scope

- True streaming for Map-Reduce or Refine (both strategies require buffering by design)
- Token-count-based automatic strategy selection
- Per-strategy `SystemPrompt` override (global `RagOptions.SystemPrompt` applies to all LLM calls)
