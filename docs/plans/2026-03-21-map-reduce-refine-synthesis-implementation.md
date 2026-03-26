# Map-Reduce + Refine Synthesis Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `MapReduceAnswerEngine` and `RefineAnswerEngine` as per-call synthesis strategies, selected via `RagOptions.SynthesisStrategy`, with a `DispatchingAnswerEngine` routing to all three engines.

**Architecture:** `SynthesisStrategy` enum added to `RagOptions`; `DispatchingAnswerEngine` registered in DI replacing the direct `ChatAnswerEngine` construction; Map-Reduce runs parallel map calls then one reduce; Refine runs sequential initial + refine calls. Both implement `AskStreamingAsync` via `AskAsync` (buffered, non-streaming by design).

**Tech Stack:** C# 13, `Microsoft.Extensions.AI` (`IChatClient`, `ChatOptions`), `NSubstitute` for mocks, `xUnit` with `TestContext.Current.CancellationToken`, `Microsoft.Extensions.Logging` (`[LoggerMessage]`)

---

### Task 1: Add Options Models + Update `RagOptions`

**Files:**
- Create: `src/Rag.NET/Models/Options/SynthesisStrategy.cs`
- Create: `src/Rag.NET/Models/Options/MapReduceOptions.cs`
- Create: `src/Rag.NET/Models/Options/RefineOptions.cs`
- Modify: `src/Rag.NET/Models/Options/RagOptions.cs`

**Step 1: Write the failing test**

In `tests/Rag.NET.Tests/Models/Options/RagOptionsTests.cs` (new file):

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models.Options;

public class RagOptionsTests
{
    [Fact]
    public void SynthesisStrategy_DefaultsToDefault()
    {
        var opts = new RagOptions();
        Assert.Equal(SynthesisStrategy.Default, opts.SynthesisStrategy);
    }

    [Fact]
    public void MapReduceOptions_DefaultConcurrencyIsFive()
    {
        var opts = new MapReduceOptions();
        Assert.Equal(5, opts.MapConcurrency);
    }

    [Fact]
    public void MapReduceOptions_NullTemplatesByDefault()
    {
        var opts = new MapReduceOptions();
        Assert.Null(opts.MapPromptTemplate);
        Assert.Null(opts.ReducePromptTemplate);
    }

    [Fact]
    public void RefineOptions_NullTemplatesByDefault()
    {
        var opts = new RefineOptions();
        Assert.Null(opts.InitialPromptTemplate);
        Assert.Null(opts.RefinePromptTemplate);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagOptionsTests" --no-build
```

Expected: compile error — `SynthesisStrategy`, `MapReduceOptions`, `RefineOptions` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/Models/Options/SynthesisStrategy.cs`:
```csharp
namespace Rag.NET.Models.Options;

public enum SynthesisStrategy { Default, MapReduce, Refine }
```

`src/Rag.NET/Models/Options/MapReduceOptions.cs`:
```csharp
namespace Rag.NET.Models.Options;

public sealed class MapReduceOptions
{
    public int MapConcurrency { get; init; } = 5;
    public string? MapPromptTemplate { get; init; }
    public string? ReducePromptTemplate { get; init; }
}
```

`src/Rag.NET/Models/Options/RefineOptions.cs`:
```csharp
namespace Rag.NET.Models.Options;

public sealed class RefineOptions
{
    public string? InitialPromptTemplate { get; init; }
    public string? RefinePromptTemplate { get; init; }
}
```

Add to `RagOptions.cs` (below `ConversationHistory`):
```csharp
    public SynthesisStrategy SynthesisStrategy { get; set; } = SynthesisStrategy.Default;
    public MapReduceOptions? MapReduceOptions { get; set; }
    public RefineOptions? RefineOptions { get; set; }
```

**Step 4: Run test to verify it passes**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagOptionsTests" --no-build
```

Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/Models/Options/SynthesisStrategy.cs \
        src/Rag.NET/Models/Options/MapReduceOptions.cs \
        src/Rag.NET/Models/Options/RefineOptions.cs \
        src/Rag.NET/Models/Options/RagOptions.cs \
        tests/Rag.NET.Tests/Models/Options/RagOptionsTests.cs
git commit -m "feat: add SynthesisStrategy enum, MapReduceOptions, RefineOptions to RagOptions"
```

---

### Task 2: Implement `MapReduceAnswerEngine`

**Files:**
- Create: `src/Rag.NET/AnswerGeneration/MapReduceAnswerEngine.cs`
- Create: `tests/Rag.NET.Tests/AnswerGeneration/MapReduceAnswerEngineTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/AnswerGeneration/MapReduceAnswerEngineTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class MapReduceAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly MapReduceAnswerEngine _sut;

    public MapReduceAnswerEngineTests()
    {
        _sut = new MapReduceAnswerEngine(_chatClient, NullLogger<MapReduceAnswerEngine>.Instance);
    }

    private static SearchResult MakeSource(string text, string docId = "doc-1") =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = 0.9 };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public async Task AskAsync_ThreeSources_MapsEachThenReduces()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
            MakeSource("chunk C", "doc-3"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("partial"), ChatReply("partial"), ChatReply("final answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(4).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_OneSourceReturnsNotFound_FilteredBeforeReduce()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        // First map returns "not found", second returns a partial answer
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("not found"), ChatReply("partial answer"), ChatReply("final"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        // 2 map calls + 1 reduce = 3 total
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final", result.Answer);
    }

    [Fact]
    public async Task AskAsync_AllSourcesReturnNotFound_ReduceCalledWithEmptyPartials()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A"),
            MakeSource("chunk B"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("not found"), ChatReply("  NOT FOUND  "), ChatReply("no information available"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        // 2 map + 1 reduce = 3 calls; reduce receives empty partials
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("no information available", result.Answer);
    }

    [Fact]
    public async Task AskAsync_MapCallThrows_ChunkSkippedAndWarningLogged()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                x => throw new InvalidOperationException("LLM error"),
                x => ChatReply("partial answer"),
                x => ChatReply("final answer"));

        // Should not throw — failed chunk treated as "not found"
        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("final answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(x => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenSingleTextDelta()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("map answer"), ChatReply("final answer"));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("final answer", updates[1].TextDelta);
        Assert.Null(updates[1].Sources);
    }

    [Fact]
    public async Task AskAsync_WithCustomPromptTemplates_UsesCustomTemplates()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        var opts = new RagOptions
        {
            MapReduceOptions = new MapReduceOptions
            {
                MapPromptTemplate = "Custom map: {chunk} Q: {query}",
                ReducePromptTemplate = "Custom reduce: {partials} Q: {query}",
            }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final"));

        // Should not throw; custom templates accepted
        var result = await _sut.AskAsync("my question", sources, opts, TestContext.Current.CancellationToken);

        Assert.Equal("final", result.Answer);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~MapReduceAnswerEngineTests" --no-build
```

Expected: compile error — `MapReduceAnswerEngine` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/AnswerGeneration/MapReduceAnswerEngine.cs`:

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Executes one LLM call per source chunk in parallel (map), filters "not found" responses,
/// then combines surviving partials in a single reduce call.
/// </summary>
public sealed class MapReduceAnswerEngine(IChatClient chatClient, ILogger<MapReduceAnswerEngine> logger) : IAnswerEngine
{
    private const string DefaultMapPrompt =
        "Using only the following text, answer this question as best you can.\n" +
        "If the text doesn't contain relevant information, say \"not found\".\n\n" +
        "Text:\n{chunk}\n\nQuestion: {query}";

    private const string DefaultReducePrompt =
        "Synthesize the following partial answers into a single coherent response.\n" +
        "Discard redundant or contradictory information.\n\n" +
        "Partial answers:\n{partials}\n\nQuestion: {query}";

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();
        var mrOpts = opts.MapReduceOptions ?? new MapReduceOptions();
        var chatOptions = BuildChatOptions(opts);

        var mapPrompt = mrOpts.MapPromptTemplate ?? DefaultMapPrompt;
        var reducePrompt = mrOpts.ReducePromptTemplate ?? DefaultReducePrompt;

        // Map step — parallel, bounded by MapConcurrency
        using var semaphore = new SemaphoreSlim(mrOpts.MapConcurrency);
        var mapTasks = sources.Select(source => MapOneAsync(source, query, mapPrompt, chatOptions, semaphore, cancellationToken));
        var mapResults = await Task.WhenAll(mapTasks).ConfigureAwait(false);

        var partials = mapResults
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r) &&
                        !r.Trim().Equals("not found", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Reduce step
        var reduceText = reducePrompt
            .Replace("{partials}", string.Join("\n\n---\n\n", partials!))
            .Replace("{query}", query);

        var reduceMessages = BuildMessages(reduceText, opts);
        var reduceResponse = await chatClient.GetResponseAsync(reduceMessages, chatOptions, cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = reduceResponse.Text ?? string.Empty,
            Sources = sources,
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new RagStreamingUpdate { Sources = sources };

        var response = await AskAsync(query, sources, options, cancellationToken).ConfigureAwait(false);
        yield return new RagStreamingUpdate { TextDelta = response.Answer };
    }

    private async Task<string?> MapOneAsync(
        SearchResult source,
        string query,
        string mapPromptTemplate,
        ChatOptions chatOptions,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prompt = mapPromptTemplate
                .Replace("{chunk}", source.Chunk.Text)
                .Replace("{query}", query);

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.MapReduceMapFailed(logger, source.Chunk.DocumentId.ToString(), ex);
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static List<ChatMessage> BuildMessages(string userText, RagOptions opts)
    {
        var messages = new List<ChatMessage>();
        if (opts.ConversationHistory is { Count: > 0 })
            messages.AddRange(opts.ConversationHistory);
        messages.Add(new ChatMessage(ChatRole.User, userText));
        return messages;
    }

    private static ChatOptions BuildChatOptions(RagOptions opts)
    {
        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
            chatOptions.Temperature = opts.Temperature.Value;
        return chatOptions;
    }
}
```

Add two log entries to `src/Rag.NET/Logging/RagPipelineLog.cs` (after `SelfQueryFailed`):

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Map-reduce map call failed for document '{DocumentId}', treating as not found")]
    internal static partial void MapReduceMapFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refine call failed for document '{DocumentId}', preserving previous answer")]
    internal static partial void RefineStepFailed(ILogger logger, string documentId, Exception exception);
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~MapReduceAnswerEngineTests" --no-build
```

Expected: PASS (7 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/MapReduceAnswerEngine.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/AnswerGeneration/MapReduceAnswerEngineTests.cs
git commit -m "feat: implement MapReduceAnswerEngine with parallel map + reduce"
```

---

### Task 3: Implement `RefineAnswerEngine`

**Files:**
- Create: `src/Rag.NET/AnswerGeneration/RefineAnswerEngine.cs`
- Create: `tests/Rag.NET.Tests/AnswerGeneration/RefineAnswerEngineTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/AnswerGeneration/RefineAnswerEngineTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class RefineAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly RefineAnswerEngine _sut;

    public RefineAnswerEngineTests()
    {
        _sut = new RefineAnswerEngine(_chatClient, NullLogger<RefineAnswerEngine>.Instance);
    }

    private static SearchResult MakeSource(string text, string docId = "doc-1") =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = 0.9 };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public async Task AskAsync_ThreeSources_InitialPlusTwoRefines()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
            MakeSource("chunk C", "doc-3"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined once"), ChatReply("final answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_OneSource_OnlyInitialCall()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("only answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("only answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_RefinementCallThrows_PreviousAnswerPreserved()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                x => ChatReply("initial answer"),
                x => throw new InvalidOperationException("LLM error"));

        // Should not throw — preserve previous answer
        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("initial answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_InitialCallThrows_PropagatesException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new InvalidOperationException("LLM error"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AskAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenSingleTextDelta()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined"));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("refined", updates[1].TextDelta);
        Assert.Null(updates[1].Sources);
    }

    [Fact]
    public async Task AskAsync_WithCustomPromptTemplates_UsesCustomTemplates()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };
        var opts = new RagOptions
        {
            RefineOptions = new RefineOptions
            {
                InitialPromptTemplate = "Initial: {chunk} Q: {query}",
                RefinePromptTemplate = "Refine: {answer} + {chunk} Q: {query}",
            }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined"));

        var result = await _sut.AskAsync("my question", sources, opts, TestContext.Current.CancellationToken);

        Assert.Equal("refined", result.Answer);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RefineAnswerEngineTests" --no-build
```

Expected: compile error — `RefineAnswerEngine` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/AnswerGeneration/RefineAnswerEngine.cs`:

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Generates an initial answer from the first source chunk, then iteratively refines it
/// with each subsequent chunk. Sequential by design.
/// </summary>
public sealed class RefineAnswerEngine(IChatClient chatClient, ILogger<RefineAnswerEngine> logger) : IAnswerEngine
{
    private const string DefaultInitialPrompt =
        "Answer this question using only the following context.\n\n" +
        "Context:\n{chunk}\n\nQuestion: {query}";

    private const string DefaultRefinePrompt =
        "Given the existing answer below and new context, refine the answer if the new\n" +
        "context adds useful information. If it adds nothing new, return the existing\n" +
        "answer unchanged.\n\n" +
        "Existing answer: {answer}\n\nNew context:\n{chunk}\n\nQuestion: {query}";

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();
        var refineOpts = opts.RefineOptions ?? new RefineOptions();
        var chatOptions = BuildChatOptions(opts);

        var initialPrompt = refineOpts.InitialPromptTemplate ?? DefaultInitialPrompt;
        var refinePrompt = refineOpts.RefinePromptTemplate ?? DefaultRefinePrompt;

        // Initial call on first chunk — always propagates on failure
        var firstChunk = sources[0];
        var initialText = initialPrompt
            .Replace("{chunk}", firstChunk.Chunk.Text)
            .Replace("{query}", query);

        var initialMessages = BuildMessages(initialText, opts);
        var initialResponse = await chatClient.GetResponseAsync(initialMessages, chatOptions, cancellationToken).ConfigureAwait(false);
        var currentAnswer = initialResponse.Text ?? string.Empty;

        // Refine with remaining chunks sequentially
        for (var i = 1; i < sources.Count; i++)
        {
            var source = sources[i];
            try
            {
                var refineText = refinePrompt
                    .Replace("{answer}", currentAnswer)
                    .Replace("{chunk}", source.Chunk.Text)
                    .Replace("{query}", query);

                var refineMessages = BuildMessages(refineText, opts);
                var refineResponse = await chatClient.GetResponseAsync(refineMessages, chatOptions, cancellationToken).ConfigureAwait(false);
                currentAnswer = refineResponse.Text ?? currentAnswer;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RagPipelineLog.RefineStepFailed(logger, source.Chunk.DocumentId.ToString(), ex);
            }
        }

        return new RagResponse
        {
            Answer = currentAnswer,
            Sources = sources,
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new RagStreamingUpdate { Sources = sources };

        var response = await AskAsync(query, sources, options, cancellationToken).ConfigureAwait(false);
        yield return new RagStreamingUpdate { TextDelta = response.Answer };
    }

    private static List<ChatMessage> BuildMessages(string userText, RagOptions opts)
    {
        var messages = new List<ChatMessage>();
        if (opts.ConversationHistory is { Count: > 0 })
            messages.AddRange(opts.ConversationHistory);
        messages.Add(new ChatMessage(ChatRole.User, userText));
        return messages;
    }

    private static ChatOptions BuildChatOptions(RagOptions opts)
    {
        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
            chatOptions.Temperature = opts.Temperature.Value;
        return chatOptions;
    }
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RefineAnswerEngineTests" --no-build
```

Expected: PASS (7 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/RefineAnswerEngine.cs \
        tests/Rag.NET.Tests/AnswerGeneration/RefineAnswerEngineTests.cs
git commit -m "feat: implement RefineAnswerEngine with sequential initial + refine calls"
```

---

### Task 4: Implement `DispatchingAnswerEngine` + Update DI

**Files:**
- Create: `src/Rag.NET/AnswerGeneration/DispatchingAnswerEngine.cs`
- Create: `tests/Rag.NET.Tests/AnswerGeneration/DispatchingAnswerEngineTests.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/AnswerGeneration/DispatchingAnswerEngineTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class DispatchingAnswerEngineTests
{
    private readonly IAnswerEngine _chatEngine = Substitute.For<IAnswerEngine>();
    private readonly IAnswerEngine _mapReduceEngine = Substitute.For<IAnswerEngine>();
    private readonly IAnswerEngine _refineEngine = Substitute.For<IAnswerEngine>();
    private readonly DispatchingAnswerEngine _sut;

    public DispatchingAnswerEngineTests()
    {
        _sut = new DispatchingAnswerEngine(_chatEngine, _mapReduceEngine, _refineEngine);
    }

    private static IReadOnlyList<SearchResult> EmptySources() => Array.Empty<SearchResult>();

    private static RagResponse ReplyWith(string text) =>
        new() { Answer = text, Sources = EmptySources() };

    [Fact]
    public async Task AskAsync_DefaultStrategy_DelegatesToChatEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.Default };
        _chatEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("chat answer"));

        var result = await _sut.AskAsync("q", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.Equal("chat answer", result.Answer);
        await _chatEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _mapReduceEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _refineEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_NullOptions_DelegatesToChatEngine()
    {
        _chatEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("chat answer"));

        var result = await _sut.AskAsync("q", EmptySources(), null, TestContext.Current.CancellationToken);

        Assert.Equal("chat answer", result.Answer);
        await _chatEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_MapReduceStrategy_DelegatesToMapReduceEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.MapReduce };
        _mapReduceEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("mapreduce answer"));

        var result = await _sut.AskAsync("q", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.Equal("mapreduce answer", result.Answer);
        await _mapReduceEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _chatEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_RefineStrategy_DelegatesToRefineEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.Refine };
        _refineEngine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ReplyWith("refine answer"));

        var result = await _sut.AskAsync("q", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.Equal("refine answer", result.Answer);
        await _refineEngine.Received(1).AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
        await _chatEngine.DidNotReceive().AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskStreamingAsync_MapReduceStrategy_DelegatesToMapReduceEngine()
    {
        var opts = new RagOptions { SynthesisStrategy = SynthesisStrategy.MapReduce };
        var sources = EmptySources();
        var updates = new List<RagStreamingUpdate>
        {
            new() { Sources = sources },
            new() { TextDelta = "mapreduce result" },
        };

        _mapReduceEngine.AskStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var received = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, opts, TestContext.Current.CancellationToken))
            received.Add(update);

        Assert.Equal(2, received.Count);
        Assert.Equal("mapreduce result", received[1].TextDelta);
    }

    [Fact]
    public async Task AskStreamingAsync_DefaultStrategy_DelegatesToChatEngine()
    {
        var sources = EmptySources();
        var updates = new List<RagStreamingUpdate>
        {
            new() { Sources = sources },
            new() { TextDelta = "chat result" },
        };

        _chatEngine.AskStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var received = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, null, TestContext.Current.CancellationToken))
            received.Add(update);

        Assert.Equal(2, received.Count);
        Assert.Equal("chat result", received[1].TextDelta);
    }
}
```

> **Note:** `ToAsyncEnumerable()` is a LINQ extension in .NET 10 for `IEnumerable<T>` — no helper needed.

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DispatchingAnswerEngineTests" --no-build
```

Expected: compile error — `DispatchingAnswerEngine` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/AnswerGeneration/DispatchingAnswerEngine.cs`:

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Routes answer generation to the appropriate engine based on <see cref="RagOptions.SynthesisStrategy"/>.
/// </summary>
public sealed class DispatchingAnswerEngine(
    IAnswerEngine chatEngine,
    IAnswerEngine mapReduceEngine,
    IAnswerEngine refineEngine) : IAnswerEngine
{
    public Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var engine = Select(options);
        return engine.AskAsync(query, sources, options, cancellationToken);
    }

    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var engine = Select(options);
        return engine.AskStreamingAsync(query, sources, options, cancellationToken);
    }

    private IAnswerEngine Select(RagOptions? options) =>
        (options?.SynthesisStrategy ?? SynthesisStrategy.Default) switch
        {
            SynthesisStrategy.MapReduce => mapReduceEngine,
            SynthesisStrategy.Refine    => refineEngine,
            _                           => chatEngine,
        };
}
```

Update `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` — replace the `answerEngine` construction lines:

**Before:**
```csharp
        services.AddSingleton<IRagPipeline>(sp =>
        {
            var r = sp.GetRequiredService<IRetriever>();
            var i = sp.GetRequiredService<IIngestor>();
            var chatClient = sp.GetService<IChatClient>();
            IAnswerEngine? answerEngine = chatClient is not null ? new ChatAnswerEngine(chatClient) : null;
            return new RagPipeline(r, i, answerEngine);
        });
```

**After:**
```csharp
        services.AddSingleton<IRagPipeline>(sp =>
        {
            var r = sp.GetRequiredService<IRetriever>();
            var i = sp.GetRequiredService<IIngestor>();
            var chatClient = sp.GetService<IChatClient>();
            IAnswerEngine? answerEngine = null;
            if (chatClient is not null)
            {
                var chatEngine = new ChatAnswerEngine(chatClient);
                var mapReduceEngine = new MapReduceAnswerEngine(chatClient,
                    sp.GetRequiredService<ILogger<MapReduceAnswerEngine>>());
                var refineEngine = new RefineAnswerEngine(chatClient,
                    sp.GetRequiredService<ILogger<RefineAnswerEngine>>());
                answerEngine = new DispatchingAnswerEngine(chatEngine, mapReduceEngine, refineEngine);
            }
            return new RagPipeline(r, i, answerEngine);
        });
```

Also add missing using at the top of `ServiceCollectionExtensions.cs`:
```csharp
using Microsoft.Extensions.Logging;
```

**Step 4: Run all tests**

```
dotnet test tests/Rag.NET.Tests --no-build
```

Expected: all tests pass. Then run the full suite:

```
dotnet test --no-build
```

Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/DispatchingAnswerEngine.cs \
        src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs \
        tests/Rag.NET.Tests/AnswerGeneration/DispatchingAnswerEngineTests.cs
git commit -m "feat: add DispatchingAnswerEngine; wire MapReduce and Refine into DI"
```

---

## Final Verification

Run the full test suite one last time:

```
dotnet test
```

All tests must pass. Then use `superpowers:finishing-a-development-branch` to complete the branch.
