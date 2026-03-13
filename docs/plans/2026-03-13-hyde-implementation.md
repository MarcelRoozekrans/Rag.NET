# HyDE (Hypothetical Document Embeddings) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the query embedding with the embedding of an LLM-generated hypothetical document, improving recall for asymmetric retrieval (short queries against long documents).

**Architecture:** Public `IHypotheticalDocumentGenerator` abstraction + internal `LlmHypotheticalDocumentGenerator` implementation, following the exact same pattern as `IQueryExpander`/`LlmQueryExpander`. HyDE is applied inside `SearchSingleQueryAsync` so it composes automatically with multi-query fan-out. The original query string is always used for BM25/keyword search; only the embedding vector is derived from the hypothetical document.

**Tech Stack:** `Microsoft.Extensions.AI.IChatClient`, xunit.v3, NSubstitute, `TestContext.Current.CancellationToken`.

---

### Task 1: `IHypotheticalDocumentGenerator` abstraction + `HydeOptions`

**Files:**
- Create: `src/Rag.NET/Abstractions/IHypotheticalDocumentGenerator.cs`
- Create: `src/Rag.NET/Models/Options/HydeOptions.cs`

**Step 1: Create the interface**

`src/Rag.NET/Abstractions/IHypotheticalDocumentGenerator.cs`:
```csharp
namespace Rag.NET.Abstractions;

/// <summary>
/// Generates a hypothetical document that would ideally answer a given query.
/// The hypothetical document text is embedded and used for vector similarity search
/// in place of the query, improving recall for asymmetric retrieval (short query vs. long document).
/// </summary>
public interface IHypotheticalDocumentGenerator
{
    /// <summary>
    /// Generates a hypothetical document for <paramref name="query"/>.
    /// The returned text is embedded and used as the search vector.
    /// On failure, callers fall back to embedding the original query directly.
    /// </summary>
    Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default);
}
```

**Step 2: Create the options class**

`src/Rag.NET/Models/Options/HydeOptions.cs`:
```csharp
namespace Rag.NET.Models.Options;

public sealed class HydeOptions
{
    /// <summary>
    /// Prompt sent to the <c>IChatClient</c> to generate the hypothetical document.
    /// One placeholder is required:
    /// <list type="bullet">
    /// <item><description><c>{query}</c> — replaced with the user's query.</description></item>
    /// </list>
    /// The LLM response is used verbatim as the hypothetical document text.
    /// </summary>
    public string PromptTemplate { get; set; } =
        "Please write a short passage that directly answers the following question. " +
        "Write only the passage, no preamble or explanation.\n\n" +
        "Question: {query}";
}
```

**Step 3: Build to verify no errors**

Run: `dotnet build src/Rag.NET -q`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Abstractions/IHypotheticalDocumentGenerator.cs src/Rag.NET/Models/Options/HydeOptions.cs
git commit -m "feat: add IHypotheticalDocumentGenerator abstraction and HydeOptions"
```

---

### Task 2: `LlmHypotheticalDocumentGenerator` with tests (TDD)

**Files:**
- Create: `src/Rag.NET/HyDE/LlmHypotheticalDocumentGenerator.cs`
- Create: `tests/Rag.NET.Tests/HyDE/LlmHypotheticalDocumentGeneratorTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/HyDE/LlmHypotheticalDocumentGeneratorTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.HyDE;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.HyDE;

public class LlmHypotheticalDocumentGeneratorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    [Fact]
    public async Task GenerateAsync_ReturnsLlmResponseAsHypotheticalDocument()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Retrieval-Augmented Generation is a technique...")]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        var result = await sut.GenerateAsync("what is rag?", TestContext.Current.CancellationToken);

        Assert.Equal("Retrieval-Augmented Generation is a technique...", result);
    }

    [Fact]
    public async Task GenerateAsync_WhenLlmResponseTextIsNull_ReturnsEmptyString()
    {
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        var result = await sut.GenerateAsync("what is rag?", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task GenerateAsync_InterpolatesQueryPlaceholder()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await sut.GenerateAsync("test query", TestContext.Current.CancellationToken);

        var prompt = capturedMessages!.Single().Text;
        Assert.Contains("test query", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{query}", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_WhenQueryIsNull_ThrowsArgumentNullException()
    {
        var sut = new LlmHypotheticalDocumentGenerator(_chatClient, new HydeOptions());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.GenerateAsync(null!, TestContext.Current.CancellationToken));
    }
}
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~LlmHypotheticalDocumentGenerator" -q`
Expected: Build error — `LlmHypotheticalDocumentGenerator` does not exist yet.

**Step 3: Create the implementation**

Create directory `src/Rag.NET/HyDE/` and file `src/Rag.NET/HyDE/LlmHypotheticalDocumentGenerator.cs`:
```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.HyDE;

internal sealed class LlmHypotheticalDocumentGenerator(IChatClient chatClient, HydeOptions options) : IHypotheticalDocumentGenerator
{
    public async Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var prompt = options.PromptTemplate
            .Replace("{query}", query, StringComparison.Ordinal);

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }
}
```

**Step 4: Run tests — verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~LlmHypotheticalDocumentGenerator" -q`
Expected: 4 passed, 0 failed.

**Step 5: Commit**

```bash
git add src/Rag.NET/HyDE/LlmHypotheticalDocumentGenerator.cs tests/Rag.NET.Tests/HyDE/LlmHypotheticalDocumentGeneratorTests.cs
git commit -m "feat: implement LlmHypotheticalDocumentGenerator with tests"
```

---

### Task 3: `RetrievalOptions.UseHyde` + `RagPipelineLog.HydeGenerationFailed`

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`

**Step 1: Add `UseHyde` to `RetrievalOptions`**

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add after the `UseMultiQuery` property (line 18):

```csharp
    /// <summary>
    /// Set to <see langword="false"/> to skip HyDE (Hypothetical Document Embeddings) for this call,
    /// even when <see cref="Rag.NET.Abstractions.IHypotheticalDocumentGenerator"/> is registered in DI.
    /// Has no effect when no generator is registered.
    /// </summary>
    public bool UseHyde { get; set; } = true;
```

The full file should now look like:
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
    /// even when <see cref="Rag.NET.Abstractions.IQueryExpander"/> is registered in DI.
    /// Has no effect when no expander is registered.
    /// </summary>
    public bool UseMultiQuery { get; set; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip HyDE (Hypothetical Document Embeddings) for this call,
    /// even when <see cref="Rag.NET.Abstractions.IHypotheticalDocumentGenerator"/> is registered in DI.
    /// Has no effect when no generator is registered.
    /// </summary>
    public bool UseHyde { get; set; } = true;
}
```

**Step 2: Add `HydeGenerationFailed` log entry**

In `src/Rag.NET/Logging/RagPipelineLog.cs`, add after the `QueryExpansionFailed` entry:
```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "HyDE generation failed for query '{Query}', falling back to original query embedding")]
    internal static partial void HydeGenerationFailed(ILogger logger, string query, Exception exception);
```

**Step 3: Build to verify**

Run: `dotnet build src/Rag.NET -q`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add UseHyde option and HydeGenerationFailed log message"
```

---

### Task 4: Wire HyDE into `RagPipeline` with tests (TDD)

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the failing tests**

Add these four tests to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`. Add them at the end of the class, before the closing `}`. Also add `using Rag.NET.Abstractions;` to the using block if not already present (check — it is already there from the multi-query tests).

```csharp
    [Fact]
    public async Task RetrieveAsync_WhenHydeGeneratorRegistered_UsesHypotheticalDocForEmbedding()
    {
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();
        hydeGenerator.GenerateAsync("original query", Arg.Any<CancellationToken>())
            .Returns("hypothetical document text");

        var sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions(),
            hydeGenerator: hydeGenerator);

        string? embeddedText = null;
        _embedder.GenerateAsync(
                Arg.Do<IEnumerable<string>>(t => embeddedText = t.First()),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.RetrieveAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hypothetical document text", embeddedText);
    }

    [Fact]
    public async Task RetrieveAsync_WhenUseHydeFalse_SkipsGeneration()
    {
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();

        var sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions(),
            hydeGenerator: hydeGenerator);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.RetrieveAsync("original query", new RetrievalOptions { UseHyde = false }, TestContext.Current.CancellationToken);

        await hydeGenerator.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenHydeGeneratorThrows_FallsBackToOriginalQuery()
    {
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();
        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions(),
            hydeGenerator: hydeGenerator);

        string? embeddedText = null;
        _embedder.GenerateAsync(
                Arg.Do<IEnumerable<string>>(t => embeddedText = t.First()),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.RetrieveAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("original query", embeddedText);
    }

    [Fact]
    public async Task RetrieveAsync_WhenHydeAndMultiQueryBothActive_HydeAppliedToEachQuery()
    {
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();
        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult($"hyp: {callInfo.Arg<string>()}"));

        var queryExpander = Substitute.For<IQueryExpander>();
        queryExpander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["variant 1", "variant 2"]);

        var sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions(),
            queryExpander: queryExpander,
            hydeGenerator: hydeGenerator);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.RetrieveAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        // HyDE called once per query: original + 2 variants = 3 total
        await hydeGenerator.Received(3).GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RetrieveAsync_WhenHyde" -q`
Expected: Build error — `RagPipeline` has no `hydeGenerator` parameter yet.

**Step 3: Update `RagPipeline`**

In `src/Rag.NET/Pipeline/RagPipeline.cs`:

a) Add two constructor parameters at the end (after `multiQueryOptions`):
```csharp
    IHypotheticalDocumentGenerator? hydeGenerator = null,
    HydeOptions? hydeOptions = null) : IRagPipeline, IDisposable
```

b) Add two fields (after `_multiQueryOptions`):
```csharp
    private readonly IHypotheticalDocumentGenerator? _hydeGenerator = hydeGenerator;
    private readonly HydeOptions _hydeOptions = hydeOptions ?? new HydeOptions();
```

c) Add the missing using:
```csharp
using Rag.NET.HyDE;
```
(alongside the existing `using Rag.NET.MultiQuery;`)

d) Update the two call sites of `SearchSingleQueryAsync` in `RetrieveAsync` to pass `opts.UseHyde`:

First call site (inside the multi-query block):
```csharp
            var tasks = allQueries.Select(q => SearchSingleQueryAsync(q, searchOptions, opts.UseHybridSearch, opts.UseHyde, cancellationToken)).ToArray();
```

Second call site (single-query else branch):
```csharp
            searchResults = await SearchSingleQueryAsync(query, searchOptions, opts.UseHybridSearch, opts.UseHyde, cancellationToken)
                .ConfigureAwait(false);
```

e) Update `SearchSingleQueryAsync` signature and body:

Replace the existing method signature and the embedding line:
```csharp
    private async Task<IReadOnlyList<SearchResult>> SearchSingleQueryAsync(
        string query,
        SearchOptions searchOptions,
        bool useHybridSearch,
        bool useHyde,
        CancellationToken cancellationToken)
    {
        var textToEmbed = query;

        if (useHyde && _hydeGenerator is not null)
        {
            try
            {
                textToEmbed = await _hydeGenerator.GenerateAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RagPipelineLog.HydeGenerationFailed(_logger, query, ex);
            }
        }

        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [textToEmbed], cancellationToken: cancellationToken).ConfigureAwait(false);

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

Note: `query` (not `textToEmbed`) is passed to `HybridSearchAsync` and `_bm25Index.Search` — keyword search always uses the original query.

**Step 4: Run tests — verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RetrieveAsync_WhenHyde" -q`
Expected: 4 passed, 0 failed.

**Step 5: Run the full test suite**

Run: `dotnet test tests/Rag.NET.Tests -q`
Expected: All tests pass, 0 failed.

**Step 6: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: wire HyDE into RagPipeline.SearchSingleQueryAsync with tests"
```

---

### Task 5: DI wiring — `RagBuilder.UseHyde()` + `ServiceCollectionExtensions`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Add `UseHyde` to `RagBuilder`**

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`:

Add using at the top:
```csharp
using Rag.NET.HyDE;
```

Add this method after `UseMultiQueryRetrieval`:
```csharp
    /// <summary>
    /// Registers <see cref="LlmHypotheticalDocumentGenerator"/> as the <see cref="IHypotheticalDocumentGenerator"/>.
    /// When registered, <see cref="RagPipeline"/> embeds a hypothetical document generated by the LLM
    /// instead of the raw query, improving recall for asymmetric retrieval (short query vs. long document).
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseHyde = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="HydeOptions"/>.</param>
    public RagBuilder UseHyde(Action<HydeOptions>? configure = null)
    {
        var options = new HydeOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton<IHypotheticalDocumentGenerator, LlmHypotheticalDocumentGenerator>();
        return this;
    }
```

**Step 2: Update `ServiceCollectionExtensions`**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`, inside the `AddSingleton<IRagPipeline>` factory lambda, add two lines after `var multiQueryOptions = sp.GetService<MultiQueryOptions>();`:

```csharp
            var hydeGenerator = sp.GetService<IHypotheticalDocumentGenerator>();
            var hydeOptions = sp.GetService<HydeOptions>();

            return new RagPipeline(parsers, chunker, store, embedder, chatClient, options, logger, resilience, queryExpander, multiQueryOptions, hydeGenerator, hydeOptions);
```

Also add the missing using at the top of `ServiceCollectionExtensions.cs`:
```csharp
using Rag.NET.HyDE;
```

**Step 3: Build the full solution**

Run: `dotnet build -q`
Expected: Build succeeded, 0 errors.

**Step 4: Run all tests**

Run: `dotnet test -q`
Expected: All test projects pass, 0 failed.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "feat: wire HyDE into DI via RagBuilder.UseHyde()"
```

---

### Task 6: Mark HyDE as done in feature backlog

**Files:**
- Modify: `docs/features.md`

**Step 1: Update the priority table**

In `docs/features.md`, find this line:
```
| [ ] | HyDE | Medium | `IChatClient` |
```

Change it to:
```
| [x] | HyDE | Medium | `IChatClient` |
```

**Step 2: Update the HyDE feature description**

In the `### Hypothetical Document Embeddings (HyDE)` section (line ~82), no content change needed — the backlog description already matches the implementation.

**Step 3: Commit**

```bash
git add docs/features.md
git commit -m "docs: mark HyDE as done in feature backlog"
```
