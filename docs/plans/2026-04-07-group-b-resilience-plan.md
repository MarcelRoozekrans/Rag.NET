# Group B: Production Resilience — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add two production-readiness features: a transient-error fallback chain for `IChatClient`, and configurable parallelism for multi-document ingestion.

**Architecture:** `FallbackChatClient` is a pure `IChatClient` decorator — no pipeline changes, no DI extensions needed. The batch optimiser adds one property to `IngestionOptions` and replaces the sequential `await foreach` in `RagPipelineExtensions.IngestFromProviderAsync` with `Parallel.ForEachAsync`, making shared collections thread-safe.

**Tech Stack:** `Microsoft.Extensions.AI` (`IChatClient`), `System.Collections.Concurrent`, `System.Threading.Tasks.Parallel`, `NSubstitute`, `xunit.v3`.

---

## Context for the implementer

### Key files

- `src/Rag.NET.Abstractions/Models/Options/IngestionOptions.cs` — add `MaxDegreeOfParallelism`
- `src/Rag.NET/DataProviders/RagPipelineExtensions.cs` — parallelise the provider loop
- `src/Rag.NET/` — create `FallbackChatClient.cs` here (alongside `AnswerGeneration/`)
- `tests/Rag.NET.Tests/` — tests go in `Resilience/FallbackChatClientTests.cs` and `DataProviders/IngestFromProviderTests.cs` (already exists)

### Test conventions

- `TestContext.Current.CancellationToken` for all async tests
- `NSubstitute` for mocks
- `xunit.v3` (`[Fact]`, `[Theory]`)
- Class per feature area, in `tests/Rag.NET.Tests/<Area>/`

### `IChatClient` interface (Microsoft.Extensions.AI)

```csharp
Task<ChatResponse> GetResponseAsync(
    IList<ChatMessage> messages,
    ChatOptions? options = null,
    CancellationToken cancellationToken = default);

IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
    IList<ChatMessage> messages,
    ChatOptions? options = null,
    CancellationToken cancellationToken = default);
```

### Transient error definition

An exception is transient if:
- It is `HttpRequestException` with `StatusCode` 429 or 503, or no status code (network failure)
- It is `TaskCanceledException` or `TimeoutException`
- Its `Message` contains any of: `"rate limit"`, `"throttl"`, `"timeout"`, `"unavailable"` (case-insensitive)

### `IngestionOptions` current state

```csharp
public sealed class IngestionOptions
{
    public bool Overwrite { get; set; }
}
```

### `IngestFromProviderAsync` current sequential loop

```csharp
await foreach (var entry in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
{
    seenIds.Add(entry.Id);
    var outcome = await ProcessEntryAsync(pipeline, providerId, entry, hashStore, baseMetadata,
        options, progress, errors, cancellationToken).ConfigureAwait(false);
    if (outcome == EntryOutcome.Ingested) ingested++;
    else skipped++;
}
```

---

## Task 1: `FallbackChatClient` — tests first

**Files:**
- Create: `tests/Rag.NET.Tests/Resilience/FallbackChatClientTests.cs`

**Step 1: Write failing tests**

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

public class FallbackChatClientTests
{
    private static IList<ChatMessage> AnyMessages() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task GetResponseAsync_PrimarySucceeds_SecondaryNeverCalled()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_PrimaryTransientFailure_SecondarySucceeds()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("rate limit exceeded", null, System.Net.HttpStatusCode.TooManyRequests));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback ok", result.Text);
    }

    [Fact]
    public async Task GetResponseAsync_NonTransientException_PropagatesImmediately()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("bad config"));

        var sut = new FallbackChatClient([primary, secondary]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));

        await secondary.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_AllClientsFail_ThrowsLastException()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("rate limit", null, System.Net.HttpStatusCode.TooManyRequests));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var sut = new FallbackChatClient([primary, secondary]);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_429StatusCode_IsTransient()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("429", null, System.Net.HttpStatusCode.TooManyRequests));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task GetResponseAsync_MessageContainsRateLimit_IsTransient()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("You hit the rate limit for this model"));
        secondary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new FallbackChatClient([primary, secondary]);
        var result = await sut.GetResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrimaryTransientFailure_SecondaryUsed()
    {
        var primary = Substitute.For<IChatClient>();
        var secondary = Substitute.For<IChatClient>();
        primary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ThrowAsync<ChatResponseUpdate>(new HttpRequestException("rate limit", null, System.Net.HttpStatusCode.TooManyRequests)));
        secondary.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(YieldUpdates(new ChatResponseUpdate { Contents = [new TextContent("streamed")] }));

        var sut = new FallbackChatClient([primary, secondary]);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync(AnyMessages(), cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(u);

        Assert.Single(updates);
        Assert.Equal("streamed", updates[0].Text);
    }

    // Helper: async enumerable that throws immediately
    private static async IAsyncEnumerable<T> ThrowAsync<T>(Exception ex)
    {
        await Task.Yield();
        throw ex;
        yield break; // unreachable, satisfies compiler
    }

    // Helper: async enumerable yielding items
    private static async IAsyncEnumerable<T> YieldUpdates<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FallbackChatClientTests" -v q
```

Expected: compile error — `Rag.NET.Resilience` namespace not found.

**Step 3: Commit tests**

```bash
git add tests/Rag.NET.Tests/Resilience/FallbackChatClientTests.cs
git commit -m "test(resilience): add failing tests for FallbackChatClient"
```

---

## Task 2: `FallbackChatClient` — implementation

**Files:**
- Create: `src/Rag.NET/Resilience/FallbackChatClient.cs`

**Step 1: Write the implementation**

`src/Rag.NET/Resilience/FallbackChatClient.cs`:
```csharp
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IChatClient"/> decorator that falls back to subsequent clients
/// when the current one raises a transient error (rate-limit, timeout, service-unavailable).
/// Non-transient errors propagate immediately without consulting further clients.
/// </summary>
public sealed class FallbackChatClient(
    IReadOnlyList<IChatClient> clients,
    ILogger<FallbackChatClient>? logger = null) : IChatClient
{
    private static readonly string[] s_transientKeywords = ["rate limit", "throttl", "timeout", "unavailable"];

    public ChatClientMetadata Metadata => clients[0].Metadata;

    public async Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (int i = 0; i < clients.Count; i++)
        {
            try
            {
                return await clients[i].GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                if (i < clients.Count - 1)
                    logger?.LogWarning(ex, "Client {Index} failed transiently ({Message}); trying next client.", i, ex.Message);
            }
        }
        throw last!;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (int i = 0; i < clients.Count; i++)
        {
            bool transientFailure = false;
            var enumerator = clients[i]
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                transientFailure = true;
                if (i < clients.Count - 1)
                    logger?.LogWarning(ex, "Streaming client {Index} failed transiently; trying next client.", i);
                hasNext = false;
            }

            if (!transientFailure)
            {
                while (hasNext)
                {
                    yield return enumerator.Current;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        last = ex;
                        if (i < clients.Count - 1)
                            logger?.LogWarning(ex, "Streaming client {Index} failed mid-stream transiently; trying next client.", i);
                        hasNext = false;
                        transientFailure = true;
                    }
                }

                if (!transientFailure)
                    yield break; // success
            }
        }

        throw last!;
    }

    public void Dispose() { /* clients are externally owned */ }

    internal static bool IsTransient(Exception ex)
    {
        if (ex is OperationCanceledException or TaskCanceledException or TimeoutException)
            return true;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                return true;
        }

        var msg = ex.Message;
        foreach (var keyword in s_transientKeywords)
        {
            if (msg.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
```

**Step 2: Run the tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FallbackChatClientTests" -v q
```

Expected: All 7 tests pass.

**Step 3: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
```

Expected: All tests pass (no regressions).

**Step 4: Commit**

```bash
git add src/Rag.NET/Resilience/FallbackChatClient.cs
git commit -m "feat(resilience): add FallbackChatClient with transient error fallback"
```

---

## Task 3: `MaxDegreeOfParallelism` — add to IngestionOptions

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/Options/IngestionOptions.cs`

**Step 1: Write failing test first**

Add this test to `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`:

```csharp
[Fact]
public async Task IngestFromProviderAsync_ParallelIngestion_AllFilesIngested()
{
    var provider = MakeProvider(
        ("id-1", "a.txt", "hello", null),
        ("id-2", "b.txt", "world", null),
        ("id-3", "c.txt", "foo", null),
        ("id-4", "d.txt", "bar", null));

    var options = new IngestionOptions { MaxDegreeOfParallelism = 4 };
    var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
        options: options,
        cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(4, result.Ingested);
    Assert.Equal(0, result.Skipped);
}
```

**Step 2: Run to verify it fails**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "IngestFromProviderAsync_ParallelIngestion" -v q
```

Expected: compile error — `MaxDegreeOfParallelism` does not exist on `IngestionOptions`.

**Step 3: Add the property**

`src/Rag.NET.Abstractions/Models/Options/IngestionOptions.cs`:
```csharp
namespace Rag.NET.Models.Options;

public sealed class IngestionOptions
{
    public bool Overwrite { get; set; }

    /// <summary>
    /// Maximum number of documents to ingest concurrently when using
    /// <c>IngestFromProviderAsync</c>. Default is 1 (sequential).
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;
}
```

**Step 4: Run the test**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "IngestFromProviderAsync_ParallelIngestion" -v q
```

Expected: FAIL — property exists but parallelism not yet implemented, behaviour still sequential (test should pass if sequential is already correct, or fail if the test asserts something specific about parallelism).

> Note: This test only asserts that 4 files are ingested — that works sequentially too. The test is a regression guard; the next task wires the actual parallel path.

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
```

Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET.Abstractions/Models/Options/IngestionOptions.cs tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs
git commit -m "feat(ingestion): add MaxDegreeOfParallelism to IngestionOptions"
```

---

## Task 4: Parallelise `IngestFromProviderAsync`

**Files:**
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`

**Step 1: Write a concurrency-specific failing test**

Add this test to `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`:

```csharp
[Fact]
public async Task IngestFromProviderAsync_ParallelIngestion_RunsConcurrently()
{
    // Verify that with MaxDegreeOfParallelism > 1, multiple documents are processed
    // concurrently by measuring overlap via a semaphore.
    var concurrentCount = 0;
    var maxConcurrent = 0;
    var gate = new SemaphoreSlim(0);

    _pipeline.IngestAsync(
            Arg.Any<Stream>(),
            Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(),
            Arg.Any<IProgress<IngestionProgress>?>(),
            Arg.Any<CancellationToken>())
        .Returns(async _ =>
        {
            var current = Interlocked.Increment(ref concurrentCount);
            Interlocked.Exchange(ref maxConcurrent, Math.Max(maxConcurrent, current));
            await gate.WaitAsync(); // hold until released
            Interlocked.Decrement(ref concurrentCount);
            return Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("x"), ChunksStored = 1 });
        });

    var provider = MakeProvider(
        ("id-1", "a.txt", "hello", null),
        ("id-2", "b.txt", "world", null),
        ("id-3", "c.txt", "foo", null));

    var ingestTask = _pipeline.IngestFromProviderAsync(provider, "prov",
        options: new IngestionOptions { MaxDegreeOfParallelism = 3 },
        cancellationToken: TestContext.Current.CancellationToken);

    // Give tasks time to start and block on the gate
    await Task.Delay(100, TestContext.Current.CancellationToken);

    // Release all
    gate.Release(3);

    var result = await ingestTask;

    Assert.Equal(3, result.Ingested);
    Assert.True(maxConcurrent > 1, $"Expected concurrent ingestion but maxConcurrent was {maxConcurrent}");
}
```

**Step 2: Run to verify it fails**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "IngestFromProviderAsync_ParallelIngestion_RunsConcurrently" -v q
```

Expected: FAIL — `maxConcurrent` will be 1 (sequential).

**Step 3: Implement parallel ingestion**

In `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`, make the following changes:

1. Add `using System.Collections.Concurrent;` at the top.

2. Replace the `IngestFromProviderAsync` method body with this version that uses `Parallel.ForEachAsync` when `MaxDegreeOfParallelism > 1`:

```csharp
public static async Task<ProviderIngestionResult> IngestFromProviderAsync(
    this IRagPipeline pipeline,
    IFileContentProvider provider,
    string providerId,
    IContentHashStore? hashStore = null,
    DocumentMetadata? baseMetadata = null,
    IngestionOptions? options = null,
    CleanupMode cleanupMode = CleanupMode.None,
    IProgress<IngestionProgress>? progress = null,
    CancellationToken cancellationToken = default)
{
    var ingested = 0;
    var skipped = 0;
    var deleted = 0;
    var errors = new ConcurrentBag<string>();
    var seenIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

    IReadOnlySet<string> knownIds = hashStore is not null && cleanupMode == CleanupMode.Full
        ? await hashStore.GetAllIdsAsync(providerId, cancellationToken).ConfigureAwait(false)
        : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

    var maxParallelism = options?.MaxDegreeOfParallelism ?? 1;
    var parallelOptions = new ParallelOptions
    {
        MaxDegreeOfParallelism = maxParallelism,
        CancellationToken = cancellationToken,
    };

    // Collect entries first (IAsyncEnumerable cannot be iterated in parallel directly)
    var entries = new List<FileEntry>();
    await foreach (var entry in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
        entries.Add(entry);

    await Parallel.ForEachAsync(entries, parallelOptions, async (entry, ct) =>
    {
        seenIds.TryAdd(entry.Id, 0);
        var outcome = await ProcessEntryAsync(pipeline, providerId, entry, hashStore, baseMetadata,
            options, progress, errors, ct).ConfigureAwait(false);
        if (outcome == EntryOutcome.Ingested)
            Interlocked.Increment(ref ingested);
        else
            Interlocked.Increment(ref skipped);
    });

    if (cleanupMode == CleanupMode.Full && hashStore is not null)
    {
        var seenSet = new HashSet<string>(seenIds.Keys, StringComparer.Ordinal);
        deleted = await CleanupDisappearedAsync(pipeline, providerId, hashStore, knownIds, seenSet,
            errors, cancellationToken).ConfigureAwait(false);
    }

    return new ProviderIngestionResult(ingested, skipped, deleted, [.. errors]);
}
```

3. Change the `ProcessEntryAsync` signature: the `errors` parameter changes from `List<string>` to `ConcurrentBag<string>`:

```csharp
private static async Task<EntryOutcome> ProcessEntryAsync(
    IRagPipeline pipeline,
    string providerId,
    FileEntry entry,
    IContentHashStore? hashStore,
    DocumentMetadata? baseMetadata,
    IngestionOptions? options,
    IProgress<IngestionProgress>? progress,
    ConcurrentBag<string> errors,  // ← was List<string>
    CancellationToken cancellationToken)
```

4. Update `CleanupDisappearedAsync` similarly — change its `errors` parameter from `List<string>` to `ConcurrentBag<string>`:

```csharp
private static async Task<int> CleanupDisappearedAsync(
    IRagPipeline pipeline,
    string providerId,
    IContentHashStore hashStore,
    IReadOnlySet<string> knownIds,
    HashSet<string> seenIds,
    ConcurrentBag<string> errors,  // ← was List<string>
    CancellationToken cancellationToken)
```

The body of both methods stays the same — `errors.Add(...)` works on both `List<string>` and `ConcurrentBag<string>`.

**Step 4: Run the concurrency test**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "IngestFromProviderAsync_ParallelIngestion" -v q
```

Expected: Both parallel tests pass.

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
```

Expected: All tests pass (no regressions).

**Step 6: Commit**

```bash
git add src/Rag.NET/DataProviders/RagPipelineExtensions.cs tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs
git commit -m "feat(ingestion): parallelise IngestFromProviderAsync via MaxDegreeOfParallelism"
```

---

## Task 5: Update features backlog

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Mark LLM Fallback Chain as done**

Find `### LLM Fallback Chain` and add before its closing `---`:
```markdown
**Status:** ✅ Done
```

**Step 2: Mark Batch Ingestion Optimiser as done**

Find `### Batch Ingestion Optimiser` and add before its closing `---`:
```markdown
**Status:** ✅ Done
```

**Step 3: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark LLM Fallback Chain and Batch Ingestion Optimiser as done"
```
