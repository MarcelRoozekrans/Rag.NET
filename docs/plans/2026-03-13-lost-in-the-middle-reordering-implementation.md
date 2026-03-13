# Lost-in-the-Middle Reordering Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add opt-in post-retrieval reordering that places the highest-scoring chunks at the start and end of the context window, improving LLM answer quality at zero cost.

**Architecture:** Static `LostInTheMiddleReorderer` class under `src/Rag.NET/PostRetrieval/`. Opt-in via `bool UseLostInTheMiddleReordering` on `RetrievalOptions` and `RagOptions`. Applied inside `RagPipeline.RetrieveAsync` after the vector store returns results.

**Tech Stack:** No new packages. Pure C# list manipulation.

---

### Task 1: Create the Reorderer and Unit Tests

**Files:**
- Create: `src/Rag.NET/PostRetrieval/LostInTheMiddleReorderer.cs`
- Create: `tests/Rag.NET.Tests/PostRetrieval/LostInTheMiddleReordererTests.cs`

**Context:** `SearchResult` is a record with `TextChunk Chunk` and `double Score`. The input list arrives sorted descending by score (best first). The algorithm redistributes: best → position 0, second-best → last position, third → position 1, fourth → second-to-last, etc. filling from the outside in.

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/PostRetrieval/LostInTheMiddleReordererTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class LostInTheMiddleReordererTests
{
    private static SearchResult MakeResult(double score) => new()
    {
        Chunk = new TextChunk { Text = $"text-{score}", DocumentId = "doc-1", ChunkIndex = 0 },
        Score = score,
    };

    [Fact]
    public void Reorder_EmptyList_ReturnsEmpty()
    {
        var result = LostInTheMiddleReorderer.Reorder([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Reorder_SingleItem_ReturnsSame()
    {
        var items = new[] { MakeResult(1.0) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Single(result);
        Assert.Equal(1.0, result[0].Score);
    }

    [Fact]
    public void Reorder_TwoItems_ReturnsBestFirstSecondBestLast()
    {
        var items = new[] { MakeResult(0.9), MakeResult(0.8) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.8, result[1].Score);
    }

    [Fact]
    public void Reorder_ThreeItems_PlacesBestFirstSecondBestLast()
    {
        // Input: [0.9, 0.8, 0.7] → Output: [0.9, 0.7, 0.8]
        var items = new[] { MakeResult(0.9), MakeResult(0.8), MakeResult(0.7) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.7, result[1].Score);
        Assert.Equal(0.8, result[2].Score);
    }

    [Fact]
    public void Reorder_FourItems_FillsOutsideIn()
    {
        // Input: [0.9, 0.8, 0.7, 0.6] → Output: [0.9, 0.7, 0.6, 0.8]
        var items = new[] { MakeResult(0.9), MakeResult(0.8), MakeResult(0.7), MakeResult(0.6) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.7, result[1].Score);
        Assert.Equal(0.6, result[2].Score);
        Assert.Equal(0.8, result[3].Score);
    }

    [Fact]
    public void Reorder_FiveItems_FillsOutsideIn()
    {
        // Input: [0.9, 0.8, 0.7, 0.6, 0.5] → Output: [0.9, 0.7, 0.5, 0.6, 0.8]
        var items = new[] { MakeResult(0.9), MakeResult(0.8), MakeResult(0.7), MakeResult(0.6), MakeResult(0.5) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.7, result[1].Score);
        Assert.Equal(0.5, result[2].Score);
        Assert.Equal(0.6, result[3].Score);
        Assert.Equal(0.8, result[4].Score);
    }
}
```

**Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "FAIL|error|could not"
```

Expected: build error — `LostInTheMiddleReorderer` does not exist yet.

**Step 3: Create the implementation**

Create `src/Rag.NET/PostRetrieval/LostInTheMiddleReorderer.cs`:

```csharp
using Rag.NET.Models;

namespace Rag.NET.PostRetrieval;

public static class LostInTheMiddleReorderer
{
    /// <summary>
    /// Reorders results so the most relevant appear at the start and end of the list,
    /// with less relevant results in the middle. Exploits the "lost-in-the-middle" phenomenon
    /// (Liu et al. 2023) where LLMs attend less to content in the middle of long contexts.
    /// </summary>
    /// <param name="results">Results sorted by descending relevance score (best first).</param>
    public static IReadOnlyList<SearchResult> Reorder(IReadOnlyList<SearchResult> results)
    {
        if (results.Count <= 2)
        {
            return results;
        }

        var reordered = new SearchResult[results.Count];
        int left = 0;
        int right = results.Count - 1;

        for (int i = 0; i < results.Count; i++)
        {
            if (i % 2 == 0)
            {
                reordered[left++] = results[i];
            }
            else
            {
                reordered[right--] = results[i];
            }
        }

        return reordered;
    }
}
```

**Step 4: Build and run the tests**

```bash
dotnet build tests/Rag.NET.Tests --no-restore
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "PASS|FAIL|Passed|Failed"
```

Expected: all new tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/PostRetrieval/LostInTheMiddleReorderer.cs tests/Rag.NET.Tests/PostRetrieval/LostInTheMiddleReordererTests.cs
git commit -m "feat: add LostInTheMiddleReorderer post-retrieval step"
```

---

### Task 2: Wire Into RetrievalOptions, RagOptions, and RagPipeline

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Modify: `src/Rag.NET/Models/Options/RagOptions.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Add the flag to both options classes**

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add:
```csharp
public bool UseLostInTheMiddleReordering { get; set; }
```

In `src/Rag.NET/Models/Options/RagOptions.cs`, add:
```csharp
public bool UseLostInTheMiddleReordering { get; set; }
```

**Step 2: Write the failing pipeline integration test**

Add to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` (inside the class, before the closing brace):

```csharp
[Fact]
public async Task RetrieveAsync_WithLostInTheMiddle_ReordersResults()
{
    var embeddings = new GeneratedEmbeddings<Embedding<float>>(
        [new Embedding<float>(new float[] { 0.1f })]);
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(embeddings);

    var results = new List<SearchResult>
    {
        new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
        new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
        new() { Chunk = new TextChunk { Text = "c", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
    };
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(results);

    var retrieved = await _sut.RetrieveAsync(
        "query",
        new RetrievalOptions { UseLostInTheMiddleReordering = true },
        TestContext.Current.CancellationToken);

    // [0.9, 0.8, 0.7] → [0.9, 0.7, 0.8]
    Assert.Equal(0.9, retrieved[0].Score);
    Assert.Equal(0.7, retrieved[1].Score);
    Assert.Equal(0.8, retrieved[2].Score);
}
```

**Step 3: Run the test to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests --no-build --filter "RetrieveAsync_WithLostInTheMiddle_ReordersResults" -v normal
```

Expected: FAIL — results are not reordered yet.

**Step 4: Wire into RagPipeline.RetrieveAsync**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, add `using Rag.NET.PostRetrieval;` at the top.

In `RetrieveAsync`, after the search call and before the return, add reordering. The existing code at the end of `RetrieveAsync` is:

```csharp
    return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);
```

Replace that return with:

```csharp
    var searchResults = await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);

    return opts.UseLostInTheMiddleReordering
        ? LostInTheMiddleReorderer.Reorder(searchResults)
        : searchResults;
```

Also update `RetrievalOptions` construction inside `AskAsync` and `AskStreamingAsync` to pass through the flag. In both methods, find:

```csharp
    var retrievalOptions = new RetrievalOptions
    {
        TopK = opts.TopK,
        MinScore = opts.MinScore,
        MetadataFilter = opts.MetadataFilter,
        UseHybridSearch = opts.UseHybridSearch,
    };
```

And add `UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,` to the initializer.

**Step 5: Build and run all tests**

```bash
dotnet build tests/Rag.NET.Tests --no-restore
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "Passed|Failed|passed|failed"
```

Expected: all tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs src/Rag.NET/Models/Options/RagOptions.cs src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: wire lost-in-the-middle reordering into RetrievalOptions and RagPipeline"
```
