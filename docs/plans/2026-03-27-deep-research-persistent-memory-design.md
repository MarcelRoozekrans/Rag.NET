# Deep Research Loop + Persistent Conversational Memory — Design

**Date:** 2026-03-27
**Status:** Approved

---

## Goal

Two composable enhancements to the retrieval and memory layers:

1. **Deep Research Loop** — `IRetriever` decorator that iteratively refines retrieval using LLM-judged sufficiency and sub-query decomposition.
2. **Persistent Conversational Memory** — `IConversationMemory` decorator that stores and recalls individual exchange pairs across sessions via vector search.

---

## Deep Research Loop

### Overview

`DeepResearchRetriever` wraps any `IRetriever`. On each `RetrieveAsync` call it runs a sufficiency-gated loop: retrieve, ask the LLM whether the result is sufficient, and if not generate focused sub-queries and retrieve again. Results are merged and deduplicated across all iterations.

### Algorithm

```
RetrieveAsync(query, options):
  chunks = inner.RetrieveAsync(query, options)   // depth 0
  depth = 0

  loop:
    llmResponse = LLM("Is this sufficient? If not, give sub-queries.", query, chunks)
    if llmResponse.sufficient OR depth >= MaxDepth:
      return deduplicate(chunks)
    for each subQuery in llmResponse.subQueries:
      chunks += inner.RetrieveAsync(subQuery, options)
    chunks = deduplicate(chunks)
    depth++
```

LLM response is a small structured JSON:
```json
{ "sufficient": false, "subQueries": ["sub-query 1", "sub-query 2"] }
```

Deduplication: by `DocumentId + ChunkIndex`.

### Options

```csharp
public sealed class DeepResearchOptions
{
    public int MaxDepth { get; init; } = 3;
    public int SubQueryCount { get; init; } = 3;
    public string? SufficiencyPrompt { get; init; }  // null = built-in default
}
```

### DI Registration

```csharp
services.AddRagNet(rag => rag
    .UseDeepResearch()                               // default options
    .UseDeepResearch(new DeepResearchOptions { MaxDepth = 2 }));  // custom
```

`UseDeepResearch` registers `DeepResearchRetriever` as a decorator over the existing `IRetriever` singleton. Requires `IChatClient` to be registered; throws `InvalidOperationException` at container build if absent.

### Error handling

- `IChatClient` missing → `InvalidOperationException` at registration
- Sub-query retrieval failure → logged, skipped; partial results returned
- LLM returns malformed JSON → treated as "sufficient = true" (fail-safe passthrough)

---

## Persistent Conversational Memory

### Overview

`PersistentConversationMemory` wraps `IConversationMemory`. On each `ProcessAsync` call it retrieves relevant past exchange pairs from the vector store (by similarity to the current query) and injects them as a system-message prefix before delegating to the inner pipeline. After the response, the caller calls `StoreAsync` to persist the new exchange.

### Storage format

Each exchange pair is stored as a `TextChunk`:
- `Text = "User: {userMessage}\nAssistant: {assistantMessage}"`
- `DocumentId = sessionId`
- `ChunkIndex = sequential index within the session`

### Flow

```
ProcessAsync(history, query):
  matches = vectorStore.SearchAsync(query, TopK, MinScore)
  if matches.Any():
    prepend system message: "From a previous conversation:\n{matches}"
  return inner.ProcessAsync(history)

StoreAsync(userMessage, assistantMessage, sessionId):
  embed exchange pair text
  vectorStore.AddAsync(chunk)
```

`StoreAsync` is a new method added to `IConversationMemory`.

### Options

```csharp
public sealed class PersistentMemoryOptions
{
    public int TopK { get; init; } = 3;
    public float MinScore { get; init; } = 0.7f;
}
```

### DI Registration

Nested under `UseConversationMemory` via a `ConversationMemoryBuilder` action delegate — structural dependency, cannot be called without an inner pipeline:

```csharp
services.AddRagNet(rag => rag
    .UseConversationMemory(
        options: new ConversationMemoryOptions { MaxExchanges = 20 },
        configure: mem => mem.UsePersistentMemory(new PersistentMemoryOptions { TopK = 3 })));
```

`UseConversationMemory` gains an optional `Action<ConversationMemoryBuilder>? configure` parameter. `ConversationMemoryBuilder` exposes `UsePersistentMemory()`.

### Error handling

- Vector store missing → `InvalidOperationException` at registration
- Retrieval returns no results → history unchanged, no error
- `StoreAsync` embedding failure → logged, exchange not persisted (non-fatal)

---

## Files

**New:**
- `src/Rag.NET/Retrieval/DeepResearchRetriever.cs`
- `src/Rag.NET/Models/Options/DeepResearchOptions.cs`
- `src/Rag.NET/Memory/PersistentConversationMemory.cs`
- `src/Rag.NET/Models/Options/PersistentMemoryOptions.cs`
- `src/Rag.NET/DependencyInjection/ConversationMemoryBuilder.cs`

**Modified:**
- `src/Rag.NET/Abstractions/IConversationMemory.cs` — add `StoreAsync` method
- `src/Rag.NET/Memory/ConversationMemoryPipeline.cs` — implement `StoreAsync` (no-op on inner pipeline)
- `src/Rag.NET/DependencyInjection/RagBuilder.cs` — add `UseDeepResearch`, update `UseConversationMemory` signature

**New tests:**
- `tests/Rag.NET.Tests/Retrieval/DeepResearchRetrieverTests.cs`
- `tests/Rag.NET.Tests/Memory/PersistentConversationMemoryTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UseDeepResearchTests.cs`
- `tests/Rag.NET.Tests/DependencyInjection/UsePersistentMemoryTests.cs`

---

## Testing Plan

### `DeepResearchRetriever`
1. LLM signals sufficient on first pass → returns chunks, no sub-queries generated
2. LLM signals insufficient → sub-queries generated → results merged → signals sufficient
3. Max depth hit before sufficient → stops, returns accumulated chunks
4. Duplicate chunks across sub-queries → deduplicated in output
5. Sub-query retrieval throws → exception logged, skipped, other results returned
6. LLM returns malformed JSON → treated as sufficient (passthrough)

### `PersistentConversationMemory`
7. Vector store returns past matches → injected as system prefix before inner pipeline
8. Vector store returns empty → history unchanged, inner pipeline called normally
9. `StoreAsync` embeds and stores exchange with correct `sessionId` and text format
10. `MinScore` threshold — results below threshold filtered out before injection

### `RagBuilder` DI
11. `UseConversationMemory(configure: mem => mem.UsePersistentMemory())` → `PersistentConversationMemory` wraps inner pipeline, both resolvable
12. `UseDeepResearch` → `DeepResearchRetriever` wraps `IRetriever`, resolvable
