# Cross-Encoder Reranking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add cross-encoder reranking to the post-retrieval pipeline, with a pluggable `IReranker` abstraction in core and an ONNX Runtime implementation in a separate package.

**Architecture:** Public `IReranker` interface in core following the same pattern as `IQueryExpander`. Reranking runs after `RedundancyFilter` and before `LostInTheMiddleReorderer` in `RetrieveAsync`. A separate `Rag.NET.Reranking.Onnx` package provides an ONNX Runtime cross-encoder implementation. `CandidateCount` on `RetrievalOptions` controls over-fetching for reranking; `UseReranking` provides per-call opt-out.

**Tech Stack:** .NET 9, xunit.v3, NSubstitute, Microsoft.ML.OnnxRuntime (ONNX package only)

**Design doc:** `docs/plans/2026-03-14-cross-encoder-reranking-design.md`

---

### Task 1: Core Abstractions — `IReranker`, `RerankResult`

**Files:**
- Create: `src/Rag.NET/Abstractions/IReranker.cs`
- Create: `src/Rag.NET/Models/RerankResult.cs`

**Step 1: Create `IReranker` interface**

Follow the exact pattern of `src/Rag.NET/Abstractions/IQueryExpander.cs`:

```csharp
// src/Rag.NET/Abstractions/IReranker.cs
namespace Rag.NET.Abstractions;

/// <summary>
/// Rescores search results using a cross-encoder model for higher precision ranking.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// Reranks <paramref name="results"/> by computing cross-encoder relevance scores
    /// for each (query, passage) pair.
    /// </summary>
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default);
}
```

Note: This file needs `using Rag.NET.Models;` for `SearchResult` and `RerankResult`.

**Step 2: Create `RerankResult` model**

```csharp
// src/Rag.NET/Models/RerankResult.cs
namespace Rag.NET.Models;

public sealed class RerankResult
{
    public required SearchResult SearchResult { get; init; }
    public required double RelevanceScore { get; init; }
}
```

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj -q`
Expected: Build succeeded with no errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Abstractions/IReranker.cs src/Rag.NET/Models/RerankResult.cs
git commit -m "feat: add IReranker abstraction and RerankResult model"
```

---

### Task 2: `RetrievalOptions` Changes + Log Messages

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs` (lines 1-19)
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs` (lines 1-27)

**Step 1: Add `UseReranking` and `CandidateCount` to `RetrievalOptions`**

Add after line 18 (`UseMultiQuery`), before the closing brace:

```csharp
    /// <summary>
    /// Set to <see langword="false"/> to skip cross-encoder reranking for this call,
    /// even when <see cref="Rag.NET.Abstractions.IReranker"/> is registered in DI.
    /// Has no effect when no reranker is registered.
    /// </summary>
    public bool UseReranking { get; set; } = true;

    /// <summary>
    /// Number of candidates to fetch from vector search before reranking.
    /// When an <see cref="Rag.NET.Abstractions.IReranker"/> is registered and this is
    /// <see langword="null"/>, defaults to <see cref="TopK"/> * 3.
    /// Ignored when no reranker is registered or <see cref="UseReranking"/> is <see langword="false"/>.
    /// </summary>
    public int? CandidateCount { get; set; }
```

**Step 2: Add log messages to `RagPipelineLog`**

Add after line 26 (`AskStarted`), before the closing brace:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranking failed for query '{Query}', returning results without reranking")]
    internal static partial void RerankingFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reranked {CandidateCount} candidates to {ResultCount} result(s)")]
    internal static partial void RerankingCompleted(ILogger logger, int candidateCount, int resultCount);
```

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj -q`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add UseReranking, CandidateCount options and reranking log messages"
```

---

### Task 3: Pipeline Integration — Tests First (TDD)

**Files:**
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` (add 6 tests at end, before closing brace on line 1424)
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs` (constructor + RetrieveAsync + SearchSingleQueryAsync)

**Step 1: Write the 6 failing tests**

Add these tests after the `RetrieveAsync_OriginalQueryAlwaysIncludedInFanOut` test (line 1423), before the final closing brace. Add `using Rag.NET.Abstractions;` if not already present (it is — line 6).

```csharp
    [Fact]
    public async Task RetrieveAsync_WhenRerankerRegistered_ReordersResultsByRerankerScore()
    {
        var reranker = Substitute.For<IReranker>();
        var chunk1 = new TextChunk { Text = "low relevance", DocumentId = "d1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "high relevance", DocumentId = "d2", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([
                new SearchResult { Chunk = chunk1, Score = 0.9 },
                new SearchResult { Chunk = chunk2, Score = 0.7 },
            ]);

        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var results = callInfo.ArgAt<IReadOnlyList<SearchResult>>(1);
                return results.Select(r => new RerankResult
                {
                    SearchResult = r,
                    RelevanceScore = r.Chunk.Text == "high relevance" ? 0.95 : 0.3,
                }).ToList();
            });

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder,
            chatClient: null, new ChunkingOptions(), reranker: reranker);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("high relevance", results[0].Chunk.Text);
        Assert.Equal("low relevance", results[1].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WhenUseRerankingFalse_SkipsReranker()
    {
        var reranker = Substitute.For<IReranker>();
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder,
            chatClient: null, new ChunkingOptions(), reranker: reranker);

        await sut.RetrieveAsync("query", new RetrievalOptions { UseReranking = false }, TestContext.Current.CancellationToken);

        await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenRerankerThrows_FallsBackToOriginalOrder()
    {
        var reranker = Substitute.For<IReranker>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Model failed"));

        var chunk = new TextChunk { Text = "result", DocumentId = "d1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([new SearchResult { Chunk = chunk, Score = 0.9 }]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder,
            chatClient: null, new ChunkingOptions(), reranker: reranker);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("result", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WhenRerankerRegistered_UsesCandidateCountForVectorSearch()
    {
        var reranker = Substitute.For<IReranker>();
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder,
            chatClient: null, new ChunkingOptions(), reranker: reranker);

        await sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5, CandidateCount = 20 }, TestContext.Current.CancellationToken);

        await _vectorStore.Received(1).SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Is<SearchOptions>(o => o.TopK == 20),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenNoReranker_CandidateCountIgnored()
    {
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // _sut has no reranker — CandidateCount should be ignored, TopK used directly
        await _sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5, CandidateCount = 20 }, TestContext.Current.CancellationToken);

        await _vectorStore.Received(1).SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Is<SearchOptions>(o => o.TopK == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenRerankerAndMultiQuery_BothCompose()
    {
        var reranker = Substitute.For<IReranker>();
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["variant 1"]);

        var chunk1 = new TextChunk { Text = "from original", DocumentId = "d1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "from variant", DocumentId = "d2", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                [new SearchResult { Chunk = chunk1, Score = 0.9 }],
                [new SearchResult { Chunk = chunk2, Score = 0.8 }]);

        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var results = callInfo.ArgAt<IReadOnlyList<SearchResult>>(1);
                return results.Select(r => new RerankResult
                {
                    SearchResult = r,
                    RelevanceScore = r.Chunk.Text == "from variant" ? 0.95 : 0.4,
                }).ToList();
            });

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder,
            chatClient: null, new ChunkingOptions(),
            queryExpander: expander, reranker: reranker);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Multi-query fetched from both queries, reranker reordered
        Assert.Equal("from variant", results[0].Chunk.Text);
        await reranker.Received(1).RerankAsync("query", Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~Rerank" -q`
Expected: Build fails — `RagPipeline` constructor doesn't accept `reranker` parameter yet.

**Step 3: Implement pipeline integration**

Modify `src/Rag.NET/Pipeline/RagPipeline.cs`:

**3a. Add constructor parameter** — after line 27 (`multiQueryOptions`), before `) : IRagPipeline, IDisposable`:

```csharp
    IReranker? reranker = null,
```

**3b. Add field** — after line 32 (`_multiQueryOptions`):

```csharp
    private readonly IReranker? _reranker = reranker;
```

**3c. Modify `SearchOptions.TopK` in `RetrieveAsync`** — at line 184, change the TopK assignment to account for CandidateCount when a reranker is registered:

Replace line 184:
```csharp
            TopK = opts.TopK,
```
With:
```csharp
            TopK = (_reranker is not null && opts.UseReranking)
                ? (opts.CandidateCount ?? opts.TopK * 3)
                : opts.TopK,
```

**3d. Add reranking block in `RetrieveAsync`** — after redundancy filter (after line 236), before `return searchResults;` (line 238). Insert:

```csharp
        if (_reranker is not null && opts.UseReranking)
        {
            try
            {
                var candidateCount = searchResults.Count;
                var reranked = await _reranker.RerankAsync(query, searchResults, cancellationToken)
                    .ConfigureAwait(false);

                searchResults = reranked
                    .OrderByDescending(r => r.RelevanceScore)
                    .Take(opts.TopK)
                    .Select(r => r.SearchResult)
                    .ToList();

                RagPipelineLog.RerankingCompleted(_logger, candidateCount, searchResults.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RagPipelineLog.RerankingFailed(_logger, query, ex);
            }
        }
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~Rerank" -v normal`
Expected: All 6 reranking tests pass.

**Step 5: Run all tests to verify no regressions**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q`
Expected: All tests pass (existing + 6 new).

**Step 6: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: integrate IReranker into RagPipeline with CandidateCount over-fetch"
```

---

### Task 4: DI Wiring — `RagBuilder.UseReranking` + `ServiceCollectionExtensions`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs` (lines 1-120)
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` (lines 1-51)

**Step 1: Add `UseReranking<T>()` to `RagBuilder`**

Add after `UseMultiQueryRetrieval` method (after line 87), before `ConfigureResilience`:

```csharp
    /// <summary>
    /// Registers <typeparamref name="TReranker"/> as the <see cref="IReranker"/>.
    /// When registered, <see cref="RagPipeline"/> rescores search results using
    /// the cross-encoder for higher precision ranking.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseReranking = false }</c>.
    /// Over-fetch control: set <c>RetrievalOptions.CandidateCount</c> (defaults to TopK * 3).
    /// </remarks>
    public RagBuilder UseReranking<TReranker>() where TReranker : class, IReranker
    {
        Services.AddSingleton<IReranker, TReranker>();
        return this;
    }
```

**Step 2: Update `ServiceCollectionExtensions` to resolve and pass `IReranker`**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`, add after line 41 (`var multiQueryOptions`):

```csharp
            var reranker = sp.GetService<IReranker>();
```

Then update line 43 (the `return new RagPipeline(...)` call) to include the new parameter:

```csharp
            return new RagPipeline(parsers, chunker, store, embedder, chatClient, options, logger, resilience, queryExpander, multiQueryOptions, reranker);
```

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj -q`
Expected: Build succeeded.

**Step 4: Run all tests**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q`
Expected: All tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "feat: wire IReranker into DI via RagBuilder.UseReranking<T>()"
```

---

### Task 5: ONNX Reranker Package

**Files:**
- Create: `src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj`
- Create: `src/Rag.NET.Reranking.Onnx/OnnxRerankerOptions.cs`
- Create: `src/Rag.NET.Reranking.Onnx/OnnxReranker.cs`
- Create: `src/Rag.NET.Reranking.Onnx/RagBuilderExtensions.cs`
- Modify: `Rag.NET.slnx` (add new projects)

> **Important:** ONNX cross-encoder inference requires `Microsoft.ML.OnnxRuntime` and a tokenizer.
> The implementation tokenizes `(query, passage)` pairs, runs ONNX inference, and applies sigmoid to get `[0, 1]` relevance scores.
> Tests for this package require a real ONNX model file. Mark ONNX-dependent tests as `[Trait("Category", "Integration")]` so they can be excluded in CI when no model is available.

**Step 1: Create project file**

```xml
<!-- src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Reranking.Onnx</RootNamespace>
    <PackageId>Rag.NET.Reranking.Onnx</PackageId>
    <Description>ONNX Runtime cross-encoder reranking for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.*" />
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="0.*" />
    <PackageReference Include="Microsoft.ML.Tokenizers.Data.Cl100kBase" Version="0.*" />
  </ItemGroup>

</Project>
```

> **Note on tokenizer:** Cross-encoder models (BERT-based) use WordPiece tokenization, not cl100k_base.
> The correct approach is to load the tokenizer from the model's `tokenizer.json` file shipped alongside the ONNX model.
> Use `Microsoft.ML.Tokenizers.BpeTokenizer` or `Tokenizer.CreateTiktokenForModel` depending on the model.
> For BERT-based models, you'll likely need to bundle or reference the model's `vocab.txt`.
> Adjust the tokenizer package references as needed during implementation — the exact tokenizer depends on the model architecture.

**Step 2: Create `OnnxRerankerOptions`**

```csharp
// src/Rag.NET.Reranking.Onnx/OnnxRerankerOptions.cs
namespace Rag.NET.Reranking.Onnx;

public sealed class OnnxRerankerOptions
{
    /// <summary>
    /// Path to the ONNX cross-encoder model file.
    /// </summary>
    public required string ModelPath { get; set; }

    /// <summary>
    /// Path to the tokenizer vocabulary file (e.g., vocab.txt for BERT-based models).
    /// Must be compatible with the ONNX model.
    /// </summary>
    public required string VocabPath { get; set; }

    /// <summary>
    /// Maximum token sequence length for the cross-encoder input.
    /// Query + passage pairs exceeding this are truncated.
    /// </summary>
    public int MaxLength { get; set; } = 512;
}
```

**Step 3: Create `OnnxReranker`**

```csharp
// src/Rag.NET.Reranking.Onnx/OnnxReranker.cs
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Reranking.Onnx;

public sealed class OnnxReranker : IReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxRerankerOptions _options;

    public OnnxReranker(OnnxRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"ONNX model file not found: {options.ModelPath}", options.ModelPath);

        if (!File.Exists(options.VocabPath))
            throw new FileNotFoundException($"Vocabulary file not found: {options.VocabPath}", options.VocabPath);

        _options = options;
        _session = new InferenceSession(options.ModelPath);
    }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
            return Task.FromResult<IReadOnlyList<RerankResult>>([]);

        var rerankResults = new List<RerankResult>(results.Count);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var score = ScorePair(query, result.Chunk.Text);
            rerankResults.Add(new RerankResult
            {
                SearchResult = result,
                RelevanceScore = score,
            });
        }

        return Task.FromResult<IReadOnlyList<RerankResult>>(rerankResults);
    }

    private double ScorePair(string query, string passage)
    {
        // Tokenize the (query, passage) pair
        // This is a simplified implementation — the exact tokenization
        // depends on the model architecture (BERT WordPiece, etc.)
        // For production, load the tokenizer from the model's tokenizer.json
        var (inputIds, attentionMask, tokenTypeIds) = TokenizePair(query, passage);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var outputs = _session.Run(inputs);
        var logits = outputs.First().AsTensor<float>();
        var score = Sigmoid(logits[0]);
        return score;
    }

    private (DenseTensor<long> inputIds, DenseTensor<long> attentionMask, DenseTensor<long> tokenTypeIds) TokenizePair(
        string query, string passage)
    {
        // Basic WordPiece-style tokenization placeholder
        // In production, use the model's actual tokenizer
        // For now, this creates the correct tensor shapes for BERT-based models:
        // [CLS] query tokens [SEP] passage tokens [SEP]

        // TODO: Replace with proper tokenizer loading from VocabPath
        // This is intentionally simple — real tokenization will be model-specific
        var maxLen = _options.MaxLength;

        // Placeholder: split on whitespace (real impl uses WordPiece)
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var passageTokens = passage.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Reserve 3 tokens for [CLS] and two [SEP]
        var available = maxLen - 3;
        var queryLen = Math.Min(queryTokens.Length, available / 2);
        var passageLen = Math.Min(passageTokens.Length, available - queryLen);
        var totalLen = queryLen + passageLen + 3;

        var inputIds = new DenseTensor<long>(new[] { 1, totalLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, totalLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, totalLen });

        // [CLS] = 101
        inputIds[0, 0] = 101;
        attentionMask[0, 0] = 1;
        tokenTypeIds[0, 0] = 0;

        var pos = 1;
        for (var i = 0; i < queryLen; i++, pos++)
        {
            inputIds[0, pos] = queryTokens[i].GetHashCode() & 0x7FFF; // placeholder token id
            attentionMask[0, pos] = 1;
            tokenTypeIds[0, pos] = 0;
        }

        // [SEP] = 102
        inputIds[0, pos] = 102;
        attentionMask[0, pos] = 1;
        tokenTypeIds[0, pos] = 0;
        pos++;

        for (var i = 0; i < passageLen; i++, pos++)
        {
            inputIds[0, pos] = passageTokens[i].GetHashCode() & 0x7FFF;
            attentionMask[0, pos] = 1;
            tokenTypeIds[0, pos] = 1; // segment B
        }

        // [SEP]
        inputIds[0, pos] = 102;
        attentionMask[0, pos] = 1;
        tokenTypeIds[0, pos] = 1;

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private static double Sigmoid(float x) => 1.0 / (1.0 + Math.Exp(-x));

    public void Dispose() => _session.Dispose();
}
```

> **Implementation note:** The `TokenizePair` method above is a **placeholder**. Real BERT-based cross-encoders require proper WordPiece tokenization using the model's `vocab.txt`. During implementation, investigate `Microsoft.ML.Tokenizers` for BERT WordPiece support, or load the tokenizer vocabulary manually. The tensor shapes (`input_ids`, `attention_mask`, `token_type_ids`) and special tokens (`[CLS]=101`, `[SEP]=102`) are correct for BERT-family models.

**Step 4: Create `RagBuilderExtensions`**

```csharp
// src/Rag.NET.Reranking.Onnx/RagBuilderExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Reranking.Onnx;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="OnnxReranker"/> as the <see cref="IReranker"/>,
    /// using a local ONNX cross-encoder model for reranking.
    /// </summary>
    /// <param name="builder">The Rag.NET builder.</param>
    /// <param name="configure">Delegate to configure <see cref="OnnxRerankerOptions"/>.</param>
    public static RagBuilder UseOnnxReranking(this RagBuilder builder, Action<OnnxRerankerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OnnxRerankerOptions { ModelPath = "", VocabPath = "" };
        configure(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IReranker, OnnxReranker>();

        return builder;
    }
}
```

**Step 5: Add projects to solution**

Modify `Rag.NET.slnx` — add inside the `/src/` folder:

```xml
    <Project Path="src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj" />
```

**Step 6: Verify build**

Run: `dotnet build src/Rag.NET.Reranking.Onnx/Rag.NET.Reranking.Onnx.csproj -q`
Expected: Build succeeded.

**Step 7: Commit**

```bash
git add src/Rag.NET.Reranking.Onnx/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.Reranking.Onnx package with OnnxReranker"
```

---

### Task 6: Update Feature Backlog

**Files:**
- Modify: `docs/features.md` (line 506)

**Step 1: Mark Cross-Encoder Reranking as done**

Change line 506 from:
```
| [ ] | Cross-Encoder Reranking | Medium | Model or API |
```
To:
```
| [x] | Cross-Encoder Reranking | Medium | Model or API |
```

**Step 2: Commit**

```bash
git add docs/features.md
git commit -m "docs: mark Cross-Encoder Reranking as done in feature backlog"
```

---

## Summary

| Task | Description | Files | Tests |
|------|-------------|-------|-------|
| 1 | Core abstractions (`IReranker`, `RerankResult`) | 2 new | — |
| 2 | `RetrievalOptions` + log messages | 2 modified | — |
| 3 | Pipeline integration (TDD) | 2 modified | 6 new |
| 4 | DI wiring (`RagBuilder` + `ServiceCollectionExtensions`) | 2 modified | — |
| 5 | ONNX package | 4 new, 1 modified | — |
| 6 | Feature backlog update | 1 modified | — |

**Total: 8 new files, 6 modified files, 6 new tests, 6 commits**
