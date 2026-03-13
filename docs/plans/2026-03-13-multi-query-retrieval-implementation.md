# Multi-Query Retrieval Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Improve retrieval recall by generating multiple semantic phrasings of a query via an LLM, running each against the vector store in parallel, and merging deduplicated results.

**Architecture:** New `IQueryExpander` abstraction + `LlmQueryExpander` default implementation. `RagPipeline.RetrieveAsync` resolves the expander optionally, fans out per variant, deduplicates by `(DocumentId, ChunkIndex)` keeping highest score, then trims to `TopK`. DI wired via `RagBuilder.UseMultiQueryRetrieval()`.

**Tech Stack:** `Microsoft.Extensions.AI` (`IChatClient`), `NSubstitute` + `xunit.v3` for tests — no new NuGet packages required.

---

## Codebase Context

Key files to understand before starting:

- [src/Rag.NET/Pipeline/RagPipeline.cs](../../src/Rag.NET/Pipeline/RagPipeline.cs) — `RetrieveAsync` is the method to modify; it currently embeds the query and calls `SearchAsync` (or hybrid). Uses primary constructor — optional params go at end with `= null` defaults.
- [src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs](../../src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs) — manually constructs `RagPipeline` via a factory lambda; this is where new constructor params get wired from DI.
- [src/Rag.NET/DependencyInjection/RagBuilder.cs](../../src/Rag.NET/DependencyInjection/RagBuilder.cs) — fluent builder with methods like `UseChunkingStrategy`, `ConfigureResilience`; add `UseMultiQueryRetrieval` here.
- [src/Rag.NET/Models/Options/RetrievalOptions.cs](../../src/Rag.NET/Models/Options/RetrievalOptions.cs) — add `UseMultiQuery` bool here, default `true`.
- [src/Rag.NET/Logging/RagPipelineLog.cs](../../src/Rag.NET/Logging/RagPipelineLog.cs) — source-generated `LoggerMessage`s; add one for expansion failure.
- [tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs](../../tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs) — existing unit tests; add multi-query tests here. Uses NSubstitute for `IVectorStore`, `IEmbeddingGenerator`, etc.

**Pattern for new types:**
- Public interface in `src/Rag.NET/Abstractions/`
- Internal sealed implementation in `src/Rag.NET/<FeatureName>/`
- Options class (public) in `src/Rag.NET/Models/Options/`

---

## Task 1: Add `MultiQueryOptions` and `IQueryExpander`

**Files:**
- Create: `src/Rag.NET/Models/Options/MultiQueryOptions.cs`
- Create: `src/Rag.NET/Abstractions/IQueryExpander.cs`

**Step 1: Create `MultiQueryOptions`**

```csharp
// src/Rag.NET/Models/Options/MultiQueryOptions.cs
namespace Rag.NET.Models.Options;

public sealed class MultiQueryOptions
{
    public int VariantCount { get; set; } = 3;

    public string PromptTemplate { get; set; } =
        "Generate {count} different phrasings of the following question.\n" +
        "Return only the rephrased questions, one per line, with no numbering or extra text.\n\n" +
        "Question: {query}";
}
```

**Step 2: Create `IQueryExpander`**

```csharp
// src/Rag.NET/Abstractions/IQueryExpander.cs
namespace Rag.NET.Abstractions;

/// <summary>
/// Expands a single query into multiple semantically equivalent variants
/// to broaden retrieval recall.
/// </summary>
public interface IQueryExpander
{
    /// <summary>
    /// Generates <paramref name="count"/> alternative phrasings of <paramref name="query"/>.
    /// Implementations may return fewer than <paramref name="count"/> items;
    /// callers must handle partial results.
    /// </summary>
    Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Build to confirm no compile errors**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Build succeeded, 0 warnings (TreatWarningsAsErrors is on).

**Step 4: Commit**

```bash
git add src/Rag.NET/Models/Options/MultiQueryOptions.cs src/Rag.NET/Abstractions/IQueryExpander.cs
git commit -m "feat: add MultiQueryOptions and IQueryExpander abstraction"
```

---

## Task 2: Implement `LlmQueryExpander` with tests

**Files:**
- Create: `src/Rag.NET/MultiQuery/LlmQueryExpander.cs`
- Create: `tests/Rag.NET.Tests/MultiQuery/LlmQueryExpanderTests.cs`

**Step 1: Write the failing tests first**

```csharp
// tests/Rag.NET.Tests/MultiQuery/LlmQueryExpanderTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Xunit;

namespace Rag.NET.Tests.MultiQuery;

public class LlmQueryExpanderTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    [Fact]
    public async Task ExpandAsync_ParsesLlmResponseIntoVariants()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "variant 1\nvariant 2\nvariant 3")]));

        var sut = new LlmQueryExpander(_chatClient, new MultiQueryOptions { VariantCount = 3 });

        var result = await sut.ExpandAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Equal("variant 1", result[0]);
        Assert.Equal("variant 2", result[1]);
        Assert.Equal("variant 3", result[2]);
    }

    [Fact]
    public async Task ExpandAsync_WhenLlmReturnsFewLines_ReturnsWhatItGot()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "only one variant")]));

        var sut = new LlmQueryExpander(_chatClient, new MultiQueryOptions { VariantCount = 3 });

        var result = await sut.ExpandAsync("what is rag?", 3, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("only one variant", result[0]);
    }

    [Fact]
    public async Task ExpandAsync_InterpolatesCountAndQueryIntoPrompt()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "a")]));

        var sut = new LlmQueryExpander(_chatClient, new MultiQueryOptions { VariantCount = 3 });

        await sut.ExpandAsync("test query", 3, TestContext.Current.CancellationToken);

        var prompt = capturedMessages!.Single().Text;
        Assert.Contains("3", prompt, StringComparison.Ordinal);
        Assert.Contains("test query", prompt, StringComparison.Ordinal);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~LlmQueryExpanderTests" -v minimal
```

Expected: `Error: The type or namespace name 'LlmQueryExpander' could not be found`

**Step 3: Implement `LlmQueryExpander`**

```csharp
// src/Rag.NET/MultiQuery/LlmQueryExpander.cs
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.MultiQuery;

internal sealed class LlmQueryExpander(IChatClient chatClient, MultiQueryOptions options) : IQueryExpander
{
    public async Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        var prompt = options.PromptTemplate
            .Replace("{count}", count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{query}", query, StringComparison.Ordinal);

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (response.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(count)
            .ToList();
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~LlmQueryExpanderTests" -v minimal
```

Expected: `Passed! - Failed: 0, Passed: 3`

**Step 5: Commit**

```bash
git add src/Rag.NET/MultiQuery/LlmQueryExpander.cs tests/Rag.NET.Tests/MultiQuery/LlmQueryExpanderTests.cs
git commit -m "feat: implement LlmQueryExpander with tests"
```

---

## Task 3: Add `UseMultiQuery` to `RetrievalOptions`

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`

**Step 1: Add the property**

Open `src/Rag.NET/Models/Options/RetrievalOptions.cs`. Add after `RedundancyThreshold`:

```csharp
/// <summary>
/// Set to <see langword="false"/> to skip multi-query expansion for this call,
/// even when <see cref="IQueryExpander"/> is registered in DI.
/// Has no effect when no expander is registered.
/// </summary>
public bool UseMultiQuery { get; set; } = true;
```

Full file after change:

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

    /// <summary>
    /// Set to <see langword="false"/> to skip multi-query expansion for this call,
    /// even when <see cref="IQueryExpander"/> is registered in DI.
    /// Has no effect when no expander is registered.
    /// </summary>
    public bool UseMultiQuery { get; set; } = true;
}
```

**Step 2: Build to confirm no compile errors**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs
git commit -m "feat: add UseMultiQuery option to RetrievalOptions"
```

---

## Task 4: Add expansion failure log message

**Files:**
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`

**Step 1: Add the `LoggerMessage`**

Open `src/Rag.NET/Logging/RagPipelineLog.cs` and add after `RetrieveCompleted`:

```csharp
[LoggerMessage(Level = LogLevel.Warning, Message = "Query expansion failed for query '{Query}', falling back to single-query retrieval")]
internal static partial void QueryExpansionFailed(ILogger logger, string query, Exception exception);
```

**Step 2: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add QueryExpansionFailed log message"
```

---

## Task 5: Update `RagPipeline` with multi-query fan-out and tests

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

### Step 1: Write failing tests

Add these tests to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`. First, add `using Rag.NET.Abstractions;` and `using NSubstitute.ExceptionExtensions;` to the existing usings block.

Add to the `RagPipelineTests` class:

```csharp
[Fact]
public async Task RetrieveAsync_WithMultiQueryExpander_DeduplicatesByChunkKeepingHighestScore()
{
    var expander = Substitute.For<IQueryExpander>();
    expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(["variant 1"]);

    var sharedChunk = new TextChunk { Text = "shared", DocumentId = "doc-1", ChunkIndex = 0 };
    var uniqueChunk = new TextChunk { Text = "unique", DocumentId = "doc-2", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f });

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    // First call (original query): sharedChunk at 0.9
    // Second call (variant 1): sharedChunk at 0.5 + uniqueChunk at 0.8
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(
            [new SearchResult { Chunk = sharedChunk, Score = 0.9 }],
            [new SearchResult { Chunk = sharedChunk, Score = 0.5 }, new SearchResult { Chunk = uniqueChunk, Score = 0.8 }]);

    var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

    var results = await sut.RetrieveAsync("what is rag?", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(2, results.Count);
    Assert.Equal(0.9, results[0].Score); // sharedChunk: highest score wins
    Assert.Equal(0.8, results[1].Score); // uniqueChunk
}

[Fact]
public async Task RetrieveAsync_WithUseMultiQueryFalse_SkipsExpansion()
{
    var expander = Substitute.For<IQueryExpander>();
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns([]);

    var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

    await sut.RetrieveAsync("query", new RetrievalOptions { UseMultiQuery = false }, TestContext.Current.CancellationToken);

    await expander.DidNotReceive().ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task RetrieveAsync_WhenExpanderThrows_FallsBackToSingleQuery()
{
    var expander = Substitute.For<IQueryExpander>();
    expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new HttpRequestException("LLM unreachable"));

    var chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns([new SearchResult { Chunk = chunk, Score = 0.9 }]);

    var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

    var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

    // Falls back: single query ran, returned one result
    Assert.Single(results);
    await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task RetrieveAsync_OriginalQueryAlwaysIncludedInFanOut()
{
    var expander = Substitute.For<IQueryExpander>();
    expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(["variant 1", "variant 2"]);

    var embedding = new Embedding<float>(new float[] { 0.1f });

    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns([]);

    var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

    await sut.RetrieveAsync("original", cancellationToken: TestContext.Current.CancellationToken);

    // 3 queries total: original + 2 variants
    await _vectorStore.Received(3).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
}
```

**Step 2: Add missing using**

At the top of `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`, add:

```csharp
using NSubstitute.ExceptionExtensions;
```

**Step 3: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~RagPipelineTests" -v minimal
```

Expected: Compile error — `RagPipeline` has no `queryExpander` parameter yet.

**Step 4: Update `RagPipeline`**

Open `src/Rag.NET/Pipeline/RagPipeline.cs`.

**4a. Add the new constructor parameters** (after `resiliencePipeline`):

```csharp
public sealed class RagPipeline(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatClient? chatClient,
    ChunkingOptions chunkingOptions,
    ILogger<RagPipeline>? logger = null,
    ResiliencePipeline? resiliencePipeline = null,
    IQueryExpander? queryExpander = null,
    MultiQueryOptions? multiQueryOptions = null) : IRagPipeline, IDisposable
```

**4b. Add two new field assignments** in the class body (after `_resiliencePipeline` assignment):

```csharp
private readonly IQueryExpander? _queryExpander = queryExpander;
private readonly MultiQueryOptions _multiQueryOptions = multiQueryOptions ?? new MultiQueryOptions();
```

**4c. Add the new `using`** at the top of the file:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
```

(Check which are already present; only add what's missing.)

**4d. Extract `SearchSingleQueryAsync` private method**

Add this private method to `RagPipeline` (before `Dispose`):

```csharp
private async Task<IReadOnlyList<SearchResult>> SearchSingleQueryAsync(
    string query,
    SearchOptions searchOptions,
    bool useHybridSearch,
    CancellationToken cancellationToken)
{
    var queryEmbeddings = await embeddingGenerator.GenerateAsync(
        [query], cancellationToken: cancellationToken).ConfigureAwait(false);

    if (useHybridSearch)
    {
        if (vectorStore is IHybridSearchable hybrid)
        {
            return await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        var denseTask = vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
        var bm25Hits = _bm25Index.Search(query, topK: searchOptions.TopK);
        var dense = await denseTask.ConfigureAwait(false);
        return RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
    }

    return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken)
        .ConfigureAwait(false);
}
```

**4e. Replace `RetrieveAsync` body** with the version below (same public signature, refactored internals):

```csharp
public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
    string query,
    RetrievalOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var opts = options ?? new RetrievalOptions();

    var searchOptions = new SearchOptions
    {
        TopK = opts.TopK,
        MinScore = opts.MinScore,
        MetadataFilter = opts.MetadataFilter,
        UseHybridSearch = opts.UseHybridSearch,
    };

    IReadOnlyList<SearchResult> searchResults;

    if (_queryExpander is not null && opts.UseMultiQuery)
    {
        IReadOnlyList<string> variants;
        try
        {
            variants = await _queryExpander.ExpandAsync(query, _multiQueryOptions.VariantCount, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RagPipelineLog.QueryExpansionFailed(_logger, query, ex);
            variants = [];
        }

        var allQueries = new List<string>(variants.Count + 1) { query };
        allQueries.AddRange(variants);

        var tasks = allQueries.Select(q => SearchSingleQueryAsync(q, searchOptions, opts.UseHybridSearch, cancellationToken));
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        searchResults = allResults
            .SelectMany(r => r)
            .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(opts.TopK)
            .ToList();
    }
    else
    {
        searchResults = await SearchSingleQueryAsync(query, searchOptions, opts.UseHybridSearch, cancellationToken)
            .ConfigureAwait(false);
    }

    if (opts.UseLostInTheMiddleReordering)
        searchResults = LostInTheMiddleReorderer.Reorder(searchResults);

    if (opts.UseRedundancyFilter)
        searchResults = await RedundancyFilter.FilterAsync(searchResults, embeddingGenerator, opts.RedundancyThreshold, cancellationToken)
            .ConfigureAwait(false);

    return searchResults;
}
```

**Step 5: Run all `Rag.NET.Tests` to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v minimal
```

Expected: `Passed! - Failed: 0`

**Step 6: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: implement multi-query fan-out in RagPipeline with tests"
```

---

## Task 6: Wire up DI

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Add `UseMultiQueryRetrieval` to `RagBuilder`**

Open `src/Rag.NET/DependencyInjection/RagBuilder.cs`. Add the following using at the top if not present:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
```

Add the new method to `RagBuilder`:

```csharp
/// <summary>
/// Registers <see cref="LlmQueryExpander"/> as the <see cref="IQueryExpander"/>.
/// When registered, <see cref="RagPipeline.RetrieveAsync"/> expands each query into
/// <see cref="MultiQueryOptions.VariantCount"/> alternatives, fans out to the vector store
/// in parallel, and merges deduplicated results.
/// </summary>
/// <remarks>
/// Requires <c>IChatClient</c> to be registered in DI.
/// Per-call opt-out: pass <c>new RetrievalOptions { UseMultiQuery = false }</c>.
/// </remarks>
/// <param name="configure">Optional delegate to configure <see cref="MultiQueryOptions"/>.</param>
public RagBuilder UseMultiQueryRetrieval(Action<MultiQueryOptions>? configure = null)
{
    var options = new MultiQueryOptions();
    configure?.Invoke(options);
    Services.AddSingleton(options);
    Services.AddSingleton<IQueryExpander, LlmQueryExpander>();
    return this;
}
```

**Step 2: Update `ServiceCollectionExtensions` to resolve new params**

Open `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`. Update the `IRagPipeline` factory lambda to pass the new optional dependencies:

```csharp
services.AddSingleton<IRagPipeline>(sp =>
{
    var parsers = sp.GetServices<IDocumentParser>();
    var chunker = sp.GetRequiredService<IChunkingStrategy>();
    var store = sp.GetRequiredService<IVectorStore>();
    var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    var chatClient = sp.GetService<IChatClient>();
    var options = sp.GetRequiredService<ChunkingOptions>();
    var logger = sp.GetService<ILogger<RagPipeline>>();
    var resilienceProvider = sp.GetService<ResiliencePipelineProvider<string>>();
    var resilience = resilienceProvider?.GetPipeline("rag-net");
    var queryExpander = sp.GetService<IQueryExpander>();
    var multiQueryOptions = sp.GetService<MultiQueryOptions>();

    return new RagPipeline(parsers, chunker, store, embedder, chatClient, options, logger, resilience, queryExpander, multiQueryOptions);
});
```

Add the missing using at the top if needed:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
```

**Step 3: Build the entire solution**

```bash
dotnet build Rag.NET.slnx
```

Expected: Build succeeded, 0 errors.

**Step 4: Run all unit tests**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v minimal
```

Expected: All passing.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "feat: wire multi-query retrieval into DI via RagBuilder.UseMultiQueryRetrieval()"
```

---

## Task 7: Mark feature as done in backlog

**Files:**
- Modify: `docs/features.md`

**Step 1: Update the priority table**

In `docs/features.md`, find the row:

```
| [ ] | Multi-Query Retrieval | Medium | `IChatClient` |
```

Change to:

```
| [x] | Multi-Query Retrieval | Medium | `IChatClient` |
```

**Step 2: Commit**

```bash
git add docs/features.md
git commit -m "docs: mark Multi-Query Retrieval as done in feature backlog"
```

---

## Final verification

Run the full test suite (excluding the Qdrant test which requires Docker):

```bash
dotnet test Rag.NET.slnx --filter "FullyQualifiedName!~QdrantVectorStoreTests" 2>&1 | tail -10
```

Expected: All tests pass, 0 failures.
