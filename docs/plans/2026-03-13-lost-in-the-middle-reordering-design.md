# Lost-in-the-Middle Reordering Design

**Goal:** Improve answer quality at zero cost by reordering retrieved results so the most relevant chunks appear at the start and end of the context window, exploiting the known LLM attention bias described in Liu et al. (2023).

**Approach:** Opt-in flag on `RetrievalOptions` and `RagOptions`. Applied inside `RetrieveAsync` after the vector store returns results. Pure in-memory transformation — no I/O, no new packages.

---

## Section 1: Architecture

A static `LostInTheMiddleReorderer` class with a single `Reorder` method takes `IReadOnlyList<SearchResult>` (already sorted by descending score) and redistributes: position 0 gets the best result, last position gets the second-best, remaining slots fill from the outside in with the rest in descending score order.

Two new bool properties — `RetrievalOptions.UseLostInTheMiddleReordering` and `RagOptions.UseLostInTheMiddleReordering` — default to `false`. When set, `RagPipeline.RetrieveAsync` calls `LostInTheMiddleReorderer.Reorder()` before returning.

---

## Section 2: Components

**New files:**
- `src/Rag.NET/PostRetrieval/LostInTheMiddleReorderer.cs` — static class, `Reorder(IReadOnlyList<SearchResult>)` returns `IReadOnlyList<SearchResult>`

**Modified files:**
- `src/Rag.NET/Models/Options/RetrievalOptions.cs` — add `bool UseLostInTheMiddleReordering { get; set; }`
- `src/Rag.NET/Models/Options/RagOptions.cs` — add `bool UseLostInTheMiddleReordering { get; set; }`
- `src/Rag.NET/Pipeline/RagPipeline.cs` — apply reordering in `RetrieveAsync` after search returns

**Test file:**
- `tests/Rag.NET.Tests/PostRetrieval/LostInTheMiddleReordererTests.cs`

---

## Section 3: Data Flow

**`RetrieveAsync` with flag set:**
1. Embed query → search vector store → get `IReadOnlyList<SearchResult>` sorted descending by score
2. If `opts.UseLostInTheMiddleReordering == true`, pass list to `LostInTheMiddleReorderer.Reorder()`
3. Return reordered list

**Reorder algorithm** (input list assumed sorted best-first):
```
result[0]   = input[0]   (best)
result[n-1] = input[1]   (second-best, at end)
result[1]   = input[2]
result[n-2] = input[3]
...
```
Fill from both ends toward middle. For odd-count lists the middle slot gets the weakest result.

---

## Section 4: Error Handling

Pure transformation — no failure modes. Edge cases handled:
- 0 results: return empty list as-is
- 1 result: return as-is
- 2 results: best at 0, second-best at 1 (no change, already optimal)

---

## Section 5: Testing

Unit tests on `LostInTheMiddleReorderer.Reorder()` directly — no pipeline needed:

- 0 results → empty list
- 1 result → same single result
- 2 results → [best, second] (unchanged)
- 3 results → [best, third, second]
- 4 results → [best, third, fourth, second]
- 5 results → [best, third, fifth, fourth, second]

Integration: pass flag via `RetrievalOptions` through `RagPipeline.RetrieveAsync`, assert returned order matches expected reordering.
