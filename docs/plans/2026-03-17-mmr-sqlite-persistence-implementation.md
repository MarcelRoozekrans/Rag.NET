# MMR Retrieval + SQLite Persistence Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Maximal Marginal Relevance (MMR) retrieval and SQLite-backed persistence for the in-memory BM25 index and parent chunk store.

**Architecture:** MMR is a post-retrieval decorator (`MmrRetriever`) that over-fetches candidates and uses a static `MmrSelector` to greedily pick the most relevant and diverse results. SQLite persistence introduces `IBm25Index` / `IParentChunkStore` interfaces so that write-through SQLite implementations can be swapped in via `RagBuilder.UseSqlitePersistence()`, with a collection-name stale guard to detect when the vector store was replaced.

**Tech Stack:** xunit.v3, NSubstitute, `Microsoft.Data.Sqlite`, `System.Text.Json` (already in .NET SDK). All tests in `tests/Rag.NET.Tests/`.

---

## Part 1 — MMR Retrieval

---

### Task 1: Add MMR fields to `RetrievalOptions`

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`

**Step 1: Add three properties after `RedundancyThreshold`**

```csharp
/// <summary>
/// Set to <see langword="true"/> to enable Maximal Marginal Relevance selection for this call.
/// Requires <c>RagBuilder.UseMmr()</c>. Unlike most retrieval features, MMR is opt-in per call.
/// Has no effect when <c>UseMmr()</c> is not registered.
/// </summary>
public bool UseMmr { get; init; } = false;

/// <summary>
/// Lambda parameter for MMR: weight between relevance and diversity.
/// <c>1.0</c> = pure relevance (no diversity), <c>0.0</c> = pure diversity (ignores relevance).
/// Default <c>0.5</c> balances both.
/// </summary>
public float MmrLambda { get; init; } = 0.5f;

/// <summary>
/// Number of candidates to fetch before MMR selection.
/// Defaults to <see cref="TopK"/> * 3 when <see langword="null"/>.
/// Ignored when <see cref="UseMmr"/> is <see langword="false"/>.
/// </summary>
public int? MmrCandidateCount { get; init; }
```

**Step 2: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs
git commit -m "feat: add UseMmr, MmrLambda, MmrCandidateCount to RetrievalOptions"
```

---

### Task 2: `MmrSelector` — algorithm and unit tests

**Files:**
- Create: `src/Rag.NET/PostRetrieval/MmrSelector.cs`
- Create: `tests/Rag.NET.Tests/PostRetrieval/MmrSelectorTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Rag.NET.Tests/PostRetrieval/MmrSelectorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class MmrSelectorTests
{
    private static SearchResult MakeResult(string text, double score = 0.9) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = "doc-1", ChunkIndex = 0 },
        Score = score,
    };

    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(
        string[] allTexts, float[][] allVectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        // The embedder is called twice: once for the query, once for all chunks.
        // We match by the count of requested texts and return appropriate vectors.
        embedder.GenerateAsync(
                Arg.Is<IEnumerable<string>>(t => t.Count() == 1),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var text = ci.Arg<IEnumerable<string>>().First();
                var idx = Array.IndexOf(allTexts, text);
                return new GeneratedEmbeddings<Embedding<float>>(
                    [new Embedding<float>(allVectors[idx])]);
            });

        embedder.GenerateAsync(
                Arg.Is<IEnumerable<string>>(t => t.Count() > 1),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var embeddings = texts.Select(t =>
                {
                    var idx = Array.IndexOf(allTexts, t);
                    return new Embedding<float>(allVectors[idx]);
                }).ToList();
                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });

        return embedder;
    }

    [Fact]
    public async Task SelectAsync_EmptyCandidates_ReturnsEmpty()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var result = await MmrSelector.SelectAsync(
            "query", [], embedder, topK: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectAsync_TopKExceedsCandidates_ReturnsAll()
    {
        // query and "only" are orthogonal → both relevant, both selected
        var candidates = new[] { MakeResult("only") };
        var embedder = MakeEmbedder(
            ["query", "only"],
            [new float[] { 1f, 0f }, new float[] { 1f, 0f }]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("only", result[0].Chunk.Text);
    }

    [Fact]
    public async Task SelectAsync_Lambda1_SelectsByRelevanceOnly()
    {
        // lambda=1.0 → pure relevance, ignore diversity.
        // Query = [1, 0]. Chunk A = [1, 0] (sim=1.0), Chunk B = [0, 1] (sim=0.0).
        // TopK=1 → should select A (highest relevance).
        var candidates = new[]
        {
            MakeResult("A", score: 0.5),
            MakeResult("B", score: 0.5),
        };
        var embedder = MakeEmbedder(
            ["query", "A", "B"],
            [new float[] { 1f, 0f }, new float[] { 1f, 0f }, new float[] { 0f, 1f }]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 1, lambda: 1.0f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("A", result[0].Chunk.Text);
    }

    [Fact]
    public async Task SelectAsync_Lambda0_SelectsByDiversityOnly()
    {
        // lambda=0.0 → pure diversity. After selecting A (first pick = any with no prior),
        // B is maximally dissimilar to A (orthogonal), C is identical to A.
        // TopK=2 → [A, B] (A first, then B as most diverse from A).
        var candidates = new[]
        {
            MakeResult("A"),
            MakeResult("B"),
            MakeResult("C"),
        };
        var vecA = new float[] { 1f, 0f };
        var vecB = new float[] { 0f, 1f }; // orthogonal to A
        var vecC = new float[] { 1f, 0f }; // identical to A

        var embedder = MakeEmbedder(
            ["query", "A", "B", "C"],
            [new float[] { 1f, 0f }, vecA, vecB, vecC]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 2, lambda: 0.0f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        // A is selected first (no prior selected, scores tied — first in list wins)
        Assert.Equal("A", result[0].Chunk.Text);
        // B is more diverse from A than C is (B is orthogonal, C is identical)
        Assert.Equal("B", result[1].Chunk.Text);
    }

    [Fact]
    public async Task SelectAsync_DefaultLambda_BalancesRelevanceAndDiversity()
    {
        // lambda=0.5 (default). query=[1,0].
        // A=[1,0] (sim_q=1.0), B=[0,1] (sim_q=0.0), C=[0.9,0.44] (sim_q≈0.9).
        // Iteration 1: no prior. MMR(A)=0.5*1.0-0.5*0=0.5, MMR(B)=0, MMR(C)≈0.45. Select A.
        // Iteration 2: prior={A}. sim(B,A)=0. sim(C,A)≈0.9.
        //   MMR(B)=0.5*0-0.5*0=0. MMR(C)=0.5*0.9-0.5*0.9=0. Tied → B wins (first in remaining list).
        // TopK=2 → [A, B].
        var candidates = new[]
        {
            MakeResult("A"),
            MakeResult("B"),
            MakeResult("C"),
        };
        var embedder = MakeEmbedder(
            ["query", "A", "B", "C"],
            [
                new float[] { 1f, 0f },
                new float[] { 1f, 0f },
                new float[] { 0f, 1f },
                new float[] { 0.9f, (float)Math.Sqrt(1 - 0.81) },
            ]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Chunk.Text);
        Assert.Equal("B", result[1].Chunk.Text);
    }
}
```

**Step 2: Run — confirm it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "MmrSelectorTests" -v n
```
Expected: FAIL — `MmrSelector` does not exist.

**Step 3: Implement `MmrSelector`**

```csharp
// src/Rag.NET/PostRetrieval/MmrSelector.cs
using Microsoft.Extensions.AI;
using Rag.NET.Models;

namespace Rag.NET.PostRetrieval;

public static class MmrSelector
{
    /// <summary>
    /// Greedily selects <paramref name="topK"/> results that are both relevant to
    /// <paramref name="query"/> and maximally dissimilar from each other.
    /// </summary>
    /// <param name="lambda">
    /// Trade-off weight: 1.0 = pure relevance, 0.0 = pure diversity. Default 0.5.
    /// </param>
    public static async Task<IReadOnlyList<SearchResult>> SelectAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        int topK,
        float lambda = 0.5f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(embedder);

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        var k = Math.Min(topK, candidates.Count);

        // Embed query and all candidate chunks.
        var queryEmbedding = await embedder.GenerateAsync([query], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var queryVec = queryEmbedding[0].Vector;

        var chunkTexts = candidates.Select(r => r.Chunk.Text).ToList();
        var chunkEmbeddings = await embedder.GenerateAsync(chunkTexts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var chunkVecs = chunkEmbeddings.Select(e => e.Vector).ToArray();

        var selected = new List<(SearchResult Result, ReadOnlyMemory<float> Vector)>(k);
        var remaining = Enumerable.Range(0, candidates.Count).ToList();

        for (int iter = 0; iter < k; iter++)
        {
            int bestIdx = -1;
            float bestScore = float.NegativeInfinity;

            foreach (var i in remaining)
            {
                var simQuery = CosineSimilarity(chunkVecs[i], queryVec);

                float maxSimSelected = 0f;
                foreach (var (_, selVec) in selected)
                    maxSimSelected = Math.Max(maxSimSelected, CosineSimilarity(chunkVecs[i], selVec));

                var score = lambda * simQuery - (1f - lambda) * maxSimSelected;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break;
            selected.Add((candidates[bestIdx], chunkVecs[bestIdx]));
            remaining.Remove(bestIdx);
        }

        return selected.Select(s => s.Result).ToList().AsReadOnly();
    }

    private static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;
        if (spanA.Length != spanB.Length) return 0f;

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

**Step 4: Run tests — confirm green**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "MmrSelectorTests" -v n
```
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/PostRetrieval/MmrSelector.cs tests/Rag.NET.Tests/PostRetrieval/MmrSelectorTests.cs
git commit -m "feat: add MmrSelector with greedy MMR algorithm"
```

---

### Task 3: `MmrRetriever` — decorator and unit tests

**Files:**
- Create: `src/Rag.NET/Retrieval/MmrRetriever.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/MmrRetrieverTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Rag.NET.Tests/Retrieval/MmrRetrieverTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class MmrRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly MmrRetriever _sut;

    public MmrRetrieverTests()
    {
        _sut = new MmrRetriever(_inner, _embedder);
    }

    private static SearchResult MakeResult(string docId, string text, double score = 0.9) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = docId, ChunkIndex = 0 },
        Score = score,
    };

    [Fact]
    public async Task RetrieveAsync_UseMmrFalse_PassesThroughWithoutCallingEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a"), MakeResult("doc-2", "b") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);

        var opts = new RetrievalOptions { UseMmr = false };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
        await _embedder.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, ct);
    }

    [Fact]
    public async Task RetrieveAsync_UseMmrTrue_OverFetchesFromInner()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns([]);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([]));

        var opts = new RetrievalOptions { UseMmr = true, TopK = 3, MmrCandidateCount = 9 };
        await _sut.RetrieveAsync("q", opts, ct);

        // inner must be called with TopK = MmrCandidateCount (9), not the original TopK (3)
        await _inner.Received(1).RetrieveAsync(
            "q",
            Arg.Is<RetrievalOptions?>(o => o!.TopK == 9),
            ct);
    }

    [Fact]
    public async Task RetrieveAsync_UseMmrTrue_DefaultCandidateCount_IsTopKTimesThree()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns([]);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([]));

        var opts = new RetrievalOptions { UseMmr = true, TopK = 4 }; // no MmrCandidateCount
        await _sut.RetrieveAsync("q", opts, ct);

        await _inner.Received(1).RetrieveAsync(
            "q",
            Arg.Is<RetrievalOptions?>(o => o!.TopK == 12), // 4 * 3
            ct);
    }

    [Fact]
    public async Task RetrieveAsync_UseMmrTrue_NullOptions_UsesDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns([]);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([]));

        // null opts → UseMmr defaults to false → should pass through without embedding
        var output = await _sut.RetrieveAsync("q", null, ct);
        await _embedder.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, ct);
    }

    [Fact]
    public async Task RetrieveAsync_EmbedderFails_ReturnsUnsortedCandidates()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a"), MakeResult("doc-2", "b") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .ThrowsAsync(new HttpRequestException("API down"));

        var opts = new RetrievalOptions { UseMmr = true, TopK = 2 };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count); // fallback — all candidates returned
    }

    [Fact]
    public async Task RetrieveAsync_CancellationRequested_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .ThrowsAsync(new OperationCanceledException());

        var opts = new RetrievalOptions { UseMmr = true, TopK = 1 };
        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RetrieveAsync("q", opts, ct));
    }
}
```

**Step 2: Run — confirm it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "MmrRetrieverTests" -v n
```
Expected: FAIL — `MmrRetriever` does not exist.

**Step 3: Add log messages to `RagPipelineLog.cs`**

Add at the end of `src/Rag.NET/Logging/RagPipelineLog.cs`:

```csharp
[LoggerMessage(Level = LogLevel.Debug, Message = "MMR selection completed: {CandidateCount} candidates -> {ResultCount} result(s)")]
internal static partial void MmrSelectionCompleted(ILogger logger, int candidateCount, int resultCount);

[LoggerMessage(Level = LogLevel.Warning, Message = "MMR selection failed for query '{Query}', returning candidates in original order")]
internal static partial void MmrSelectionFailed(ILogger logger, string query, Exception exception);
```

**Step 4: Implement `MmrRetriever`**

```csharp
// src/Rag.NET/Retrieval/MmrRetriever.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that applies Maximal Marginal Relevance selection to retrieved results.
/// Opt-in per call: only active when <see cref="RetrievalOptions.UseMmr"/> is <see langword="true"/>.
/// </summary>
public sealed class MmrRetriever(
    IRetriever inner,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseMmr)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var candidateCount = opts.MmrCandidateCount ?? opts.TopK * 3;
        var expanded = opts with { TopK = candidateCount, UseMmr = false };

        var candidates = await inner.RetrieveAsync(query, expanded, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
            return candidates;

        try
        {
            var selected = await MmrSelector.SelectAsync(
                query, candidates, embeddingGenerator,
                topK: opts.TopK,
                lambda: opts.MmrLambda,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            RagPipelineLog.MmrSelectionCompleted(_logger, candidates.Count, selected.Count);
            return selected;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.MmrSelectionFailed(_logger, query, ex);
            return candidates;
        }
    }
}
```

**Step 5: Run tests — confirm green**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "MmrRetrieverTests" -v n
```
Expected: All PASS.

**Step 6: Commit**

```bash
git add src/Rag.NET/Retrieval/MmrRetriever.cs src/Rag.NET/Logging/RagPipelineLog.cs tests/Rag.NET.Tests/Retrieval/MmrRetrieverTests.cs
git commit -m "feat: add MmrRetriever decorator"
```

---

### Task 4: Wire MMR into DI via `RagBuilder.UseMmr()` and `ServiceCollectionExtensions`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Write the failing DI integration test**

Add to `ServiceCollectionExtensionsTests`:

```csharp
[Fact]
public async Task AddRagNet_WithMmr_CallsEmbedderForMmrSelection()
{
    var services = new ServiceCollection();
    var vectorStore = Substitute.For<IVectorStore>();
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    services.AddSingleton(vectorStore);
    services.AddSingleton(embedder);

    var singleVec = new float[] { 1f, 0f };
    embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(singleVec)]));
    vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult>());

    services.AddRagNet(b => b.UseMmr());

    var sp = services.BuildServiceProvider();
    var pipeline = sp.GetRequiredService<IRagPipeline>();

    await pipeline.RetrieveAsync("query", new RetrievalOptions { UseMmr = true },
        TestContext.Current.CancellationToken);

    // With UseMmr=true, embedder is called at least twice: once for vector search, once for MMR
    await embedder.Received(2).GenerateAsync(
        Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
}
```

**Step 2: Run — confirm it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "AddRagNet_WithMmr_CallsEmbedderForMmrSelection" -v n
```
Expected: FAIL — `UseMmr()` not defined on `RagBuilder`.

**Step 3: Add `UseMmr()` to `RagBuilder.cs`**

Add after `UseReranking<TReranker>()`:

```csharp
/// <summary>
/// Registers <see cref="MmrRetriever"/> in the post-retrieval chain.
/// When registered, MMR selection is opt-in per call: set
/// <c>new RetrievalOptions { UseMmr = true }</c> to activate.
/// </summary>
/// <remarks>
/// MMR over-fetches candidates (<see cref="RetrievalOptions.MmrCandidateCount"/>, default TopK × 3),
/// then selects <see cref="RetrievalOptions.TopK"/> results balancing relevance and diversity.
/// Requires <c>IEmbeddingGenerator</c> to be registered in DI.
/// </remarks>
public RagBuilder UseMmr()
{
    Services.AddSingleton<MmrRetriever>();
    return this;
}
```

Add the using at the top of `RagBuilder.cs`:
```csharp
using Rag.NET.Retrieval;
```

**Step 4: Wire `MmrRetriever` into `BuildRetrieverChain` in `ServiceCollectionExtensions.cs`**

In `BuildRetrieverChain`, add after the `RedundancyFilterRetriever` line and before `LostInTheMiddleRetriever`:

```csharp
var mmrRetriever = sp.GetService<MmrRetriever>();
if (mmrRetriever is not null)
{
    chain = mmrRetriever; // MmrRetriever wraps chain via its inner constructor param
}
```

Wait — `MmrRetriever` is registered as `AddSingleton<MmrRetriever>()`, which means DI constructs it but we need to pass `chain` as the `inner` parameter. DI can't do that automatically. We need manual construction like other decorators. Replace the `AddSingleton<MmrRetriever>()` approach with a marker and manual construction. Change the approach:

**In `RagBuilder.UseMmr()`:** Instead of registering `MmrRetriever`, register a marker:
```csharp
public RagBuilder UseMmr()
{
    Services.AddSingleton<MmrEnabled>();
    return this;
}
```

Where `MmrEnabled` is a tiny marker class:
```csharp
// src/Rag.NET/DependencyInjection/MmrEnabled.cs
namespace Rag.NET.DependencyInjection;

internal sealed class MmrEnabled;
```

**In `BuildRetrieverChain`**, after `RedundancyFilterRetriever` and before `LostInTheMiddleRetriever`:

```csharp
if (sp.GetService<MmrEnabled>() is not null)
{
    chain = new MmrRetriever(chain, embedder, sp.GetService<ILogger<MmrRetriever>>());
}
```

**Step 5: Run test — confirm green**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "AddRagNet_WithMmr_CallsEmbedderForMmrSelection" -v n
```
Expected: PASS.

**Step 6: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: All PASS.

**Step 7: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/MmrEnabled.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "feat: wire MmrRetriever into DI via RagBuilder.UseMmr()"
```

---

## Part 2 — SQLite Persistence

---

### Task 5: Extract `IBm25Index` interface

**Files:**
- Create: `src/Rag.NET/Abstractions/IBm25Index.cs`
- Modify: `src/Rag.NET/Search/InMemoryBm25Index.cs`

**Step 1: Create the interface**

```csharp
// src/Rag.NET/Abstractions/IBm25Index.cs
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// In-process keyword index supporting Add, Remove, and BM25-scored Search.
/// </summary>
public interface IBm25Index : IDisposable
{
    void Add(int docId, TextChunk chunk);
    void Remove(string documentId);
    IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK);
}
```

**Step 2: Make `InMemoryBm25Index` implement `IBm25Index`**

Change the class declaration in `src/Rag.NET/Search/InMemoryBm25Index.cs`:

```csharp
// Before:
public sealed class InMemoryBm25Index : IDisposable

// After:
public sealed class InMemoryBm25Index : IBm25Index
```

Add the using:
```csharp
using Rag.NET.Abstractions;
```

**Step 3: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Abstractions/IBm25Index.cs src/Rag.NET/Search/InMemoryBm25Index.cs
git commit -m "refactor: extract IBm25Index interface from InMemoryBm25Index"
```

---

### Task 6: Extract `IParentChunkStore` interface

**Files:**
- Create: `src/Rag.NET/Abstractions/IParentChunkStore.cs`
- Modify: `src/Rag.NET/Storage/InMemoryParentChunkStore.cs`

**Step 1: Create the interface**

```csharp
// src/Rag.NET/Abstractions/IParentChunkStore.cs
namespace Rag.NET.Abstractions;

/// <summary>
/// Store for parent chunk text keyed by (documentId, parentChunkIndex).
/// </summary>
public interface IParentChunkStore
{
    void Add(string documentId, int parentChunkIndex, string text);
    bool TryGet(string documentId, int parentChunkIndex, out string? text);
    void Remove(string documentId);
}
```

**Step 2: Make `InMemoryParentChunkStore` implement `IParentChunkStore`**

Change the class declaration in `src/Rag.NET/Storage/InMemoryParentChunkStore.cs`:

```csharp
// Before:
public sealed class InMemoryParentChunkStore

// After:
public sealed class InMemoryParentChunkStore : IParentChunkStore
```

Add the using:
```csharp
using Rag.NET.Abstractions;
```

**Step 3: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Abstractions/IParentChunkStore.cs src/Rag.NET/Storage/InMemoryParentChunkStore.cs
git commit -m "refactor: extract IParentChunkStore interface from InMemoryParentChunkStore"
```

---

### Task 7: Update consumers to use `IBm25Index` and `IParentChunkStore`

**Files:**
- Modify: `src/Rag.NET/Retrieval/VectorStoreRetriever.cs`
- Modify: `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- Modify: `src/Rag.NET/Retrieval/ParentDocumentRetriever.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Update `VectorStoreRetriever.cs`**

Change the constructor parameter from `InMemoryBm25Index` to `IBm25Index`:

```csharp
// Before:
public sealed class VectorStoreRetriever(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    InMemoryBm25Index bm25Index,
    ...

// After:
public sealed class VectorStoreRetriever(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IBm25Index bm25Index,
    ...
```

Add using: `using Rag.NET.Abstractions;`
Remove using: `using Rag.NET.Search;` (if no longer needed in this file)

**Step 2: Update `DocumentIngestor.cs`**

Change constructor parameters:

```csharp
// Before:
    InMemoryBm25Index bm25Index,
    InMemoryParentChunkStore? parentStore = null,

// After:
    IBm25Index bm25Index,
    IParentChunkStore? parentStore = null,
```

Add using: `using Rag.NET.Abstractions;`
Remove usings for `Rag.NET.Search` and `Rag.NET.Storage` if no longer referenced directly.

**Step 3: Update `ParentDocumentRetriever.cs`** (check if it uses the concrete type)

Read `src/Rag.NET/Retrieval/ParentDocumentRetriever.cs`, then update the constructor parameter from `InMemoryParentChunkStore` to `IParentChunkStore` if needed.

**Step 4: Update `ServiceCollectionExtensions.cs`**

a) In `AddRagNet`, change the `IIngestor` factory to resolve `IBm25Index` instead of `InMemoryBm25Index`:

```csharp
services.AddSingleton<IIngestor>(sp => new DocumentIngestor(
    sp.GetServices<IDocumentParser>(),
    sp.GetRequiredService<IChunkingStrategy>(),
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
    sp.GetRequiredService<ChunkingOptions>(),
    sp.GetRequiredService<IBm25Index>(),         // was InMemoryBm25Index
    sp.GetService<IParentChunkStore>(),           // was InMemoryParentChunkStore
    sp.GetService<ParentDocumentOptions>()));
```

b) In `BuildRetrieverChain`, change `bm25Index` resolution:

```csharp
var bm25Index = sp.GetRequiredService<IBm25Index>();   // was InMemoryBm25Index
```

c) In the parent store check:

```csharp
var parentStore = sp.GetService<IParentChunkStore>();  // was InMemoryParentChunkStore
if (parentDocOptions is not null && parentStore is not null)
{
    chain = new ParentDocumentRetriever(chain, parentStore, ...);
}
```

d) After `configure?.Invoke(builder)`, add the default `IBm25Index` mapping:

```csharp
// Default IBm25Index → InMemoryBm25Index (overridden by UseSqlitePersistence)
services.TryAddSingleton<IBm25Index>(sp => sp.GetRequiredService<InMemoryBm25Index>());
```

e) In `UseParentDocumentRetrieval()` in `RagBuilder.cs`, add `IParentChunkStore` mapping after the `InMemoryParentChunkStore` registration:

```csharp
Services.AddSingleton<InMemoryParentChunkStore>();
Services.TryAddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<InMemoryParentChunkStore>());
```

**Step 5: Update the DI test that uses `InMemoryParentChunkStore` directly**

In `ServiceCollectionExtensionsTests.cs`, find the test `AddRagNet_WithParentDocumentRetrieval_ReplacesChildWithParentText` and the opt-out test. Change:

```csharp
// Before:
var parentStore = sp.GetRequiredService<Rag.NET.Storage.InMemoryParentChunkStore>();

// After:
var parentStore = sp.GetRequiredService<Rag.NET.Abstractions.IParentChunkStore>();
```

**Step 6: Build and run all tests**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
dotnet test tests/Rag.NET.Tests/
```
Expected: Build succeeded, all tests PASS.

**Step 7: Commit**

```bash
git add src/Rag.NET/Retrieval/VectorStoreRetriever.cs src/Rag.NET/Ingestion/DocumentIngestor.cs src/Rag.NET/Retrieval/ParentDocumentRetriever.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs src/Rag.NET/DependencyInjection/RagBuilder.cs tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "refactor: consume IBm25Index and IParentChunkStore in retriever, ingestor, and DI"
```

---

### Task 8: Add `Microsoft.Data.Sqlite` package and `SqliteBm25Index`

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`
- Create: `src/Rag.NET/Storage/SqliteBm25Index.cs`
- Create: `tests/Rag.NET.Tests/Storage/SqliteBm25IndexTests.cs`

**Step 1: Add the package**

```bash
dotnet add src/Rag.NET/Rag.NET.csproj package Microsoft.Data.Sqlite
```

**Step 2: Write failing tests**

```csharp
// tests/Rag.NET.Tests/Storage/SqliteBm25IndexTests.cs
using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class SqliteBm25IndexTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-test-{Guid.NewGuid():N}.db");
    private SqliteBm25Index? _sut;

    private SqliteBm25Index CreateSut(string collection = "test-coll")
    {
        _sut = new SqliteBm25Index(_dbPath, collection);
        return _sut;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static TextChunk MakeChunk(string docId, int idx, string text) => new()
    {
        Text = text, DocumentId = docId, ChunkIndex = idx,
    };

    [Fact]
    public async Task Add_ThenRestart_SearchFindsChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
        await sut.DisposeAsync();

        // Simulate restart: create new instance pointing to same db
        _sut = new SqliteBm25Index(_dbPath, "test-coll");
        var results = _sut.Search("hello", topK: 5);
        Assert.Single(results);
        Assert.Equal("hello world", results[0].chunk.Text);
    }

    [Fact]
    public async Task Remove_ThenRestart_SearchFindsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
        sut.Remove("doc-1");
        await sut.DisposeAsync();

        _sut = new SqliteBm25Index(_dbPath, "test-coll");
        var results = _sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task CollectionNameMismatch_WipesExistingData()
    {
        var sut = CreateSut("collection-A");
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
        await sut.DisposeAsync();

        // New instance with different collection name → stale guard wipes data
        _sut = new SqliteBm25Index(_dbPath, "collection-B");
        var results = _sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Add_MultipleChunks_AllReturnedBySearch()
    {
        var sut = CreateSut();
        sut.Add(1, MakeChunk("doc-1", 0, "the quick brown fox"));
        sut.Add(2, MakeChunk("doc-2", 0, "the lazy dog"));

        var results = sut.Search("fox", topK: 5);
        Assert.Single(results); // only first chunk matches "fox"
        Assert.Equal("doc-1", results[0].chunk.DocumentId);
    }
}
```

**Step 3: Run — confirm it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SqliteBm25IndexTests" -v n
```
Expected: FAIL — `SqliteBm25Index` does not exist.

**Step 4: Implement `SqliteBm25Index`**

```csharp
// src/Rag.NET/Storage/SqliteBm25Index.cs
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Search;

namespace Rag.NET.Storage;

/// <summary>
/// Write-through SQLite-backed BM25 index. Wraps <see cref="InMemoryBm25Index"/>.
/// Lazy-initialises on first use: creates tables, applies stale guard, loads persisted data.
/// </summary>
public sealed class SqliteBm25Index : IBm25Index
{
    private readonly InMemoryBm25Index _memory = new();
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;
    private bool _disposed;

    public SqliteBm25Index(string dbPath, string? collectionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
    }

    public void Add(int docId, TextChunk chunk)
    {
        EnsureInitialised();
        _memory.Add(docId, chunk);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO bm25_docs
                (doc_id, document_id, chunk_index, start_position, end_position, chunk_text, metadata_json, token_length)
            VALUES
                ($docId, $documentId, $chunkIndex, $startPos, $endPos, $text, $meta, $len)
            """;
        cmd.Parameters.AddWithValue("$docId", docId);
        cmd.Parameters.AddWithValue("$documentId", chunk.DocumentId);
        cmd.Parameters.AddWithValue("$chunkIndex", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("$startPos", chunk.StartPosition);
        cmd.Parameters.AddWithValue("$endPos", chunk.EndPosition);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(chunk.Metadata));
        cmd.Parameters.AddWithValue("$len", Tokenize(chunk.Text).Count);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string documentId)
    {
        EnsureInitialised();
        _memory.Remove(documentId);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bm25_docs WHERE document_id = $docId";
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
    {
        EnsureInitialised();
        return _memory.Search(query, topK);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
        _initLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }

    private void EnsureInitialised()
    {
        if (_initialised) return;
        _initLock.Wait();
        try
        {
            if (_initialised) return;
            InitialiseCore();
            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void InitialiseCore()
    {
        using var conn = OpenConnection();
        CreateSchema(conn);

        if (_collectionName is not null)
        {
            var storedName = ReadMetadata(conn, "collection_name");
            if (storedName is not null && storedName != _collectionName)
            {
                ClearData(conn);
            }
            WriteMetadata(conn, "collection_name", _collectionName);
        }

        LoadIntoMemory(conn);
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bm25_docs (
                doc_id         INTEGER NOT NULL PRIMARY KEY,
                document_id    TEXT NOT NULL,
                chunk_index    INTEGER NOT NULL,
                start_position INTEGER NOT NULL DEFAULT 0,
                end_position   INTEGER NOT NULL DEFAULT 0,
                chunk_text     TEXT NOT NULL,
                metadata_json  TEXT NOT NULL DEFAULT '{}',
                token_length   INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMetadata(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM rag_metadata WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMetadata(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO rag_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static void ClearData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bm25_docs; DELETE FROM rag_metadata;";
        cmd.ExecuteNonQuery();
    }

    private void LoadIntoMemory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT doc_id, document_id, chunk_index, start_position, end_position, chunk_text, metadata_json FROM bm25_docs";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var docId = reader.GetInt32(0);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6))
                           ?? new Dictionary<string, string>(StringComparer.Ordinal);

            var chunk = new TextChunk
            {
                DocumentId = reader.GetString(1),
                ChunkIndex = reader.GetInt32(2),
                StartPosition = reader.GetInt32(3),
                EndPosition = reader.GetInt32(4),
                Text = reader.GetString(5),
                Metadata = metadata,
            };
            _memory.Add(docId, chunk);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    // Minimal tokenizer matching InMemoryBm25Index.Tokenize for token_length calculation
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var lower = text.ToLowerInvariant();
        var start = -1;
        for (int i = 0; i <= lower.Length; i++)
        {
            bool isAlnum = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (isAlnum && start == -1) start = i;
            else if (!isAlnum && start != -1) { tokens.Add(lower[start..i]); start = -1; }
        }
        return tokens;
    }
}
```

**Step 5: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SqliteBm25IndexTests" -v n
```
Expected: All PASS.

**Step 6: Commit**

```bash
git add src/Rag.NET/Rag.NET.csproj src/Rag.NET/Storage/SqliteBm25Index.cs tests/Rag.NET.Tests/Storage/SqliteBm25IndexTests.cs
git commit -m "feat: add SqliteBm25Index with write-through persistence and stale guard"
```

---

### Task 9: `SqliteParentChunkStore` and tests

**Files:**
- Create: `src/Rag.NET/Storage/SqliteParentChunkStore.cs`
- Create: `tests/Rag.NET.Tests/Storage/SqliteParentChunkStoreTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Rag.NET.Tests/Storage/SqliteParentChunkStoreTests.cs
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class SqliteParentChunkStoreTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-parent-test-{Guid.NewGuid():N}.db");
    private SqliteParentChunkStore? _sut;

    private SqliteParentChunkStore CreateSut(string collection = "test-coll")
    {
        _sut = new SqliteParentChunkStore(_dbPath, collection);
        return _sut;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Add_ThenRestart_TryGetSucceeds()
    {
        var sut = CreateSut();
        sut.Add("doc-1", 0, "large parent text");
        await sut.DisposeAsync();

        _sut = new SqliteParentChunkStore(_dbPath, "test-coll");
        var found = _sut.TryGet("doc-1", 0, out var text);
        Assert.True(found);
        Assert.Equal("large parent text", text);
    }

    [Fact]
    public async Task Remove_ThenRestart_TryGetFails()
    {
        var sut = CreateSut();
        sut.Add("doc-1", 0, "large parent text");
        sut.Remove("doc-1");
        await sut.DisposeAsync();

        _sut = new SqliteParentChunkStore(_dbPath, "test-coll");
        var found = _sut.TryGet("doc-1", 0, out _);
        Assert.False(found);
    }

    [Fact]
    public async Task CollectionNameMismatch_WipesExistingData()
    {
        var sut = CreateSut("collection-A");
        sut.Add("doc-1", 0, "parent text");
        await sut.DisposeAsync();

        _sut = new SqliteParentChunkStore(_dbPath, "collection-B");
        var found = _sut.TryGet("doc-1", 0, out _);
        Assert.False(found);
    }

    [Fact]
    public void Add_MultipleParents_AllRetrievable()
    {
        var sut = CreateSut();
        sut.Add("doc-1", 0, "first parent");
        sut.Add("doc-1", 1, "second parent");

        Assert.True(sut.TryGet("doc-1", 0, out var t0));
        Assert.Equal("first parent", t0);
        Assert.True(sut.TryGet("doc-1", 1, out var t1));
        Assert.Equal("second parent", t1);
    }
}
```

**Step 2: Run — confirm failure**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SqliteParentChunkStoreTests" -v n
```
Expected: FAIL — `SqliteParentChunkStore` does not exist.

**Step 3: Implement `SqliteParentChunkStore`**

```csharp
// src/Rag.NET/Storage/SqliteParentChunkStore.cs
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;

namespace Rag.NET.Storage;

/// <summary>
/// Write-through SQLite-backed parent chunk store. Wraps <see cref="InMemoryParentChunkStore"/>.
/// Uses the same <c>rag_metadata</c> and collection-name stale guard as <see cref="SqliteBm25Index"/>.
/// The two stores can share a database file.
/// </summary>
public sealed class SqliteParentChunkStore : IParentChunkStore
{
    private readonly InMemoryParentChunkStore _memory = new();
    private readonly string _dbPath;
    private readonly string? _collectionName;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;
    private bool _disposed;

    public SqliteParentChunkStore(string dbPath, string? collectionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _collectionName = collectionName;
    }

    public void Add(string documentId, int parentChunkIndex, string text)
    {
        EnsureInitialised();
        _memory.Add(documentId, parentChunkIndex, text);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO parent_chunks (document_id, parent_chunk_index, text)
            VALUES ($docId, $idx, $text)
            """;
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.Parameters.AddWithValue("$idx", parentChunkIndex);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.ExecuteNonQuery();
    }

    public bool TryGet(string documentId, int parentChunkIndex, out string? text)
    {
        EnsureInitialised();
        return _memory.TryGet(documentId, parentChunkIndex, out text);
    }

    public void Remove(string documentId)
    {
        EnsureInitialised();
        _memory.Remove(documentId);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM parent_chunks WHERE document_id = $docId";
        cmd.Parameters.AddWithValue("$docId", documentId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }

    private void EnsureInitialised()
    {
        if (_initialised) return;
        _initLock.Wait();
        try
        {
            if (_initialised) return;
            InitialiseCore();
            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void InitialiseCore()
    {
        using var conn = OpenConnection();
        CreateSchema(conn);

        if (_collectionName is not null)
        {
            var storedName = ReadMetadata(conn, "collection_name");
            if (storedName is not null && storedName != _collectionName)
                ClearData(conn);
            WriteMetadata(conn, "collection_name", _collectionName);
        }

        LoadIntoMemory(conn);
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS parent_chunks (
                document_id        TEXT NOT NULL,
                parent_chunk_index INTEGER NOT NULL,
                text               TEXT NOT NULL,
                PRIMARY KEY (document_id, parent_chunk_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMetadata(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM rag_metadata WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMetadata(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO rag_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static void ClearData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM parent_chunks; DELETE FROM rag_metadata;";
        cmd.ExecuteNonQuery();
    }

    private void LoadIntoMemory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT document_id, parent_chunk_index, text FROM parent_chunks";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            _memory.Add(reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SqliteParentChunkStoreTests" -v n
```
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/Storage/SqliteParentChunkStore.cs tests/Rag.NET.Tests/Storage/SqliteParentChunkStoreTests.cs
git commit -m "feat: add SqliteParentChunkStore with write-through persistence and stale guard"
```

---

### Task 10: `RagBuilder.UseSqlitePersistence()` + DI wiring + integration test

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Write the failing DI integration test**

Add to `ServiceCollectionExtensionsTests.cs`:

```csharp
[Fact]
public async Task AddRagNet_WithSqlitePersistence_ReturnsChunksAfterSimulatedRestart()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-di-test-{Guid.NewGuid():N}.db");
    try
    {
        var ct = TestContext.Current.CancellationToken;

        // --- First "session" ---
        var services1 = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        services1.AddSingleton(vectorStore);
        services1.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
            .Returns(new List<SearchResult>());

        services1.AddRagNet(b => b.UseSqlitePersistence(dbPath, "test-coll"));
        var sp1 = services1.BuildServiceProvider();

        // Ingest a chunk — this should write to SQLite
        var ingestor1 = sp1.GetRequiredService<IIngestor>();
        await ingestor1.IngestAsync(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello world")),
            new Rag.NET.Models.DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt" },
            cancellationToken: ct);

        // --- Second "session" (same db, same collection) ---
        var services2 = new ServiceCollection();
        services2.AddSingleton(vectorStore);
        services2.AddSingleton(embedder);
        services2.AddRagNet(b => b.UseSqlitePersistence(dbPath, "test-coll"));
        var sp2 = services2.BuildServiceProvider();

        var bm25 = sp2.GetRequiredService<IBm25Index>();
        var results = bm25.Search("hello", topK: 5);

        Assert.NotEmpty(results); // chunks loaded from SQLite without re-ingestion
    }
    finally
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }
}
```

**Step 2: Run — confirm failure**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "AddRagNet_WithSqlitePersistence_ReturnsChunksAfterSimulatedRestart" -v n
```
Expected: FAIL — `UseSqlitePersistence` not defined.

**Step 3: Add `UseSqlitePersistence()` to `RagBuilder.cs`**

```csharp
/// <summary>
/// Registers SQLite-backed persistence for <see cref="IBm25Index"/> and <see cref="IParentChunkStore"/>.
/// On startup, both stores load persisted data from <paramref name="dbPath"/>.
/// Every Add/Remove writes through to SQLite synchronously.
/// </summary>
/// <param name="dbPath">Path to the SQLite database file. Created if it does not exist.</param>
/// <param name="collectionName">
/// Optional stale-data guard. If the registered name differs from what is stored in the database,
/// all persisted data is wiped before loading. Change this value when replacing the vector store.
/// Omit to skip the stale guard.
/// </param>
public RagBuilder UseSqlitePersistence(string dbPath, string? collectionName = null)
{
    Services.AddSingleton<SqliteBm25Index>(_ => new SqliteBm25Index(dbPath, collectionName));
    Services.AddSingleton<IBm25Index>(sp => sp.GetRequiredService<SqliteBm25Index>());

    Services.AddSingleton<SqliteParentChunkStore>(_ => new SqliteParentChunkStore(dbPath, collectionName));
    Services.AddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<SqliteParentChunkStore>());

    return this;
}
```

Add usings at top of `RagBuilder.cs`:
```csharp
using Rag.NET.Storage;
```

**Step 4: Run the integration test**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "AddRagNet_WithSqlitePersistence_ReturnsChunksAfterSimulatedRestart" -v n
```
Expected: PASS.

**Step 5: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: All PASS.

**Step 6: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "feat: add RagBuilder.UseSqlitePersistence() with IBm25Index and IParentChunkStore wiring"
```

---

### Task 11: Final — run full solution build and all tests

**Step 1: Build the full solution**

```bash
dotnet build Rag.NET.slnx
```
Expected: Build succeeded, 0 errors.

**Step 2: Run all core tests**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: All PASS.

**Step 3: Commit if any fixes were needed, then tag**

```bash
git commit -m "chore: post-implementation cleanup" --allow-empty
```
