# LLM Metadata Extraction + Self-Query Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `LlmMetadataExtractionBehavior` (ingestion) and `SelfQueryBehavior` (retrieval) so the pipeline automatically enriches chunks with LLM-extracted tags at index time and narrows retrieval by those tags at query time.

**Architecture:** Two new pipeline behaviors wired into the existing `IngestionPipelineBuilder` and `RetrievalPipelineBuilder`. Both use an optional `AttributeInfo[]` schema to constrain the LLM and degrade gracefully when not configured. Internal LLM call results are `Result<T>` (ZeroAlloc.Results); on failure, a warning is logged and the pipeline continues unchanged.

**Tech Stack:** C# 13 / .NET 10, `Microsoft.Extensions.AI.IChatClient`, `ZeroAlloc.Results`, `ZeroAlloc.Inject`, `System.Text.Json`, NSubstitute + xUnit for tests.

---

### Task 1: Add shared models

**Files:**
- Create: `src/Rag.NET/Models/AttributeInfo.cs`
- Create: `src/Rag.NET/SelfQuery/SelfQueryOutput.cs`

**Step 1: Create `AttributeInfo`**

```csharp
// src/Rag.NET/Models/AttributeInfo.cs
namespace Rag.NET.Models;

/// <summary>
/// Describes a metadata field that the LLM should extract at ingest
/// and/or filter on at query time.
/// </summary>
public sealed record AttributeInfo(string Name, string Description);
```

**Step 2: Create `SelfQueryOutput` (internal DTO for parsed LLM response)**

```csharp
// src/Rag.NET/SelfQuery/SelfQueryOutput.cs
namespace Rag.NET.SelfQuery;

internal sealed record SelfQueryOutput(
    string Query,
    IReadOnlyList<KeyValuePair<string, string>> Filters);
```

**Step 3: Commit**

```bash
git add src/Rag.NET/Models/AttributeInfo.cs src/Rag.NET/SelfQuery/SelfQueryOutput.cs
git commit -m "feat: add AttributeInfo and SelfQueryOutput models"
```

---

### Task 2: Add options types and extend `RetrievalOptions`

**Files:**
- Create: `src/Rag.NET/Models/Options/LlmMetadataExtractionOptions.cs`
- Create: `src/Rag.NET/Models/Options/SelfQueryOptions.cs`
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`

**Step 1: Create `LlmMetadataExtractionOptions`**

```csharp
// src/Rag.NET/Models/Options/LlmMetadataExtractionOptions.cs
using Rag.NET.Models;

namespace Rag.NET.Models.Options;

public sealed class LlmMetadataExtractionOptions
{
    /// <summary>
    /// When provided, the LLM is constrained to extract only these fields.
    /// When null, the LLM extracts freely.
    /// </summary>
    public IReadOnlyList<AttributeInfo>? Schema { get; init; }
}
```

**Step 2: Create `SelfQueryOptions`**

```csharp
// src/Rag.NET/Models/Options/SelfQueryOptions.cs
using Rag.NET.Models;

namespace Rag.NET.Models.Options;

public sealed class SelfQueryOptions
{
    /// <summary>
    /// When provided, the LLM is told which metadata fields are available for filtering.
    /// When null, the LLM filters freely.
    /// </summary>
    public IReadOnlyList<AttributeInfo>? Schema { get; init; }
}
```

**Step 3: Add `UseSelfQuery` to `RetrievalOptions`**

Open `src/Rag.NET/Models/Options/RetrievalOptions.cs` and add after the `UseHyde` property:

```csharp
/// <summary>
/// Set to <see langword="false"/> to skip self-query rewriting and filter generation for this call,
/// even when <see cref="SelfQueryOptions"/> is registered in DI.
/// Has no effect when <c>UseSelfQuery()</c> is not registered.
/// </summary>
public bool UseSelfQuery { get; init; } = true;
```

**Step 4: Commit**

```bash
git add src/Rag.NET/Models/Options/LlmMetadataExtractionOptions.cs \
        src/Rag.NET/Models/Options/SelfQueryOptions.cs \
        src/Rag.NET/Models/Options/RetrievalOptions.cs
git commit -m "feat: add LlmMetadataExtractionOptions, SelfQueryOptions; extend RetrievalOptions"
```

---

### Task 3: Add log messages

**Files:**
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`

**Step 1: Add four log messages at the end of the class (before the closing `}`)**

```csharp
[LoggerMessage(Level = LogLevel.Debug, Message = "LLM metadata extraction produced {TagCount} tag(s) for chunk {ChunkIndex}")]
internal static partial void MetadataExtractionCompleted(ILogger logger, int tagCount, int chunkIndex);

[LoggerMessage(Level = LogLevel.Warning, Message = "LLM metadata extraction failed for chunk {ChunkIndex}, skipping: {Error}")]
internal static partial void MetadataExtractionFailed(ILogger logger, int chunkIndex, string error);

[LoggerMessage(Level = LogLevel.Debug, Message = "Self-query produced {FilterCount} filter(s) for query '{Query}'")]
internal static partial void SelfQueryCompleted(ILogger logger, string query, int filterCount);

[LoggerMessage(Level = LogLevel.Warning, Message = "Self-query failed for query '{Query}', proceeding without filter: {Error}")]
internal static partial void SelfQueryFailed(ILogger logger, string query, string error);
```

**Step 2: Build to confirm no compile errors**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add log messages for metadata extraction and self-query behaviors"
```

---

### Task 4: Implement `LlmMetadataExtractionBehavior` (TDD)

**Files:**
- Create: `src/Rag.NET/Ingestion/Behaviors/LlmMetadataExtractionBehavior.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/Behaviors/LlmMetadataExtractionBehaviorTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Ingestion/Behaviors/LlmMetadataExtractionBehaviorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class LlmMetadataExtractionBehaviorTests
{
    private static IngestionContext MakeContext(params TextChunk[] chunks)
    {
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
            },
            GetNextBm25DocId = () => 1,
        };
        ctx.Chunks.AddRange(chunks);
        return ctx;
    }

    private static TextChunk MakeChunk(string text, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc-1"), ChunkIndex = index };

    private static ValueTask<IngestionResult> StubNext(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = ctx.Chunks.Count });

    // ── No-op when options not set ────────────────────────────────────────────

    [Fact]
    public async Task WhenOptionsNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        var sut = new LlmMetadataExtractionBehavior { ChatClient = chatClient, ExtractionOptions = null };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenChatClientNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = null,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsValidJson_MergesTagsIntoChunkMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, """{"topic":"security","year":"2024"}""")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some security document");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Equal("security", chunk.Metadata["topic"]);
        Assert.Equal("2024", chunk.Metadata["year"]);
    }

    // ── Schema-guided: unknown keys ignored ───────────────────────────────────

    [Fact]
    public async Task WhenSchemaProvided_IgnoresKeysNotInSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, """{"topic":"security","unknown_key":"should-be-ignored"}""")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions
            {
                Schema = [new AttributeInfo("topic", "Main subject area")]
            }
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.True(chunk.Metadata.ContainsKey("topic"));
        Assert.False(chunk.Metadata.ContainsKey("unknown_key"));
    }

    // ── Invalid JSON: warning logged, chunk unchanged ─────────────────────────

    [Fact]
    public async Task WhenLlmReturnsInvalidJson_ChunkMetadataUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "not json at all")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        // Should not throw
        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
    }

    // ── Empty JSON: no tags, pipeline continues ───────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsEmptyJson_NoTagsAdded()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "{}")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
    }
}
```

**Step 2: Run tests — confirm they fail**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "LlmMetadataExtractionBehaviorTests" -v minimal
```
Expected: FAIL with type-not-found errors.

**Step 3: Implement `LlmMetadataExtractionBehavior`**

```csharp
// src/Rag.NET/Ingestion/Behaviors/LlmMetadataExtractionBehavior.cs
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class LlmMetadataExtractionBehavior : IIngestionBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }
    [Inject(Required = false)] public LlmMetadataExtractionOptions? ExtractionOptions { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ChatClient is null || ExtractionOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        foreach (ref var chunk in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            var result = await ExtractAsync(chunk.Text, ct).ConfigureAwait(false);
            result.Match(
                tags =>
                {
                    foreach (var (key, value) in tags)
                    {
                        if (ExtractionOptions.Schema is null || ExtractionOptions.Schema.Any(a => a.Name == key))
                            chunk.Metadata.TryAdd(key, value);
                    }
                    RagPipelineLog.MetadataExtractionCompleted(Microsoft.Extensions.Logging.NullLogger.Instance, tags.Count, chunk.ChunkIndex);
                },
                error => RagPipelineLog.MetadataExtractionFailed(Microsoft.Extensions.Logging.NullLogger.Instance, chunk.ChunkIndex, error));
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private async ValueTask<Result<IReadOnlyDictionary<string, string>>> ExtractAsync(string text, CancellationToken ct)
    {
        try
        {
            var prompt = BuildPrompt(text);
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await ChatClient!.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var json = response.Text ?? "{}";

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null)
                return Result<IReadOnlyDictionary<string, string>>.Failure("LLM returned null JSON");

            return Result<IReadOnlyDictionary<string, string>>.Success(parsed);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(ex.Message);
        }
    }

    private string BuildPrompt(string text)
    {
        if (ExtractionOptions!.Schema is { Count: > 0 } schema)
        {
            var fields = string.Join(", ", schema.Select(a => $"{a.Name} ({a.Description})"));
            return $"""
                Extract metadata from the following text.
                Return a JSON object using only these fields: {fields}.
                Omit fields not present in the text. Return {{}} if nothing applies.
                Values must be strings.

                Text:
                {text}
                """;
        }

        return $"""
            Extract metadata from the following text as a flat JSON object.
            Keys must be lowercase snake_case strings. Values must be strings.
            Return {{}} if nothing useful can be extracted.

            Text:
            {text}
            """;
    }
}
```

**Step 4: Run tests — confirm they pass**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "LlmMetadataExtractionBehaviorTests" -v minimal
```
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/Behaviors/LlmMetadataExtractionBehavior.cs \
        tests/Rag.NET.Tests/Ingestion/Behaviors/LlmMetadataExtractionBehaviorTests.cs
git commit -m "feat: implement LlmMetadataExtractionBehavior with tests"
```

---

### Task 5: Wire `LlmMetadataExtractionBehavior` into the pipeline

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`

**Step 1: Add behavior to `IngestionPipelineBuilder`**

In `IngestionPipelineBuilder.cs`, add `typeof(LlmMetadataExtractionBehavior)` to the `_types` list after `ChunkingBehavior` and before `MetadataBehavior`:

```csharp
private readonly List<Type> _types =
[
    typeof(OverwriteBehavior),
    typeof(ParseBehavior),
    typeof(ChunkingBehavior),
    typeof(LlmMetadataExtractionBehavior),   // ← add this line
    typeof(MetadataBehavior),
    typeof(ParentDocumentIngestionBehavior),
    typeof(EmbeddingBehavior),
    typeof(StorageBehavior),
];
```

**Step 2: Add `UseLlmMetadataExtraction()` to `RagBuilder`**

Add after `UseHyde()`:

```csharp
/// <summary>
/// Registers <see cref="LlmMetadataExtractionBehavior"/> in the ingestion pipeline.
/// When registered, the LLM extracts structured metadata tags from each chunk at index time.
/// </summary>
/// <remarks>
/// Requires <c>IChatClient</c> to be registered in DI.
/// When <paramref name="schema"/> is provided, extraction is constrained to the listed fields.
/// </remarks>
/// <param name="schema">Optional list of fields to extract. When null, the LLM extracts freely.</param>
public RagBuilder UseLlmMetadataExtraction(IReadOnlyList<AttributeInfo>? schema = null)
{
    Services.AddSingleton(new LlmMetadataExtractionOptions { Schema = schema });
    return this;
}
```

Add the required using at the top of `RagBuilder.cs`:
```csharp
using Rag.NET.Models;
using Rag.NET.Ingestion.Behaviors;
```

**Step 3: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded.

**Step 4: Run all tests**

```bash
dotnet test tests/Rag.NET.Tests/ -v minimal
```
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs \
        src/Rag.NET/DependencyInjection/RagBuilder.cs
git commit -m "feat: wire LlmMetadataExtractionBehavior into ingestion pipeline; add UseLlmMetadataExtraction()"
```

---

### Task 6: Implement `SelfQueryBehavior` (TDD)

**Files:**
- Create: `src/Rag.NET/SelfQuery/SelfQueryBehavior.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/Behaviors/SelfQueryBehaviorTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Retrieval/Behaviors/SelfQueryBehaviorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Specifications;
using Rag.NET.SelfQuery;
using Xunit;
using ZeroAlloc.Specification;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class SelfQueryBehaviorTests
{
    private static RetrievalContext MakeCtx(RetrievalOptions? options = null) =>
        new() { Query = "show me 2024 security documents", Options = options ?? new RetrievalOptions() };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        CaptureAndReturn(out Capture capture)
    {
        var c = new Capture();
        capture = c;
        return (ctx, _) =>
        {
            c.Context = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        };
    }

    private sealed class Capture { public RetrievalContext? Context { get; set; } }

    // ── No-op when options not set ────────────────────────────────────────────

    [Fact]
    public async Task WhenOptionsNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = null };
        var ctx = MakeCtx();

        var next = CaptureAndReturn(out var capture);
        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, capture.Context);
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenChatClientNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SelfQueryBehavior { ChatClient = null, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();

        var next = CaptureAndReturn(out var capture);
        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, capture.Context);
    }

    [Fact]
    public async Task WhenUseSelfQueryFalse_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx(new RetrievalOptions { UseSelfQuery = false });

        var next = CaptureAndReturn(out var capture);
        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, capture.Context);
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Happy path: query + filters applied ───────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsQueryAndFilters_SetsEmbeddingOverrideAndFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant,
                """{"query":"security documents","filters":[{"key":"year","value":"2024"},{"key":"topic","value":"security"}]}""")]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();

        var next = CaptureAndReturn(out var capture);
        await sut.HandleAsync(ctx, ct, next);

        Assert.Equal("security documents", capture.Context!.Options.EmbeddingTextOverride);
        Assert.NotNull(capture.Context.Options.Filter);
    }

    // ── Empty filters: refined query applied, no filter set ──────────────────

    [Fact]
    public async Task WhenLlmReturnsNoFilters_SetsQueryOverrideOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant,
                """{"query":"refined query","filters":[]}""")]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();

        var next = CaptureAndReturn(out var capture);
        await sut.HandleAsync(ctx, ct, next);

        Assert.Equal("refined query", capture.Context!.Options.EmbeddingTextOverride);
        Assert.Null(capture.Context.Options.Filter);
    }

    // ── Invalid JSON: original query used, no filter ──────────────────────────

    [Fact]
    public async Task WhenLlmReturnsInvalidJson_OriginalQueryUsedNoFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "not json")]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();

        var next = CaptureAndReturn(out var capture);
        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, capture.Context);
    }
}
```

**Step 2: Run tests — confirm they fail**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SelfQueryBehaviorTests" -v minimal
```
Expected: FAIL.

**Step 3: Implement `SelfQueryBehavior`**

```csharp
// src/Rag.NET/SelfQuery/SelfQueryBehavior.cs
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Retrieval.Specifications;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;
using ZeroAlloc.Specification;

namespace Rag.NET.SelfQuery;

[Singleton]
public sealed class SelfQueryBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }
    [Inject(Required = false)] public SelfQueryOptions? SelfQueryOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseSelfQuery || ChatClient is null || SelfQueryOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var result = await ParseAsync(ctx.Query, ct).ConfigureAwait(false);

        return result.Match(
            output =>
            {
                var filter = BuildFilter(output.Filters);
                RagPipelineLog.SelfQueryCompleted(ctx.Logger, ctx.Query, output.Filters.Count);
                var updated = ctx with
                {
                    Options = ctx.Options with
                    {
                        UseSelfQuery = false,
                        EmbeddingTextOverride = output.Query,
                        Filter = filter ?? ctx.Options.Filter,
                    }
                };
                return next(updated, ct);
            },
            error =>
            {
                RagPipelineLog.SelfQueryFailed(ctx.Logger, ctx.Query, error);
                return next(ctx with { Options = ctx.Options with { UseSelfQuery = false } }, ct);
            }).ConfigureAwait(false);
    }

    private async ValueTask<Result<SelfQueryOutput>> ParseAsync(string question, CancellationToken ct)
    {
        try
        {
            var prompt = BuildPrompt(question);
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await ChatClient!.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var json = response.Text ?? "{}";

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? question : question;
            var filters = new List<KeyValuePair<string, string>>();

            if (root.TryGetProperty("filters", out var filtersProp))
            {
                foreach (var f in filtersProp.EnumerateArray())
                {
                    var key = f.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var value = f.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (key is not null && value is not null)
                        filters.Add(new KeyValuePair<string, string>(key, value));
                }
            }

            return Result<SelfQueryOutput>.Success(new SelfQueryOutput(query, filters));
        }
        catch (JsonException ex)
        {
            return Result<SelfQueryOutput>.Failure(ex.Message);
        }
    }

    private static ISpecification<SearchResult>? BuildFilter(IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        ISpecification<SearchResult>? result = null;
        foreach (var (key, value) in filters)
        {
            var spec = new HasTagSpec(key, value);
            result = result is null ? spec : result.And(spec);
        }
        return result;
    }

    private string BuildPrompt(string question)
    {
        if (SelfQueryOptions!.Schema is { Count: > 0 } schema)
        {
            var fields = string.Join(", ", schema.Select(a => $"{a.Name} ({a.Description})"));
            return $"""
                Parse this question into a search query and metadata filters.
                Available metadata fields: {fields}.
                Return JSON: {{"query": "...", "filters": [{{"key": "...", "value": "..."}}]}}.
                Only include filters for the listed fields. Filters may be an empty array.

                Question: {question}
                """;
        }

        return $"""
            Parse this question into a search query and metadata filters.
            Return JSON: {{"query": "...", "filters": [{{"key": "...", "value": "..."}}]}}.
            Filters may be an empty array.

            Question: {question}
            """;
    }
}
```

**Step 4: Run tests — confirm they pass**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SelfQueryBehaviorTests" -v minimal
```
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/SelfQuery/SelfQueryBehavior.cs \
        src/Rag.NET/SelfQuery/SelfQueryOutput.cs \
        tests/Rag.NET.Tests/Retrieval/Behaviors/SelfQueryBehaviorTests.cs
git commit -m "feat: implement SelfQueryBehavior with tests"
```

---

### Task 7: Wire `SelfQueryBehavior` into the retrieval pipeline

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`

**Step 1: Add behavior to `RetrievalPipelineBuilder`**

Add `typeof(SelfQueryBehavior)` as the **first entry** in `_types` (before `ResultCacheBehavior`):

```csharp
private readonly List<Type> _types =
[
    typeof(SelfQueryBehavior),          // ← add this line first
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
    typeof(VectorStoreBehavior),
];
```

**Step 2: Add `UseSelfQuery()` to `RagBuilder`**

Add after `UseLlmMetadataExtraction()`:

```csharp
/// <summary>
/// Registers <see cref="SelfQueryBehavior"/> in the retrieval pipeline.
/// When registered, the LLM parses each question into a refined semantic query
/// and a metadata filter expression before retrieval.
/// </summary>
/// <remarks>
/// Requires <c>IChatClient</c> to be registered in DI.
/// Per-call opt-out: pass <c>new RetrievalOptions { UseSelfQuery = false }</c>.
/// When <paramref name="schema"/> is provided, filtering is constrained to the listed fields.
/// </remarks>
/// <param name="schema">Optional list of filterable fields. When null, the LLM filters freely.</param>
public RagBuilder UseSelfQuery(IReadOnlyList<AttributeInfo>? schema = null)
{
    Services.AddSingleton(new SelfQueryOptions { Schema = schema });
    return this;
}
```

Add the required using:
```csharp
using Rag.NET.SelfQuery;
```

**Step 3: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: Build succeeded.

**Step 4: Run all tests**

```bash
dotnet test tests/Rag.NET.Tests/ -v minimal
```
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs \
        src/Rag.NET/DependencyInjection/RagBuilder.cs
git commit -m "feat: wire SelfQueryBehavior into retrieval pipeline; add UseSelfQuery()"
```

---

### Task 8: Update feature backlog

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Mark both features as done**

In the priority table at the bottom of `docs/reference/features.md`, change:

```markdown
| [ ] | LLM Metadata Extraction at Ingest | High | `IChatClient` |
```
to:
```markdown
| [x] | LLM Metadata Extraction at Ingest | High | `IChatClient` |
```

And:
```markdown
| [ ] | Self-Query Filtering | High | `IChatClient` + schema |
```
to:
```markdown
| [x] | Self-Query Filtering | High | `IChatClient` + schema |
```

Also add a **Status: ✅ Done** entry in the **Retrieval** section for Self-Query and in the **Document Enrichment** section for LLM Metadata Extraction (following the same pattern as the LLM-as-Judge entry in the Evaluation section).

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark LLM metadata extraction and self-query as done in feature backlog"
```

---

### Final: Run the full test suite

```bash
dotnet test -v minimal
```
Expected: All tests PASS across all projects.
