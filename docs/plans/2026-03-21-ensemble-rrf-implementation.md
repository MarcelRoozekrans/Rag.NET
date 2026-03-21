# Ensemble / Reciprocal Rank Fusion (RRF) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract the RRF hybrid-search logic from `VectorStoreBehavior` into a first-class `EnsembleBehavior` with configurable per-retriever weights and k.

**Architecture:** New `EnsembleBehavior` intercepts before `VectorStoreBehavior` when `UseHybridSearch=true`, running dense + BM25 in parallel and merging via weighted RRF. A new `EnsembleOptions` record holds `DenseWeight`, `Bm25Weight`, and `K`. `VectorStoreBehavior` is stripped of its BM25 fallback path — it becomes pure dense-only. `RrfMerger` gains a weighted overload.

**Tech Stack:** .NET 10, NSubstitute (tests), xunit.v3, existing `IRetrievalBehavior` middleware pattern, `[Inject]`/`[Singleton]` source generators from ZeroAlloc.Inject.

---

### Task 1: Add `EnsembleOptions` and wire it into `RetrievalOptions`

**Files:**
- Create: `src/Rag.NET/Models/Options/EnsembleOptions.cs`
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs` (scaffold only in this task)

**Step 1: Write the failing test**

Create `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`:

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class EnsembleBehaviorTests
{
    [Fact]
    public void EnsembleOptions_Defaults_AreCorrect()
    {
        var opts = new EnsembleOptions();
        Assert.Equal(0.5f, opts.DenseWeight);
        Assert.Equal(0.5f, opts.Bm25Weight);
        Assert.Equal(60, opts.K);
    }

    [Fact]
    public void RetrievalOptions_EnsembleOptions_DefaultsToNull()
    {
        var opts = new RetrievalOptions();
        Assert.Null(opts.EnsembleOptions);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests --filter "EnsembleBehaviorTests"
```
Expected: FAIL — `EnsembleOptions` type not found.

**Step 3: Create `EnsembleOptions.cs`**

```csharp
namespace Rag.NET.Models.Options;

public sealed class EnsembleOptions
{
    public float DenseWeight { get; init; } = 0.5f;
    public float Bm25Weight  { get; init; } = 0.5f;
    public int   K           { get; init; } = 60;
}
```

**Step 4: Add property to `RetrievalOptions`**

Add after the `UseHybridSearch` property in `src/Rag.NET/Models/Options/RetrievalOptions.cs`:

```csharp
/// <summary>
/// Per-retriever weights and k for RRF hybrid search.
/// Null applies defaults (0.5 / 0.5 / 60). Only used when <see cref="UseHybridSearch"/> is true.
/// </summary>
public EnsembleOptions? EnsembleOptions { get; init; }
```

**Step 5: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "EnsembleBehaviorTests"
```
Expected: PASS (2 tests).

**Step 6: Commit**

```bash
git add src/Rag.NET/Models/Options/EnsembleOptions.cs \
        src/Rag.NET/Models/Options/RetrievalOptions.cs \
        tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs
git commit -m "feat: add EnsembleOptions and wire into RetrievalOptions"
```

---

### Task 2: Add weighted overload to `RrfMerger`

**Files:**
- Modify: `src/Rag.NET/Search/RrfMerger.cs`
- Test: `tests/Rag.NET.Tests/Search/RrfMergerTests.cs` (already exists — check with `Glob`)

> **Note:** `RrfMerger` is `internal`. Tests in `Rag.NET.Tests` have access via `InternalsVisibleTo` — confirm the test project references the main assembly, not a separate one.

**Step 1: Find or create the RrfMerger tests file**

Run: `find tests/Rag.NET.Tests/Search -name "*Rrf*"` — if it exists, add to it; if not, create `tests/Rag.NET.Tests/Search/RrfMergerTests.cs`.

**Step 2: Write the failing tests**

Add these to the RrfMerger test file:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class RrfMergerWeightedTests
{
    private static SearchResult MakeDenseResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = $"{docId}-{chunkIndex}" },
            Score = score
        };

    private static (TextChunk chunk, double score) MakeBm25Hit(string docId, int chunkIndex) =>
        (new TextChunk { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = $"{docId}-{chunkIndex}" }, 1.0);

    [Fact]
    public void Merge_Weighted_EqualWeights_ProducesExpectedOrder()
    {
        // doc-A: rank 0 in dense, not in BM25
        // doc-B: rank 0 in BM25, not in dense
        // equal weights → both get 1/(60+1) = 0.01639; same RRF score; order by first-seen
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.5f, Bm25Weight = 0.5f, K = 60 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Merge_Weighted_Bm25Heavy_RanksBm25ResultHigher()
    {
        // doc-A rank 0 dense, doc-B rank 0 BM25 (doc-B not in dense, doc-A not in BM25)
        // DenseWeight=0.1, Bm25Weight=0.9 → doc-B score = 0.9/61 ≈ 0.01475 > doc-A score = 0.1/61 ≈ 0.00164
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.1f, Bm25Weight = 0.9f, K = 60 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal("doc-B", results[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public void Merge_Weighted_DenseHeavy_RanksDenseResultHigher()
    {
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.9f, Bm25Weight = 0.1f, K = 60 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal("doc-A", results[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public void Merge_Weighted_KClampedToOne_WhenKLessThanOne()
    {
        // K < 1 should be clamped to 1 — result should not throw
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.5f, Bm25Weight = 0.5f, K = 0 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Merge_Weighted_EmptyBm25_ReturnsOnlyDenseResults()
    {
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = Array.Empty<(TextChunk, double)>();
        var options = new EnsembleOptions();

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Single(results);
        Assert.Equal("doc-A", results[0].Chunk.DocumentId.ToString());
    }
}
```

**Step 3: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "RrfMergerWeightedTests"
```
Expected: FAIL — no overload with `EnsembleOptions`.

**Step 4: Add weighted overload to `RrfMerger`**

In `src/Rag.NET/Search/RrfMerger.cs`, add after the existing `Merge` method:

```csharp
internal static IReadOnlyList<SearchResult> Merge(
    IReadOnlyList<SearchResult> dense,
    IReadOnlyList<(TextChunk chunk, double score)> bm25Hits,
    int topK,
    EnsembleOptions options)
{
    if (topK <= 0) return [];
    var k = Math.Max(1, options.K);

    var rrfScores = new Dictionary<(string docId, int chunkIndex), double>();
    var chunkLookup = new Dictionary<(string docId, int chunkIndex), TextChunk>();

    for (int rank = 0; rank < dense.Count; rank++)
    {
        var chunk = dense[rank].Chunk;
        var key = (chunk.DocumentId, chunk.ChunkIndex);
        var contrib = options.DenseWeight / (k + rank + 1);
        rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
        chunkLookup.TryAdd(key, chunk);
    }

    for (int rank = 0; rank < bm25Hits.Count; rank++)
    {
        var chunk = bm25Hits[rank].chunk;
        var key = (chunk.DocumentId, chunk.ChunkIndex);
        var contrib = options.Bm25Weight / (k + rank + 1);
        rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
        chunkLookup.TryAdd(key, chunk);
    }

    var sorted = new List<(double score, TextChunk chunk)>(rrfScores.Count);
    foreach (var (key, score) in rrfScores)
        sorted.Add((score, chunkLookup[key]));

    sorted.Sort(static (a, b) => b.score.CompareTo(a.score));

    var count = Math.Min(topK, sorted.Count);
    var result = new List<SearchResult>(count);
    for (int i = 0; i < count; i++)
        result.Add(new SearchResult { Chunk = sorted[i].chunk, Score = sorted[i].score });

    return result;
}
```

You also need to add the `using` at the top:
```csharp
using Rag.NET.Models.Options;
```

**Step 5: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "RrfMergerWeightedTests"
```
Expected: PASS (5 tests).

**Step 6: Commit**

```bash
git add src/Rag.NET/Search/RrfMerger.cs \
        tests/Rag.NET.Tests/Search/RrfMergerWeightedTests.cs
git commit -m "feat: add weighted RRF overload to RrfMerger accepting EnsembleOptions"
```

---

### Task 3: Implement `EnsembleBehavior`

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs` (extend from Task 1)

**Step 1: Write the failing tests**

Add to `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class EnsembleBehaviorTests
{
    // ... existing tests from Task 1 ...

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static (TextChunk chunk, double score) MakeBm25Hit(string docId, int chunkIndex) =>
        (new TextChunk { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = $"{docId}-{chunkIndex}" }, 1.0);

    private static RetrievalContext MakeCtx(RetrievalOptions options) =>
        new() { Query = "test query", Options = options, Logger = NullLogger.Instance };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    [Fact]
    public async Task HandleAsync_HybridSearchFalse_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new EnsembleBehavior
        {
            Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = new InMemoryBm25Index(),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = false });

        var nextCalled = false;
        var output = await sut.HandleAsync(ctx, ct, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>(expected);
        });

        Assert.True(nextCalled);
        Assert.Same(expected, output);
    }

    [Fact]
    public async Task HandleAsync_HybridSearchTrue_MergesDenseAndBm25()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var denseResults = new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) };

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.NotEmpty(output);
        Assert.Contains(output, r => r.Chunk.DocumentId.ToString() == "doc-dense");
        Assert.Contains(output, r => r.Chunk.DocumentId.ToString() == "doc-bm25");
    }

    [Fact]
    public async Task HandleAsync_HybridSearchTrue_CustomWeights_BothResultsIncluded()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        var denseResults = new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) };

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { MakeBm25Hit("doc-bm25", 0) });

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions
        {
            UseHybridSearch = true,
            TopK = 5,
            EnsembleOptions = new EnsembleOptions { DenseWeight = 0.1f, Bm25Weight = 0.9f, K = 60 }
        });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        // BM25-heavy: doc-bm25 should be ranked first
        Assert.Equal("doc-bm25", output[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public async Task HandleAsync_Bm25Throws_ReturnsOnlyDenseResults()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        var denseResults = new List<SearchResult> { MakeResult("doc-dense", 0, 0.9) };

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(denseResults);
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Throws(new InvalidOperationException("BM25 failure"));

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.Single(output);
        Assert.Equal("doc-dense", output[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public async Task HandleAsync_EnsembleOptionsNull_UsesDefaults()
    {
        var ct = TestContext.Current.CancellationToken;

        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = Substitute.For<IBm25Index>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("doc-A", 0, 0.9) });
        bm25Index.Search(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Array.Empty<(TextChunk, double)>());

        var sut = new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = vectorStore,
            Bm25Index = bm25Index,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true, EnsembleOptions = null });

        // Should not throw — defaults are applied
        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException());

        Assert.NotEmpty(output);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "EnsembleBehaviorTests"
```
Expected: FAIL — `EnsembleBehavior` type not found.

**Step 3: Create `EnsembleBehavior.cs`**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class EnsembleBehavior : IRetrievalBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var opts = ctx.Options;

        if (!opts.UseHybridSearch)
            return await next(ctx, ct).ConfigureAwait(false);

        var ensembleOpts = opts.EnsembleOptions ?? new EnsembleOptions();
        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
        };

        var textToEmbed = opts.EmbeddingTextOverride ?? ctx.Query;
        var queryEmbeddings = await Embedder.GenerateAsync([textToEmbed], cancellationToken: ct).ConfigureAwait(false);

        var denseTask = VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct);

        IReadOnlyList<(TextChunk chunk, double score)> bm25Hits;
        try
        {
            bm25Hits = Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.EnsembleBm25Failed(ctx.Logger, ex);
            var dense = await denseTask.ConfigureAwait(false);
            return dense;
        }

        var denseResults = await denseTask.ConfigureAwait(false);
        var merged = RrfMerger.Merge(denseResults, bm25Hits, opts.TopK, ensembleOpts);

        RagPipelineLog.VectorStoreSearchCompleted(ctx.Logger, "ensemble-rrf", merged.Count);
        return merged;
    }
}
```

**Step 4: Add `EnsembleBm25Failed` log entry to `RagPipelineLog`**

Open `src/Rag.NET/Logging/RagPipelineLog.cs`. Add:

```csharp
[LoggerMessage(Level = LogLevel.Warning, Message = "EnsembleBehavior: BM25 search failed; falling back to dense-only results.")]
internal static partial void EnsembleBm25Failed(ILogger logger, Exception exception);
```

**Step 5: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "EnsembleBehaviorTests"
```
Expected: PASS.

**Step 6: Commit**

```bash
git add src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs
git commit -m "feat: add EnsembleBehavior with weighted RRF hybrid search"
```

---

### Task 4: Remove RRF fallback path from `VectorStoreBehavior` and register `EnsembleBehavior`

**Files:**
- Modify: `src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs`
- Modify: `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs` (remove the dead hybrid path test if present; add a test asserting VectorStoreBehavior never calls BM25)

**Step 1: Write the failing test**

Add to `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs`:

```csharp
[Fact]
public async Task VectorStore_WhenHybridSearch_StillCallsDenseOnly_DoesNotTouchBm25()
{
    var ct = TestContext.Current.CancellationToken;
    var vectorStore = Substitute.For<IVectorStore>();
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    var bm25Index = Substitute.For<IBm25Index>();

    var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
    var expected = MakeResult("doc-1", 0, 0.95);

    embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
    vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult> { expected });

    var sut = new VectorStoreBehavior
    {
        VectorStore = vectorStore,
        Embedder = embedder,
        Bm25Index = bm25Index,
    };
    var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true });

    var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

    Assert.Single(output);
    bm25Index.DidNotReceive().Search(Arg.Any<string>(), Arg.Any<int>());
}
```

**Step 2: Run tests to verify it fails**

```
dotnet test tests/Rag.NET.Tests --filter "VectorStore_WhenHybridSearch_StillCallsDenseOnly"
```
Expected: FAIL — current `VectorStoreBehavior` calls `Bm25Index.Search` when `UseHybridSearch = true`.

**Step 3: Simplify `VectorStoreBehavior`**

Replace the hybrid branch in `VectorStoreBehavior.HandleAsync` — the entire `if (opts.UseHybridSearch) { ... } else { ... }` block becomes:

```csharp
var results = await VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false);
RagPipelineLog.VectorStoreSearchCompleted(ctx.Logger, "dense", results.Count);
return results;
```

Also remove the unused `searchMode` variable, the `IHybridSearchable` branch, and the BM25 imports if no longer needed.

> `IBm25Index` is still injected (the property is used by `EnsembleBehavior`, not `VectorStoreBehavior`) — but the `[Inject] public IBm25Index Bm25Index` property **must be removed** from `VectorStoreBehavior` since `VectorStoreBehavior` no longer uses it.

Also remove the `using Rag.NET.Search;` import if `RrfMerger` is the only consumer.

**Step 4: Register `EnsembleBehavior` in `RetrievalPipelineBuilder`**

In `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs`, add `typeof(EnsembleBehavior)` to `_types` just before `typeof(VectorStoreBehavior)`:

```csharp
private readonly List<Type> _types =
[
    typeof(SelfQueryBehavior),
    typeof(ResultCacheBehavior),
    typeof(LostInTheMiddleBehavior),
    typeof(MmrBehavior),
    typeof(RedundancyFilterBehavior),
    typeof(ParentDocumentRetrievalBehavior),
    typeof(RerankingBehavior),
    typeof(MultiQueryBehavior),
    typeof(HydeBehavior),
    typeof(EmbeddingCacheBehavior),
    typeof(FilterBehavior),
    typeof(EnsembleBehavior),   // <-- new
    typeof(VectorStoreBehavior),
];
```

**Step 5: Run full test suite**

```
dotnet test tests/Rag.NET.Tests
```
Expected: All existing tests pass, new test passes.

**Step 6: Commit**

```bash
git add src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs \
        src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs \
        tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs
git commit -m "refactor: extract hybrid-search from VectorStoreBehavior; register EnsembleBehavior"
```

---

### Task 5: Run full solution tests and verify nothing is broken

**Step 1: Build and test all projects**

```
dotnet test
```
Expected: All tests pass (currently 487+ across 12 projects).

**Step 2: Commit if there are any fixups**

Only commit if additional fixes were needed (shouldn't be, but verify).

---

## Summary

| Task | Key Change |
|------|-----------|
| 1 | `EnsembleOptions` model + `RetrievalOptions.EnsembleOptions` property |
| 2 | `RrfMerger.Merge` weighted overload |
| 3 | `EnsembleBehavior` with parallel dense+BM25 + RRF merge |
| 4 | Strip hybrid path from `VectorStoreBehavior`; register `EnsembleBehavior` before it |
| 5 | Full test suite green check |
