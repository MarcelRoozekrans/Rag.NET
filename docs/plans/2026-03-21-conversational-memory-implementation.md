# Conversational Memory Management Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add automatic conversation history management with composable strategies: sliding window, token-budget truncation, and LLM summary compression.

**Architecture:** New `IConversationMemory` abstraction called inside answer engines before building LLM messages. `ConversationMemoryPipeline` applies three strategies in order: window → budget → summary. Stateless — the caller persists history as today; the library only transforms what it receives.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI` (`IChatClient`), `Microsoft.ML.Tokenizers` (Tiktoken), xunit.v3, NSubstitute

---

### Task 1: `IConversationMemory` abstraction + `ConversationMemoryOptions`

**Files:**
- Create: `src/Rag.NET/Abstractions/IConversationMemory.cs`
- Create: `src/Rag.NET/Models/Options/ConversationMemoryOptions.cs`
- Test: `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs` (scaffold)

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs`:

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class ConversationMemoryTests
{
    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var opts = new ConversationMemoryOptions();
        Assert.Null(opts.MaxExchanges);
        Assert.Null(opts.MaxTokens);
        Assert.False(opts.UseSummary);
        Assert.Null(opts.SummaryPromptTemplate);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests --filter "ConversationMemoryTests"
```

**Step 3: Create the interface and options**

`src/Rag.NET/Abstractions/IConversationMemory.cs`:
```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Abstractions;

public interface IConversationMemory
{
    Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
```

`src/Rag.NET/Models/Options/ConversationMemoryOptions.cs`:
```csharp
namespace Rag.NET.Models.Options;

public sealed class ConversationMemoryOptions
{
    /// <summary>
    /// Maximum number of user/assistant exchange pairs to keep.
    /// Oldest exchanges removed first. System messages always preserved.
    /// Null = no window limit. Applied first.
    /// </summary>
    public int? MaxExchanges { get; init; }

    /// <summary>
    /// Maximum token budget for conversation history.
    /// Uses cl100k_base tokenizer. Oldest non-system messages trimmed
    /// until within budget. Null = no token limit. Applied second.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// When true, messages trimmed by window or token budget are
    /// LLM-summarized into a system message prefix instead of discarded.
    /// Requires IChatClient in DI. Applied last. Default false.
    /// </summary>
    public bool UseSummary { get; init; } = false;

    /// <summary>
    /// Custom prompt for the summary LLM call. Default asks for a
    /// concise summary of the conversation so far.
    /// </summary>
    public string? SummaryPromptTemplate { get; init; }
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "ConversationMemoryTests"
```
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/Abstractions/IConversationMemory.cs \
        src/Rag.NET/Models/Options/ConversationMemoryOptions.cs \
        tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs
git commit -m "feat: add IConversationMemory abstraction and ConversationMemoryOptions"
```

---

### Task 2: Sliding window strategy

**Files:**
- Create: `src/Rag.NET/Memory/ConversationMemoryPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs`

**Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models.Options;
using Xunit;

// Add these tests to the existing class:

private static List<ChatMessage> MakeExchanges(int count)
{
    var messages = new List<ChatMessage>();
    for (int i = 0; i < count; i++)
    {
        messages.Add(new ChatMessage(ChatRole.User, $"Question {i + 1}"));
        messages.Add(new ChatMessage(ChatRole.Assistant, $"Answer {i + 1}"));
    }
    return messages;
}

[Fact]
public async Task SlidingWindow_KeepsLastNExchanges()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new ConversationMemoryOptions { MaxExchanges = 2 };
    var sut = new ConversationMemoryPipeline(opts, chatClient: null);
    var history = MakeExchanges(5); // 10 messages

    var result = await sut.ProcessAsync(history, ct);

    Assert.Equal(4, result.Count); // 2 exchanges = 4 messages
    Assert.Contains("Question 4", result[0].Text);
    Assert.Contains("Answer 5", result[^1].Text);
}

[Fact]
public async Task SlidingWindow_PreservesSystemMessages()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new ConversationMemoryOptions { MaxExchanges = 1 };
    var sut = new ConversationMemoryPipeline(opts, chatClient: null);
    var history = new List<ChatMessage>
    {
        new(ChatRole.System, "You are helpful."),
        new(ChatRole.User, "Q1"), new(ChatRole.Assistant, "A1"),
        new(ChatRole.User, "Q2"), new(ChatRole.Assistant, "A2"),
    };

    var result = await sut.ProcessAsync(history, ct);

    Assert.Equal(3, result.Count); // system + last exchange
    Assert.Equal(ChatRole.System, result[0].Role);
    Assert.Contains("Q2", result[1].Text);
}

[Fact]
public async Task NoStrategiesConfigured_ReturnsUnchanged()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new ConversationMemoryOptions(); // all null/false
    var sut = new ConversationMemoryPipeline(opts, chatClient: null);
    var history = MakeExchanges(3);

    var result = await sut.ProcessAsync(history, ct);

    Assert.Equal(history.Count, result.Count);
}

[Fact]
public async Task EmptyHistory_ReturnsEmpty()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new ConversationMemoryOptions { MaxExchanges = 2 };
    var sut = new ConversationMemoryPipeline(opts, chatClient: null);

    var result = await sut.ProcessAsync([], ct);

    Assert.Empty(result);
}
```

**Step 2: Run tests to verify they fail**

**Step 3: Create `ConversationMemoryPipeline` with sliding window**

`src/Rag.NET/Memory/ConversationMemoryPipeline.cs`:

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Memory;

public sealed class ConversationMemoryPipeline(
    ConversationMemoryOptions options,
    IChatClient? chatClient) : IConversationMemory
{
    public async Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (history.Count == 0)
            return history;

        var messages = new List<ChatMessage>(history);
        List<ChatMessage>? trimmed = null;

        // Step 1: Sliding window
        if (options.MaxExchanges.HasValue)
            trimmed = ApplySlidingWindow(messages, options.MaxExchanges.Value);

        // Steps 2 & 3 (token budget + summary) in future tasks
        _ = chatClient; // will be used for summary

        return messages;
    }

    private static List<ChatMessage> ApplySlidingWindow(List<ChatMessage> messages, int maxExchanges)
    {
        var systemMessages = messages.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystem = messages.Where(m => m.Role != ChatRole.System).ToList();

        // Count exchanges: each user+assistant pair = 1 exchange
        // Keep last N exchanges from the end
        var keepCount = maxExchanges * 2;
        var trimmed = new List<ChatMessage>();

        if (nonSystem.Count > keepCount)
        {
            trimmed.AddRange(nonSystem.Take(nonSystem.Count - keepCount));
            nonSystem = nonSystem.Skip(nonSystem.Count - keepCount).ToList();
        }

        messages.Clear();
        messages.AddRange(systemMessages);
        messages.AddRange(nonSystem);

        return trimmed;
    }
}
```

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests --filter "ConversationMemoryTests"
```
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/Memory/ConversationMemoryPipeline.cs \
        tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs
git commit -m "feat: implement sliding window strategy in ConversationMemoryPipeline"
```

---

### Task 3: Token budget strategy

**Files:**
- Modify: `src/Rag.NET/Memory/ConversationMemoryPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task TokenBudget_TrimsOldestNonSystemMessages()
{
    var ct = TestContext.Current.CancellationToken;
    // Each "Question N" / "Answer N" is ~3 tokens. 5 exchanges = ~30 tokens.
    // Budget of 15 tokens should keep roughly last 2-3 exchanges.
    var opts = new ConversationMemoryOptions { MaxTokens = 15 };
    var sut = new ConversationMemoryPipeline(opts, chatClient: null);
    var history = MakeExchanges(5);

    var result = await sut.ProcessAsync(history, ct);

    Assert.True(result.Count < history.Count, "Some messages should be trimmed");
    Assert.True(result.Count > 0, "Should keep at least some messages");
    // Last message should always be the most recent
    Assert.Contains("Answer 5", result[^1].Text);
}

[Fact]
public async Task TokenBudget_PreservesSystemMessages()
{
    var ct = TestContext.Current.CancellationToken;
    var opts = new ConversationMemoryOptions { MaxTokens = 10 };
    var sut = new ConversationMemoryPipeline(opts, chatClient: null);
    var history = new List<ChatMessage>
    {
        new(ChatRole.System, "System prompt."),
        new(ChatRole.User, "Q1"), new(ChatRole.Assistant, "A1"),
        new(ChatRole.User, "Q2"), new(ChatRole.Assistant, "A2"),
    };

    var result = await sut.ProcessAsync(history, ct);

    Assert.Contains(result, m => m.Role == ChatRole.System);
}
```

**Step 2: Run tests to verify they fail**

**Step 3: Add token budget logic**

Use `Microsoft.ML.Tokenizers.TiktokenTokenizer` (already a project dependency via `TokenAwareChunkingStrategy`). In `ConversationMemoryPipeline`:

- Count tokens per message using `cl100k_base` encoding
- Remove oldest non-system messages one by one until total ≤ `MaxTokens`
- Track trimmed messages for later summary step

**Step 4: Run tests**

**Step 5: Commit**

```bash
git add src/Rag.NET/Memory/ConversationMemoryPipeline.cs \
        tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs
git commit -m "feat: add token budget strategy to ConversationMemoryPipeline"
```

---

### Task 4: Summary strategy

**Files:**
- Modify: `src/Rag.NET/Memory/ConversationMemoryPipeline.cs`
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs` (add `ConversationSummaryFailed` log entry)
- Modify: `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Summary_WhenTrimmedMessages_PrependsSummarySystemMessage()
{
    var ct = TestContext.Current.CancellationToken;
    var chatClient = Substitute.For<IChatClient>();
    chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of old conversation.")));

    var opts = new ConversationMemoryOptions { MaxExchanges = 1, UseSummary = true };
    var sut = new ConversationMemoryPipeline(opts, chatClient);
    var history = new List<ChatMessage>
    {
        new(ChatRole.User, "Q1"), new(ChatRole.Assistant, "A1"),
        new(ChatRole.User, "Q2"), new(ChatRole.Assistant, "A2"),
    };

    var result = await sut.ProcessAsync(history, ct);

    // Should have: summary system message + last exchange (Q2, A2) = 3 messages
    Assert.Equal(3, result.Count);
    Assert.Equal(ChatRole.System, result[0].Role);
    Assert.Contains("Summary", result[0].Text);
}

[Fact]
public async Task Summary_LlmFails_ReturnsTrimmedWithoutSummary()
{
    var ct = TestContext.Current.CancellationToken;
    var chatClient = Substitute.For<IChatClient>();
    chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new InvalidOperationException("LLM down"));

    var opts = new ConversationMemoryOptions { MaxExchanges = 1, UseSummary = true };
    var sut = new ConversationMemoryPipeline(opts, chatClient);
    var history = MakeExchanges(3);

    var result = await sut.ProcessAsync(history, ct);

    // Should still work — just no summary prepended
    Assert.Equal(2, result.Count); // last exchange only
}

[Fact]
public async Task Summary_NoTrimmedMessages_SkipsSummaryCall()
{
    var ct = TestContext.Current.CancellationToken;
    var chatClient = Substitute.For<IChatClient>();

    var opts = new ConversationMemoryOptions { MaxExchanges = 10, UseSummary = true };
    var sut = new ConversationMemoryPipeline(opts, chatClient);
    var history = MakeExchanges(2); // well within window

    var result = await sut.ProcessAsync(history, ct);

    await chatClient.DidNotReceive().GetResponseAsync(
        Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task Summary_CustomPromptTemplate_UsedInLlmCall()
{
    var ct = TestContext.Current.CancellationToken;
    var chatClient = Substitute.For<IChatClient>();
    chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Custom summary.")));

    var opts = new ConversationMemoryOptions
    {
        MaxExchanges = 1,
        UseSummary = true,
        SummaryPromptTemplate = "Summarize this chat: {messages}",
    };
    var sut = new ConversationMemoryPipeline(opts, chatClient);
    var history = MakeExchanges(3);

    _ = await sut.ProcessAsync(history, ct);

    await chatClient.Received(1).GetResponseAsync(
        Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text!.Contains("Summarize this chat"))),
        Arg.Any<ChatOptions?>(),
        Arg.Any<CancellationToken>());
}
```

**Step 2: Run tests to verify they fail**

**Step 3: Implement summary strategy**

In `ConversationMemoryPipeline.ProcessAsync`, after sliding window and token budget:

- If `UseSummary = true` and `trimmed.Count > 0`:
  - Build a prompt from trimmed messages (format: "User: ... / Assistant: ...")
  - Call `chatClient.GetResponseAsync` with summary prompt
  - Prepend result as `new ChatMessage(ChatRole.System, "Summary of earlier conversation: {text}")`
  - Catch non-cancellation exceptions → log warning via `RagPipelineLog.ConversationSummaryFailed`, continue without summary
  - `OperationCanceledException` → re-throw

Add `ConversationSummaryFailed` to `RagPipelineLog.cs`:
```csharp
[LoggerMessage(Level = LogLevel.Warning, Message = "ConversationMemoryPipeline: summary LLM call failed; returning trimmed history without summary")]
internal static partial void ConversationSummaryFailed(ILogger logger, Exception exception);
```

> **Note:** `ConversationMemoryPipeline` needs an `ILogger` injected to call this. Add it to the constructor.

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests --filter "ConversationMemoryTests"
```

**Step 5: Commit**

```bash
git add src/Rag.NET/Memory/ConversationMemoryPipeline.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs
git commit -m "feat: add summary strategy with graceful LLM failure fallback"
```

---

### Task 5: Answer engine integration + `UseConversationMemory` DI registration

**Files:**
- Modify: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`
- Modify: `src/Rag.NET/AnswerGeneration/MapReduceAnswerEngine.cs`
- Modify: `src/Rag.NET/AnswerGeneration/RefineAnswerEngine.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void UseConversationMemory_RegistersMemoryAndOptions()
{
    var services = new ServiceCollection();
    services.AddSingleton(Substitute.For<IChatClient>());
    services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
    services.AddSingleton(Substitute.For<IVectorStore>());

    services.AddRagNet(rag => rag.UseConversationMemory(new ConversationMemoryOptions
    {
        MaxExchanges = 5,
        MaxTokens = 2000,
    }));

    var provider = services.BuildServiceProvider();
    var memory = provider.GetService<IConversationMemory>();
    Assert.NotNull(memory);
    Assert.IsType<ConversationMemoryPipeline>(memory);
}
```

**Step 2: Run tests to verify they fail**

**Step 3: Add `UseConversationMemory` to `RagBuilder`**

```csharp
/// <summary>
/// Enables automatic conversation history management with composable strategies:
/// sliding window, token-budget truncation, and LLM summary compression.
/// When not registered, answer engines pass conversation history through unchanged.
/// </summary>
public RagBuilder UseConversationMemory(ConversationMemoryOptions? options = null)
{
    Services.AddSingleton(options ?? new ConversationMemoryOptions());
    Services.AddSingleton<IConversationMemory, ConversationMemoryPipeline>();
    return this;
}
```

**Step 4: Integrate into answer engines**

In `ServiceCollectionExtensions.cs`, when building the `IRagPipeline`, resolve `IConversationMemory?` from DI and pass it to the answer engines.

In each engine's `BuildMessages` method (or equivalent), add:
```csharp
var history = opts.ConversationHistory;
if (memory is not null && history is { Count: > 0 })
    history = await memory.ProcessAsync(history, ct).ConfigureAwait(false);
```

This changes `BuildMessages` from `static` to instance (needs the `IConversationMemory` reference). Alternatively, pass `IConversationMemory?` as a parameter to `BuildMessages`.

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName!~PgVector"
```

**Step 6: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs \
        src/Rag.NET/AnswerGeneration/MapReduceAnswerEngine.cs \
        src/Rag.NET/AnswerGeneration/RefineAnswerEngine.cs \
        src/Rag.NET/DependencyInjection/RagBuilder.cs \
        src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs \
        tests/Rag.NET.Tests/Memory/ConversationMemoryTests.cs
git commit -m "feat: integrate ConversationMemory into answer engines; add UseConversationMemory"
```

---

### Task 6: Full solution test suite green check

**Step 1:** `dotnet test --filter "FullyQualifiedName!~PgVector"`

Expected: All tests pass.

---

## Summary

| Task | Key Change |
|------|-----------|
| 1 | `IConversationMemory` interface + `ConversationMemoryOptions` |
| 2 | Sliding window strategy (keep last N exchanges, preserve system messages) |
| 3 | Token budget strategy (trim oldest non-system until within limit) |
| 4 | Summary strategy (LLM-compress trimmed messages, graceful failure) |
| 5 | Answer engine integration + `UseConversationMemory` DI registration |
| 6 | Full suite green |
