# Redundancy Filter Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a post-retrieval step that drops near-duplicate chunks from search results using cosine similarity, so downstream prompts don't waste context on repeated content.

**Architecture:** Static class `RedundancyFilter` in `src/Rag.NET/PostRetrieval/` (mirrors `LostInTheMiddleReorderer` pattern). It re-embeds the retrieved chunk texts in a single batch call, then greedily accepts each chunk only if its cosine similarity to every already-accepted chunk is below the threshold. Wire it into `RagPipeline.RetrieveAsync` when `UseRedundancyFilter` is set on `RetrievalOptions`/`RagOptions`.

**Tech Stack:** C# 13, .NET 10, `Microsoft.Extensions.AI`, xunit.v3, NSubstitute.

---

### Task 1: Add `UseRedundancyFilter` and `RedundancyThreshold` to options

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Modify: `src/Rag.NET/Models/Options/RagOptions.cs`

**Step 1: Add to `RetrievalOptions`**

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add two properties:

```csharp
namespace Rag.NET.Models.Options;

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public IDictionary<string, string>? MetadataFilter { get; set; }
    public bool UseHybridSearch { get; set; }
    public bool UseLostInTheMiddleReordering { get; set; }
    public bool UseRedundancyFilter { get; set; }
    public float RedundancyThreshold { get; set; } = 0.95f;
}
```

**Step 2: Add to `RagOptions`**

In `src/Rag.NET/Models/Options/RagOptions.cs`, add:

```csharp
public bool UseRedundancyFilter { get; set; }
public float RedundancyThreshold { get; set; } = 0.95f;
```

**Step 3: Build to confirm no errors**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj -v minimal
```

Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs src/Rag.NET/Models/Options/RagOptions.cs
git commit -m "feat: add UseRedundancyFilter and RedundancyThreshold to RetrievalOptions and RagOptions"
```

---

### Task 2: Implement `RedundancyFilter` static class

**Files:**
- Create: `src/Rag.NET/PostRetrieval/RedundancyFilter.cs`
- Test: `tests/Rag.NET.Tests/PostRetrieval/RedundancyFilterTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/PostRetrieval/RedundancyFilterTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class RedundancyFilterTests
{
    private static SearchResult MakeResult(string text, double score) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = "doc", ChunkIndex = 0 },
        Score = score
    };

    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(
        params ReadOnlyMemory<float>[] vectorsInOrder)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var callCount = 0;
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = texts.Select((_, i) =>
                    new Embedding<float>(vectorsInOrder[callCount + i])).ToList();
                callCount += texts.Count;
                return Task.FromResult<IList<Embedding<float>>>(result);
            });
        return embedder;
    }

    [Fact]
    public async Task FilterAsync_EmptyList_ReturnsEmpty()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var result = await RedundancyFilter.FilterAsync([], embedder, 0.95f);
        Assert.Empty(result);
        await embedder.DidNotReceive().GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilterAsync_SingleResult_ReturnsSingleResult()
    {
        var embedder = MakeEmbedder(new float[] { 1f, 0f, 0f });
        var results = new[] { MakeResult("a", 0.9) };
        var filtered = await RedundancyFilter.FilterAsync(results, embedder, 0.95f);
        Assert.Single(filtered);
    }

    [Fact]
    public async Task FilterAsync_IdenticalEmbeddings_DropsRedundant()
    {
        // Two chunks with identical embeddings: cosine similarity = 1.0 > threshold 0.95 → second dropped
        var vec = new float[] { 1f, 0f, 0f };
        var embedder = MakeEmbedder(vec, vec);
        var results = new[] { MakeResult("a", 0.9), MakeResult("b", 0.8) };
        var filtered = await RedundancyFilter.FilterAsync(results, embedder, 0.95f);
        Assert.Single(filtered);
        Assert.Equal("a", filtered[0].Chunk.Text);
    }

    [Fact]
    public async Task FilterAsync_OrthogonalEmbeddings_KeepsBoth()
    {
        // Cosine similarity of orthogonal vectors = 0.0 < threshold → both kept
        var embedder = MakeEmbedder(new float[] { 1f, 0f, 0f }, new float[] { 0f, 1f, 0f });
        var results = new[] { MakeResult("a", 0.9), MakeResult("b", 0.8) };
        var filtered = await RedundancyFilter.FilterAsync(results, embedder, 0.95f);
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public async Task FilterAsync_PreservesOrderOfAcceptedResults()
    {
        // a and c are orthogonal, b is redundant to a
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 1f, 0f, 0f }; // same as a
        var c = new float[] { 0f, 1f, 0f }; // orthogonal
        var embedder = MakeEmbedder(a, b, c);
        var results = new[]
        {
            MakeResult("a", 0.9),
            MakeResult("b", 0.8),
            MakeResult("c", 0.7)
        };
        var filtered = await RedundancyFilter.FilterAsync(results, embedder, 0.95f);
        Assert.Equal(2, filtered.Count);
        Assert.Equal("a", filtered[0].Chunk.Text);
        Assert.Equal("c", filtered[1].Chunk.Text);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RedundancyFilterTests" -v minimal
```

Expected: FAIL — `RedundancyFilter` does not exist.

**Step 3: Implement `RedundancyFilter`**

Create `src/Rag.NET/PostRetrieval/RedundancyFilter.cs`:

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Models;

namespace Rag.NET.PostRetrieval;

public static class RedundancyFilter
{
    /// <summary>
    /// Filters out near-duplicate chunks using cosine similarity of their embeddings.
    /// Re-embeds all chunks in a single batch call, then greedily accepts each chunk
    /// only if its similarity to every previously accepted chunk is below <paramref name="threshold"/>.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> FilterAsync(
        IReadOnlyList<SearchResult> results,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        float threshold,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(embedder);

        if (results.Count == 0)
            return Array.Empty<SearchResult>();

        var texts = results.Select(r => r.Chunk.Text).ToList();
        var embeddings = await embedder.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var vectors = embeddings.Select(e => e.Vector).ToArray();
        var accepted = new List<(SearchResult Result, ReadOnlyMemory<float> Vector)>();

        for (int i = 0; i < results.Count; i++)
        {
            bool redundant = accepted.Any(a => CosineSimilarity(vectors[i], a.Vector) >= threshold);
            if (!redundant)
                accepted.Add((results[i], vectors[i]));
        }

        return accepted.Select(a => a.Result).ToList();
    }

    private static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;

        if (spanA.Length != spanB.Length)
            return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < spanA.Length; i++)
        {
            dot += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RedundancyFilterTests" -v minimal
```

Expected: all PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/PostRetrieval/RedundancyFilter.cs tests/Rag.NET.Tests/PostRetrieval/RedundancyFilterTests.cs
git commit -m "feat: add RedundancyFilter post-retrieval step using cosine similarity"
```

---

### Task 3: Wire `RedundancyFilter` into `RagPipeline.RetrieveAsync`

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs:116-155`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs:157-208` (AskAsync — propagate new options)
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs:211-244` (AskStreamingAsync — propagate new options)
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write failing tests**

Add to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`:

```csharp
[Fact]
public async Task RetrieveAsync_WithRedundancyFilter_DropsRedundantChunks()
{
    // Arrange: two results with identical embeddings
    var result1 = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 };
    var result2 = new SearchResult { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 };
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([result1, result2]));

    // Query embedding + two re-embed calls all return same vector
    var sameVec = new float[] { 1f, 0f, 0f };
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(ci =>
        {
            var texts = ci.Arg<IEnumerable<string>>().ToList();
            return Task.FromResult<IList<Embedding<float>>>(
                texts.Select(_ => new Embedding<float>(sameVec)).ToList());
        });

    // Act
    var retrieved = await _pipeline.RetrieveAsync("q",
        new RetrievalOptions { UseRedundancyFilter = true, RedundancyThreshold = 0.95f });

    // Assert: only first chunk kept
    Assert.Single(retrieved);
    Assert.Equal("a", retrieved[0].Chunk.Text);
}

[Fact]
public async Task RetrieveAsync_WithoutRedundancyFilter_KeepsAll()
{
    var result1 = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 };
    var result2 = new SearchResult { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 };
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([result1, result2]));

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(ci =>
        {
            var texts = ci.Arg<IEnumerable<string>>().ToList();
            return Task.FromResult<IList<Embedding<float>>>(
                texts.Select(_ => new Embedding<float>(new float[] { 1f, 0f, 0f })).ToList());
        });

    var retrieved = await _pipeline.RetrieveAsync("q", new RetrievalOptions { UseRedundancyFilter = false });

    Assert.Equal(2, retrieved.Count);
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RetrieveAsync_WithRedundancyFilter" -v minimal
```

Expected: FAIL.

**Step 3: Wire filter in `RetrieveAsync`**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, update `RetrieveAsync` — replace the final return statement:

```csharp
// existing code builds searchResults...

if (opts.UseLostInTheMiddleReordering)
    searchResults = LostInTheMiddleReorderer.Reorder(searchResults);

if (opts.UseRedundancyFilter)
    searchResults = await RedundancyFilter.FilterAsync(searchResults, embeddingGenerator, opts.RedundancyThreshold, cancellationToken).ConfigureAwait(false);

return searchResults;
```

Also propagate the new options in `AskAsync` and `AskStreamingAsync`. In both methods, the `retrievalOptions` construction block already maps RagOptions → RetrievalOptions. Add:

```csharp
UseRedundancyFilter = opts.UseRedundancyFilter,
RedundancyThreshold = opts.RedundancyThreshold,
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RetrieveAsync_WithRedundancyFilter" -v minimal
dotnet test tests/Rag.NET.Tests -v minimal
```

Expected: all PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: wire RedundancyFilter into RagPipeline.RetrieveAsync"
```
