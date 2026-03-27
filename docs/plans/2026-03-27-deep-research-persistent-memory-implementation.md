# Deep Research Loop + Persistent Conversational Memory — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `DeepResearchRetriever` (decorator over `IRetriever`) and `PersistentConversationMemory` (decorator over `IConversationMemory`) with full DI wiring and tests.

**Architecture:** `DeepResearchRetriever` wraps `PipelineRetriever` via a `WireDeepResearch` helper in `ServiceCollectionExtensions` (mirrors the existing `WireRefinementStrategy` pattern). `PersistentConversationMemory` is registered inline inside a revised `UseConversationMemory` that accepts an optional `Action<ConversationMemoryBuilder>` delegate. `IConversationMemory` gains `StoreAsync`; `ConversationMemoryPipeline` implements it as a no-op.

**Tech Stack:** .NET 9, xUnit, NSubstitute, `ZeroAlloc.Results` (`Result<T,E>`), `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator<string,Embedding<float>>`, `ChatMessage`, `ChatRole`), `System.Text.Json`.

---

## Key types and signatures

```csharp
// IRetriever.RetrieveAsync returns:
Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(string query, RetrievalOptions? options, CancellationToken ct);

// Result accessors (ZeroAlloc.Results):
result.IsSuccess   // bool
result.Value       // IReadOnlyList<SearchResult>

// SearchResult:
record SearchResult { TextChunk Chunk; double Score; }

// TextChunk:
record TextChunk { string Text; DocumentId DocumentId; int ChunkIndex; }

// DocumentId wraps a string:
new DocumentId("session-1")
documentId.Value   // string

// IVectorStore:
Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct);
Task<IReadOnlyList<SearchResult>> SearchAsync(ReadOnlyMemory<float> embedding, SearchOptions options, CancellationToken ct);

// EmbeddedChunk:
record EmbeddedChunk { TextChunk Chunk; ReadOnlyMemory<float> Embedding; }

// SearchOptions:
class SearchOptions { int TopK = 5; double MinScore = 0.0; }

// IEmbeddingGenerator.GenerateAsync:
Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options, CancellationToken ct);
// Access embedding vector: embeddings[0].Vector  (ReadOnlyMemory<float>)

// IChatClient.GetResponseAsync:
Task<ChatResponse> GetResponseAsync(IList<ChatMessage> messages, ChatOptions? options, CancellationToken ct);
// Access response text: response.Message.Text

// PipelineRetriever [Inject] properties (must be wired manually in WireDeepResearch):
Pipeline<RetrievalContext, IReadOnlyList<SearchResult>> Pipeline   // required — from Rag.NET.Pipeline
ILogger<PipelineRetriever>? Logger                                  // optional
```

---

### Task 1: `StoreAsync` on `IConversationMemory` + no-op in `ConversationMemoryPipeline`

**Files:**
- Modify: `src/Rag.NET/Abstractions/IConversationMemory.cs`
- Modify: `src/Rag.NET/Memory/ConversationMemoryPipeline.cs`
- Create: `tests/Rag.NET.Tests/Memory/ConversationMemoryStoreTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Rag.NET.Tests/Memory/ConversationMemoryStoreTests.cs
using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class ConversationMemoryStoreTests
{
    [Fact]
    public async Task StoreAsync_IsNoOp_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        IConversationMemory sut = new ConversationMemoryPipeline(new ConversationMemoryOptions(), chatClient: null);

        await sut.StoreAsync("Hello", "Hi there", "session-1", ct);
    }
}
```

**Step 2: Run test — expect compile error**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ConversationMemoryStoreTests" -v m
```

Expected: compile error — `IConversationMemory` has no `StoreAsync`.

**Step 3: Add `StoreAsync` to `IConversationMemory`**

In `src/Rag.NET/Abstractions/IConversationMemory.cs`, add after `ProcessAsync`:

```csharp
/// <summary>
/// Persists a completed exchange pair for future recall.
/// Implementations that do not support persistence return <see cref="Task.CompletedTask"/>.
/// </summary>
Task StoreAsync(
    string userMessage,
    string assistantMessage,
    string sessionId,
    CancellationToken cancellationToken = default);
```

**Step 4: Implement no-op in `ConversationMemoryPipeline`**

In `src/Rag.NET/Memory/ConversationMemoryPipeline.cs`, add:

```csharp
public Task StoreAsync(
    string userMessage,
    string assistantMessage,
    string sessionId,
    CancellationToken cancellationToken = default) => Task.CompletedTask;
```

**Step 5: Run test — expect pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ConversationMemoryStoreTests" -v m
```

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests -v m
```

Expected: all tests pass (existing tests unaffected).

**Step 7: Commit**

```bash
git add src/Rag.NET/Abstractions/IConversationMemory.cs \
        src/Rag.NET/Memory/ConversationMemoryPipeline.cs \
        tests/Rag.NET.Tests/Memory/ConversationMemoryStoreTests.cs
git commit -m "feat: add StoreAsync to IConversationMemory; no-op in ConversationMemoryPipeline"
```

---

### Task 2: `DeepResearchOptions` + `DeepResearchRetriever`

**Files:**
- Create: `src/Rag.NET/Models/Options/DeepResearchOptions.cs`
- Create: `src/Rag.NET/Retrieval/DeepResearchRetriever.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/DeepResearchRetrieverTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Retrieval/DeepResearchRetrieverTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class DeepResearchRetrieverTests
{
    private static SearchResult MakeResult(string docId, int chunkIndex, double score = 1.0) =>
        new()
        {
            Chunk = new TextChunk { Text = "text", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score,
        };

    private static Result<IReadOnlyList<SearchResult>, RagError> Ok(params SearchResult[] results) =>
        Result<IReadOnlyList<SearchResult>, RagError>.Success(results);

    private static void ReturnSufficient(IChatClient chatClient) =>
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":true,\"subQueries\":[]}")));

    private static void ReturnInsufficientThenSufficient(IChatClient chatClient, params string[] subQueries)
    {
        var list = string.Join(",", subQueries.Select(q => $"\"{q}\""));
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, $"{{\"sufficient\":false,\"subQueries\":[{list}]}}")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":true,\"subQueries\":[]}")));
    }

    [Fact]
    public async Task SufficientOnFirstPass_ReturnsChunks_NoSubQueries()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        ReturnSufficient(chatClient);

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        await inner.Received(1).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct);
    }

    [Fact]
    public async Task InsufficientThenSufficient_MergesSubQueryResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q",    Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc2", 0)));
        ReturnInsufficientThenSufficient(chatClient, "sub1");

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
    }

    [Fact]
    public async Task MaxDepthReached_StopsLoop_ReturnsAccumulatedChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("doc1", 0)));
        // Always insufficient
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":false,\"subQueries\":[\"sub1\"]}")));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions { MaxDepth = 2 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // Exactly MaxDepth sufficiency checks — loop stopped
        await chatClient.Received(2)
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuplicateChunks_Deduplicated_HighestScoreKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q",    Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0, 0.9)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0, 0.7)));
        ReturnInsufficientThenSufficient(chatClient, "sub1");

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal(0.9, result.Value[0].Score);
    }

    [Fact]
    public async Task SubQueryRetrievalThrows_LoggedAndSkipped_OtherResultsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q",    Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct).ThrowsAsync(new HttpRequestException("down"));
        ReturnInsufficientThenSufficient(chatClient, "sub1");

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value); // sub1 threw, doc1 still present
    }

    [Fact]
    public async Task MalformedLlmJson_TreatedAsSufficient_Passthrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json {{{")));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        await inner.Received(1).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct);
    }
}
```

**Step 2: Run tests — expect compile errors**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DeepResearchRetrieverTests" -v m
```

Expected: compile errors — `DeepResearchRetriever` and `DeepResearchOptions` do not exist.

**Step 3: Create `DeepResearchOptions`**

```csharp
// src/Rag.NET/Models/Options/DeepResearchOptions.cs
namespace Rag.NET.Models.Options;

public sealed class DeepResearchOptions
{
    public int MaxDepth { get; init; } = 3;
    public int SubQueryCount { get; init; } = 3;

    /// <summary>Custom prompt template. When null the built-in default is used.</summary>
    public string? SufficiencyPrompt { get; init; }
}
```

**Step 4: Create `DeepResearchRetriever`**

```csharp
// src/Rag.NET/Retrieval/DeepResearchRetriever.cs
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Retrieval;

public sealed class DeepResearchRetriever(
    IRetriever inner,
    IChatClient chatClient,
    DeepResearchOptions options,
    ILogger<DeepResearchRetriever>? logger = null) : IRetriever
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return result;

        var chunks = result.Value.ToList();

        for (int depth = 0; depth < this.options.MaxDepth; depth++)
        {
            var sufficiency = await CheckSufficiencyAsync(query, chunks, cancellationToken).ConfigureAwait(false);
            if (sufficiency.Sufficient)
                break;

            foreach (var subQuery in sufficiency.SubQueries.Take(this.options.SubQueryCount))
            {
                try
                {
                    var sub = await inner.RetrieveAsync(subQuery, options, cancellationToken).ConfigureAwait(false);
                    if (sub.IsSuccess)
                        chunks.AddRange(sub.Value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Sub-query retrieval failed for '{SubQuery}'", subQuery);
                }
            }

            chunks = chunks
                .GroupBy(r => (r.Chunk.DocumentId.Value, r.Chunk.ChunkIndex))
                .Select(g => g.MaxBy(r => r.Score)!)
                .ToList();
        }

        return Result<IReadOnlyList<SearchResult>, RagError>.Success(chunks.AsReadOnly());
    }

    private sealed record SufficiencyResponse(bool Sufficient, string[] SubQueries);

    private async Task<SufficiencyResponse> CheckSufficiencyAsync(
        string query, IList<SearchResult> chunks, CancellationToken cancellationToken)
    {
        var promptText = options.SufficiencyPrompt ?? BuildDefaultPrompt(query, chunks);
        try
        {
            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, promptText)],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                cancellationToken).ConfigureAwait(false);

            var json = response.Message.Text ?? "{}";
            return JsonSerializer.Deserialize<SufficiencyResponse>(json, _jsonOptions)
                   ?? new SufficiencyResponse(true, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return new SufficiencyResponse(true, []); }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sufficiency check failed; treating as sufficient.");
            return new SufficiencyResponse(true, []);
        }
    }

    private string BuildDefaultPrompt(string query, IList<SearchResult> chunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Query: {query}");
        sb.AppendLine("Retrieved context:");
        foreach (var r in chunks)
            sb.AppendLine($"- {r.Chunk.Text}");
        sb.AppendLine();
        sb.Append("Is the above context sufficient to answer the query? ");
        sb.AppendLine($"If not, provide up to {options.SubQueryCount} focused sub-queries.");
        sb.AppendLine("Respond with JSON only: {\"sufficient\": true, \"subQueries\": []}");
        return sb.ToString();
    }
}
```

**Step 5: Run tests — expect pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DeepResearchRetrieverTests" -v m
```

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests -v m
```

**Step 7: Commit**

```bash
git add src/Rag.NET/Models/Options/DeepResearchOptions.cs \
        src/Rag.NET/Retrieval/DeepResearchRetriever.cs \
        tests/Rag.NET.Tests/Retrieval/DeepResearchRetrieverTests.cs
git commit -m "feat: implement DeepResearchRetriever with sufficiency-gated sub-query loop"
```

---

### Task 3: `UseDeepResearch` DI wiring

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseDeepResearchTests.cs`

**Background:** ZeroAlloc registers `PipelineRetriever` with `[Singleton(As = typeof(IRetriever))]` — it is registered **only** as `IRetriever`, not by its concrete type. The `WireDeepResearch` method (called from `AddRagNet` after `configure?.Invoke(builder)`) adds `PipelineRetriever` as a concrete singleton with all `[Inject]` properties wired manually, then adds a second `IRetriever` registration pointing to `DeepResearchRetriever`. The last registration wins in .NET DI. The sentinel is `DeepResearchOptions` being present in the service collection.

`PipelineRetriever` has two `[Inject]` properties to wire manually:
- `Pipeline<RetrievalContext, IReadOnlyList<SearchResult>> Pipeline` — `GetRequiredService` (from `Rag.NET.Pipeline`)
- `ILogger<PipelineRetriever>? Logger` — `GetService`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/DependencyInjection/UseDeepResearchTests.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseDeepResearchTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseDeepResearch_IRetrieverIsDeepResearchRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseDeepResearch_DefaultOptions_MaxDepthIsThree()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        Assert.Equal(3, sp.GetRequiredService<DeepResearchOptions>().MaxDepth);
    }

    [Fact]
    public void UseDeepResearch_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch(new DeepResearchOptions { MaxDepth = 1 }))
            .BuildServiceProvider();
        Assert.Equal(1, sp.GetRequiredService<DeepResearchOptions>().MaxDepth);
    }

    [Fact]
    public void WithoutUseDeepResearch_IRetrieverIsPipelineRetriever()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.IsType<PipelineRetriever>(sp.GetRequiredService<IRetriever>());
    }
}
```

**Step 2: Run tests — expect compile error**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~UseDeepResearchTests" -v m
```

Expected: compile error — `UseDeepResearch` does not exist.

**Step 3: Add `UseDeepResearch` to `RagBuilder`**

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`, add:

```csharp
/// <summary>
/// Wraps the registered <see cref="IRetriever"/> with <see cref="DeepResearchRetriever"/>.
/// On each retrieval call, runs a sufficiency-gated loop: retrieve, ask the LLM whether the
/// result is sufficient, and if not generate focused sub-queries and retrieve again.
/// Results are merged and deduplicated across all iterations.
/// </summary>
/// <remarks>Requires <c>IChatClient</c> to be registered in DI.</remarks>
public RagBuilder UseDeepResearch(DeepResearchOptions? options = null)
{
    Services.AddSingleton(options ?? new DeepResearchOptions());
    return this;
}
```

Also add the `using` for `DeepResearchRetriever` at the top of `RagBuilder.cs` if not already present:
- `using Rag.NET.Retrieval;`

**Step 4: Add `WireDeepResearch` to `ServiceCollectionExtensions`**

In `AddRagNet`, after `WireRefinementStrategy(services);`, add:

```csharp
WireDeepResearch(services);
```

Add the private method to `ServiceCollectionExtensions`:

```csharp
private static void WireDeepResearch(IServiceCollection services)
{
    if (!services.Any(d => d.ServiceType == typeof(DeepResearchOptions)))
        return;

    // PipelineRetriever is registered only as IRetriever by ZeroAlloc ([Singleton(As = typeof(IRetriever))]).
    // Register it by its concrete type with manually-wired [Inject] properties so the decorator can wrap it.
    services.AddSingleton<PipelineRetriever>(sp => new PipelineRetriever
    {
        Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
        Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
    });

    // Replace IRetriever with the decorator (last AddSingleton<IRetriever> wins).
    services.AddSingleton<IRetriever>(sp => new DeepResearchRetriever(
        sp.GetRequiredService<PipelineRetriever>(),
        sp.GetRequiredService<IChatClient>(),
        sp.GetRequiredService<DeepResearchOptions>(),
        sp.GetService<ILogger<DeepResearchRetriever>>()));
}
```

Additional `using` directives needed in `ServiceCollectionExtensions.cs`:
- `using Rag.NET.Pipeline;`   (for `Pipeline<,>`)
- `using Rag.NET.Retrieval;`  (for `PipelineRetriever`, `DeepResearchRetriever`)
- `using Rag.NET.Models;`     (for `RetrievalContext`, `SearchResult`)

**Step 5: Run tests — expect pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~UseDeepResearchTests" -v m
```

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests -v m
```

**Step 7: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs \
        src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs \
        tests/Rag.NET.Tests/DependencyInjection/UseDeepResearchTests.cs
git commit -m "feat: add UseDeepResearch DI registration via WireDeepResearch"
```

---

### Task 4: `PersistentMemoryOptions` + `PersistentConversationMemory`

**Files:**
- Create: `src/Rag.NET/Models/Options/PersistentMemoryOptions.cs`
- Create: `src/Rag.NET/Memory/PersistentConversationMemory.cs`
- Create: `tests/Rag.NET.Tests/Memory/PersistentConversationMemoryTests.cs`

**Background:**
- `ProcessAsync`: embed the last user message → search vector store → filter by `MinScore` in memory (vector store returns all TopK results; we filter ourselves) → if any remain, prepend a `ChatRole.System` message → call inner `ProcessAsync`.
- `StoreAsync`: format exchange as `"User: {msg}\nAssistant: {reply}"` → embed → store via `IVectorStore.StoreAsync`. Use a `ConcurrentDictionary<string, int>` for per-session `ChunkIndex` tracking.
- Embedding mock pattern: see `SemanticChunkingStrategyDocumentTests.cs` for the exact `IEmbeddingGenerator` mock setup already in use.

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Memory/PersistentConversationMemoryTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class PersistentConversationMemoryTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(float[] vector)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]));
        return embedder;
    }

    private static IConversationMemory PassthroughInner()
    {
        var inner = Substitute.For<IConversationMemory>();
        inner.ProcessAsync(Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>())
             .Returns(ci => ci.Arg<IReadOnlyList<ChatMessage>>());
        return inner;
    }

    private static SearchResult MakeMatch(string text, double score = 0.9) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("s1"), ChunkIndex = 0 },
            Score = score,
        };

    [Fact]
    public async Task ProcessAsync_MatchesFound_PrependsPrefixSystemMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(new[] { MakeMatch("User: Hi\nAssistant: Hello") });

        var sut = new PersistentConversationMemory(
            PassthroughInner(), vectorStore, MockEmbedder([0.1f]), new PersistentMemoryOptions());

        var result = await sut.ProcessAsync([new ChatMessage(ChatRole.User, "Hello")], ct);

        Assert.Contains(result, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("From a previous conversation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_NoMatches_HistoryPassedThroughUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(Array.Empty<SearchResult>());
        var inner = PassthroughInner();

        var sut = new PersistentConversationMemory(
            inner, vectorStore, MockEmbedder([0.1f]), new PersistentMemoryOptions());

        var history = new[] { new ChatMessage(ChatRole.User, "Hi") };
        var result = await sut.ProcessAsync(history, ct);

        Assert.DoesNotContain(result, m => m.Role == ChatRole.System);
        await inner.Received(1).ProcessAsync(history, ct);
    }

    [Fact]
    public async Task ProcessAsync_BelowMinScore_FilteredOut_NoPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(new[] { MakeMatch("old exchange", score: 0.3) }); // below 0.7

        var sut = new PersistentConversationMemory(
            PassthroughInner(), vectorStore, MockEmbedder([0.1f]),
            new PersistentMemoryOptions { MinScore = 0.7f });

        var result = await sut.ProcessAsync([new ChatMessage(ChatRole.User, "Hi")], ct);

        Assert.DoesNotContain(result, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task StoreAsync_EmbeddsAndStoresWithCorrectTextAndSessionId()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();

        var sut = new PersistentConversationMemory(
            Substitute.For<IConversationMemory>(), vectorStore,
            MockEmbedder([0.5f]), new PersistentMemoryOptions());

        await sut.StoreAsync("Hello", "Hi there", "session-42", ct);

        await vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(chunks =>
                chunks.Count == 1 &&
                chunks[0].Chunk.Text.Contains("User: Hello",      StringComparison.Ordinal) &&
                chunks[0].Chunk.Text.Contains("Assistant: Hi there", StringComparison.Ordinal) &&
                chunks[0].Chunk.DocumentId.Value == "session-42"),
            ct);
    }
}
```

**Step 2: Run tests — expect compile errors**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~PersistentConversationMemoryTests" -v m
```

**Step 3: Create `PersistentMemoryOptions`**

```csharp
// src/Rag.NET/Models/Options/PersistentMemoryOptions.cs
namespace Rag.NET.Models.Options;

public sealed class PersistentMemoryOptions
{
    public int TopK { get; init; } = 3;
    public float MinScore { get; init; } = 0.7f;
}
```

**Step 4: Create `PersistentConversationMemory`**

```csharp
// src/Rag.NET/Memory/PersistentConversationMemory.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Memory;

public sealed class PersistentConversationMemory(
    IConversationMemory inner,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    PersistentMemoryOptions options,
    ILogger<PersistentConversationMemory>? logger = null) : IConversationMemory
{
    private readonly ConcurrentDictionary<string, int> _sessionCounters = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var query = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (!string.IsNullOrEmpty(query))
        {
            var matches = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var relevant = matches.Where(r => r.Score >= options.MinScore).ToList();
            if (relevant.Count > 0)
            {
                var prefix = "From a previous conversation:\n" +
                    string.Join("\n", relevant.Select(r => r.Chunk.Text));
                var withPrefix = new List<ChatMessage>(history.Count + 1) { new(ChatRole.System, prefix) };
                withPrefix.AddRange(history);
                return await inner.ProcessAsync(withPrefix, cancellationToken).ConfigureAwait(false);
            }
        }
        return await inner.ProcessAsync(history, cancellationToken).ConfigureAwait(false);
    }

    public async Task StoreAsync(
        string userMessage,
        string assistantMessage,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var text = $"User: {userMessage}\nAssistant: {assistantMessage}";
        try
        {
            var embeddings = await embedder
                .GenerateAsync([text], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var chunkIndex = _sessionCounters.AddOrUpdate(sessionId, 0, (_, v) => v + 1);
            var chunk = new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = text,
                    DocumentId = new DocumentId(sessionId),
                    ChunkIndex = chunkIndex,
                },
                Embedding = embeddings[0].Vector,
            };
            await vectorStore.StoreAsync([chunk], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist exchange for session '{SessionId}'", sessionId);
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        try
        {
            var embeddings = await embedder
                .GenerateAsync([query], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return await vectorStore
                .SearchAsync(embeddings[0].Vector, new SearchOptions { TopK = options.TopK }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Persistent memory search failed for query '{Query}'", query);
            return [];
        }
    }
}
```

**Step 5: Run tests — expect pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~PersistentConversationMemoryTests" -v m
```

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests -v m
```

**Step 7: Commit**

```bash
git add src/Rag.NET/Models/Options/PersistentMemoryOptions.cs \
        src/Rag.NET/Memory/PersistentConversationMemory.cs \
        tests/Rag.NET.Tests/Memory/PersistentConversationMemoryTests.cs
git commit -m "feat: implement PersistentConversationMemory with vector-backed exchange recall"
```

---

### Task 5: `ConversationMemoryBuilder` + `UseConversationMemory` update

**Files:**
- Create: `src/Rag.NET/DependencyInjection/ConversationMemoryBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs` — update `UseConversationMemory` signature
- Create: `tests/Rag.NET.Tests/DependencyInjection/UsePersistentMemoryTests.cs`

**Background:** `ConversationMemoryBuilder` is a simple value object that records whether `UsePersistentMemory` was called and with what options. `UseConversationMemory` reads this state after `configure?.Invoke(memBuilder)` and constructs the appropriate `IConversationMemory` registration.

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/DependencyInjection/UsePersistentMemoryTests.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Memory;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UsePersistentMemoryTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UsePersistentMemory_WrapsWithDecorator()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory(configure: mem => mem.UsePersistentMemory()))
            .BuildServiceProvider();

        Assert.IsType<PersistentConversationMemory>(sp.GetRequiredService<IConversationMemory>());
    }

    [Fact]
    public void UseConversationMemory_WithoutConfigure_RegistersConversationMemoryPipeline()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory())
            .BuildServiceProvider();

        Assert.IsType<ConversationMemoryPipeline>(sp.GetRequiredService<IConversationMemory>());
    }

    [Fact]
    public void UsePersistentMemory_DefaultOptions_TopKIsThree()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory(configure: mem => mem.UsePersistentMemory()))
            .BuildServiceProvider();

        Assert.Equal(3, sp.GetRequiredService<PersistentMemoryOptions>().TopK);
    }

    [Fact]
    public void UsePersistentMemory_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory(
                configure: mem => mem.UsePersistentMemory(new PersistentMemoryOptions { TopK = 5 })))
            .BuildServiceProvider();

        Assert.Equal(5, sp.GetRequiredService<PersistentMemoryOptions>().TopK);
    }
}
```

**Step 2: Run tests — expect compile errors**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~UsePersistentMemoryTests" -v m
```

Expected: compile error — `ConversationMemoryBuilder` doesn't exist; `UseConversationMemory` has no `configure` parameter.

**Step 3: Create `ConversationMemoryBuilder`**

```csharp
// src/Rag.NET/DependencyInjection/ConversationMemoryBuilder.cs
using Rag.NET.Models.Options;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Configures optional decorators over <see cref="Rag.NET.Memory.ConversationMemoryPipeline"/>.
/// Obtained via the <c>configure</c> parameter of <see cref="RagBuilder.UseConversationMemory"/>.
/// </summary>
public sealed class ConversationMemoryBuilder
{
    private bool _usePersistentMemory;
    private PersistentMemoryOptions? _persistentMemoryOptions;

    internal bool HasPersistentMemory => _usePersistentMemory;
    internal PersistentMemoryOptions PersistentMemoryOptions => _persistentMemoryOptions ?? new PersistentMemoryOptions();

    /// <summary>
    /// Wraps the conversation memory pipeline with <see cref="Rag.NET.Memory.PersistentConversationMemory"/>,
    /// which retrieves relevant past exchange pairs from the vector store and injects them as a
    /// system-message prefix before delegating to the inner pipeline.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="Rag.NET.Abstractions.IVectorStore"/> and
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> to be registered in DI.
    /// </remarks>
    public ConversationMemoryBuilder UsePersistentMemory(PersistentMemoryOptions? options = null)
    {
        _usePersistentMemory = true;
        _persistentMemoryOptions = options;
        return this;
    }
}
```

**Step 4: Update `UseConversationMemory` in `RagBuilder`**

Replace the existing `UseConversationMemory` method with:

```csharp
/// <summary>
/// Registers <see cref="ConversationMemoryPipeline"/> as the <see cref="IConversationMemory"/>.
/// Use the optional <paramref name="configure"/> delegate to wrap the pipeline with additional
/// decorators, such as <see cref="ConversationMemoryBuilder.UsePersistentMemory"/>.
/// </summary>
public RagBuilder UseConversationMemory(
    ConversationMemoryOptions? options = null,
    Action<ConversationMemoryBuilder>? configure = null)
{
    var opts = options ?? new ConversationMemoryOptions();
    Services.AddSingleton(opts);

    var memBuilder = new ConversationMemoryBuilder();
    configure?.Invoke(memBuilder);

    if (memBuilder.HasPersistentMemory)
    {
        var persistentOpts = memBuilder.PersistentMemoryOptions;
        Services.AddSingleton(persistentOpts);
        Services.AddSingleton<IConversationMemory>(sp =>
        {
            IConversationMemory pipeline = new ConversationMemoryPipeline(
                opts,
                sp.GetService<IChatClient>(),
                sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance);
            return new PersistentConversationMemory(
                pipeline,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                persistentOpts,
                sp.GetService<ILogger<PersistentConversationMemory>>());
        });
    }
    else
    {
        Services.AddSingleton<IConversationMemory>(sp =>
            new ConversationMemoryPipeline(
                opts,
                sp.GetService<IChatClient>(),
                sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance));
    }

    return this;
}
```

Add `using Rag.NET.Memory;` to `RagBuilder.cs` if not already present.

**Step 5: Run tests — expect pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~UsePersistentMemoryTests" -v m
```

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests -v m
```

**Step 7: Commit**

```bash
git add src/Rag.NET/DependencyInjection/ConversationMemoryBuilder.cs \
        src/Rag.NET/DependencyInjection/RagBuilder.cs \
        tests/Rag.NET.Tests/DependencyInjection/UsePersistentMemoryTests.cs
git commit -m "feat: add ConversationMemoryBuilder and UsePersistentMemory DI registration"
```
