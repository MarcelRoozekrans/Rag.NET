# Design: Conversational Memory Management

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

Automatic conversation history management for multi-turn RAG. Three composable strategies applied in order: sliding window (cap exchanges), token-budget truncation (fit within limit), and summary memory (LLM-compress trimmed messages). Integrates into existing answer engines via a new `IConversationMemory` abstraction. Stateless — the caller persists history as they do today; the library only transforms what it receives.

---

## Architecture

### `IConversationMemory`

```csharp
public interface IConversationMemory
{
    Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
```

Called inside answer engines before building the LLM message list. When not registered, answer engines behave exactly as today — zero overhead, no breaking change.

### `ConversationMemoryOptions`

```csharp
public sealed class ConversationMemoryOptions
{
    public int? MaxExchanges { get; init; }
    public int? MaxTokens { get; init; }
    public bool UseSummary { get; init; } = false;
    public string? SummaryPromptTemplate { get; init; }
}
```

- `MaxExchanges`: maximum user/assistant exchange pairs to keep. Oldest removed first. Null = no window limit. Applied first.
- `MaxTokens`: maximum token budget for history. Uses `cl100k_base` tokenizer. Oldest non-system messages trimmed until within budget. Null = no token limit. Applied second.
- `UseSummary`: when true, trimmed messages are LLM-summarized into a system message prefix instead of discarded. Requires `IChatClient` in DI. Applied last.
- `SummaryPromptTemplate`: custom prompt for the summary call. Default asks for a concise summary of the conversation so far.

### `ConversationMemoryPipeline`

Implements `IConversationMemory`. Runs the three strategies in order:

1. **Sliding window** — count user/assistant pairs from the end, trim anything beyond `MaxExchanges`. System messages are always preserved (never counted, never removed).
2. **Token budget** — tokenize remaining messages with `cl100k_base` (`Microsoft.ML.Tokenizers`), remove oldest non-system messages one by one until total tokens ≤ `MaxTokens`.
3. **Summary** — if `UseSummary = true` and messages were trimmed in steps 1 or 2, pass the trimmed messages to the LLM with a summary prompt. Prepend the result as a system message: `"Summary of earlier conversation: {summary}"`.

### Answer Engine Integration

In `BuildMessages` (shared by `ChatAnswerEngine`, `MapReduceAnswerEngine`, `RefineAnswerEngine`), before adding conversation history:

```csharp
var history = opts.ConversationHistory;
if (memory is not null && history is { Count: > 0 })
    history = await memory.ProcessAsync(history, ct);
```

`IConversationMemory` is injected as a nullable dependency — null when not registered.

### Registration

```csharp
rag.UseConversationMemory(o =>
{
    o.MaxExchanges = 10;
    o.MaxTokens = 4000;
    o.UseSummary = true;
});
```

When nothing is configured, `IConversationMemory` is not registered. Answer engines skip the processing step entirely.

### File Layout

```
src/Rag.NET/Abstractions/IConversationMemory.cs
src/Rag.NET/Memory/ConversationMemoryPipeline.cs
src/Rag.NET/Models/Options/ConversationMemoryOptions.cs
src/Rag.NET/DependencyInjection/RagBuilder.cs  (add UseConversationMemory method)
tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs
```

---

## Error Handling

- **Empty history:** returned unchanged, no processing.
- **`UseSummary = true` but LLM call fails:** log warning, return trimmed history without summary (graceful degradation).
- **`OperationCanceledException`:** re-thrown immediately.
- **`MaxExchanges` and `MaxTokens` both null, `UseSummary = false`:** pass-through, no work done.

---

## Testing

| Scenario | Expected |
|---|---|
| `MaxExchanges = 2`, 5 exchanges in | Last 2 exchanges kept, first 3 removed |
| System messages | Always preserved, never counted as exchanges |
| `MaxTokens = 100`, history exceeds | Oldest non-system messages trimmed until within budget |
| `UseSummary = true` + trimmed messages | Summary system message prepended |
| `UseSummary = true` + LLM fails | Warning logged, trimmed history returned without summary |
| All three strategies combined | Window → budget → summary, applied in order |
| No strategies configured | History returned unchanged |
| Empty history | Returned unchanged, no LLM calls |
| `OperationCanceledException` | Propagates immediately |
| Custom `SummaryPromptTemplate` | Used instead of default prompt |

---

## Out of Scope

- Persistent memory store (cross-session recall via vector store — separate future feature)
- Per-message importance scoring
- Automatic conversation topic detection
- Speaker/role-aware summarization
