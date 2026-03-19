# Property Injection Pipeline — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the decorator chain with instance-based pipeline behaviors that own their own service dependencies via `[Inject]` property injection, producing lean contexts and a first-class extensibility model.

**Architecture:** Each behavior is a `[Singleton]` instance with `[Inject]` on the specific services it needs. `IngestionPipelineBuilder`/`RetrievalPipelineBuilder` resolve behaviors from DI at startup and chain them into a single `Func` delegate stored in `Pipeline<TContext, TResult>`. The chain is built once — no per-call allocation. Contexts carry only runtime inputs, accumulated state, and an `Extensions` dictionary. Facades (`PipelineIngestor`, `PipelineRetriever`) have `[Inject]` properties for their pipeline plus the services needed by `DeleteAsync`.

**Tech Stack:** C# / .NET 10, ZeroAlloc.Inject 0.11.3 (already installed), xUnit v3, NSubstitute

---

## Important Notes

- **Do NOT add `ZeroAlloc.Pipeline`** — we use `ZeroAlloc.Inject` (already installed) and write the chain manually.
- **`RetrievalContext` is a `sealed record`** — behaviors use `ctx with { Options = ... }` to derive modified contexts (Hyde, MultiQuery, Reranking all need this).
- **`IngestionContext` is a `sealed class`** — it has mutable `List<>` state.
- **`[Inject(Optional = true)]`** marks optional services. If ZeroAlloc.Inject 0.11.3 does not support `Optional` flag, fall back to `IServiceProvider` + `GetService<T>()` for optional services only.
- **Build command:** `dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q` (not `dotnet build` — see MSB3492 workaround).
- **Test command:** `dotnet test tests/Rag.NET.Tests --no-build -q`.
- **`_nextBm25DocId`** counter lives on `PipelineIngestor` (same as `DocumentIngestor`). `StorageBehavior` receives it via `ctx.GetNextBm25DocId`.
- **Existing `AddRagNet()` calls `services.AddRagNETServices()` first** — this is the ZeroAlloc.Inject-generated method that auto-registers parsers and chunking strategy. Do not remove it.

---

## Task 0: Update All NuGet Packages

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`
- Modify: any other `.csproj` files in the solution

### Step 1: Update packages to latest

```bash
dotnet tool update dotnet-outdated-tool -g 2>/dev/null || dotnet tool install dotnet-outdated-tool -g
dotnet outdated --upgrade src/Rag.NET/Rag.NET.csproj
dotnet outdated --upgrade tests/Rag.NET.Tests/Rag.NET.Tests.csproj
```

If `dotnet-outdated-tool` is unavailable, update manually in each `.csproj` by changing all `Version="9.*"` and `Version="10.*"` wildcards to their latest resolved versions. Check `ZeroAlloc.Inject` specifically — upgrade to latest if a newer stable version exists.

### Step 2: Build and verify

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q
```

Expected: build succeeds, all existing tests pass.

### Step 3: Commit

```bash
git add **/*.csproj
git commit -m "chore: update all NuGet packages to latest versions"
```

---

## Task 1: Behavior Interfaces + Pipeline Type + Builders

**Files:**
- Create: `src/Rag.NET/Ingestion/IIngestionBehavior.cs`
- Create: `src/Rag.NET/Retrieval/IRetrievalBehavior.cs`
- Create: `src/Rag.NET/Pipeline/Pipeline.cs`
- Create: `src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs`
- Create: `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs`

### Step 1: Create `IIngestionBehavior.cs`

```csharp
using Rag.NET.Models;

namespace Rag.NET.Ingestion;

/// <summary>
/// Contract for a single step in the ingestion pipeline.
/// Implementations are registered as singletons and own their service dependencies via [Inject].
/// </summary>
public interface IIngestionBehavior
{
    ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next);
}
```

### Step 2: Create `IRetrievalBehavior.cs`

```csharp
using Rag.NET.Models;

namespace Rag.NET.Retrieval;

/// <summary>
/// Contract for a single step in the retrieval pipeline.
/// Implementations are registered as singletons and own their service dependencies via [Inject].
/// </summary>
public interface IRetrievalBehavior
{
    ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx,
        CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next);
}
```

### Step 3: Create `Pipeline.cs`

```csharp
namespace Rag.NET.Pipeline;

/// <summary>
/// Wraps a delegate chain built once at startup.
/// ExecuteAsync has zero allocation on the hot path — all closures are captured at build time.
/// </summary>
public sealed class Pipeline<TContext, TResult>(
    Func<TContext, CancellationToken, ValueTask<TResult>> chain)
{
    public ValueTask<TResult> ExecuteAsync(TContext ctx, CancellationToken ct) => chain(ctx, ct);
}
```

### Step 4: Create `IngestionPipelineBuilder.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Pipeline;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Builds the ingestion Pipeline from an ordered list of behavior types.
/// Call Add/Replace in AddRagNet() to extend or override default behaviors.
/// </summary>
public sealed class IngestionPipelineBuilder
{
    private readonly List<Type> _types =
    [
        typeof(OverwriteBehavior),
        typeof(ParseBehavior),
        typeof(ChunkingBehavior),
        typeof(MetadataBehavior),
        typeof(ParentDocumentIngestionBehavior),
        typeof(EmbeddingBehavior),
        typeof(StorageBehavior),
    ];

    /// <summary>Insert a custom behavior after an existing one.</summary>
    public IngestionPipelineBuilder Add<T>(Type? after = null, Type? before = null)
        where T : IIngestionBehavior
    {
        var idx = after is not null ? _types.IndexOf(after) + 1
                : before is not null ? _types.IndexOf(before)
                : _types.Count;

        if (idx < 0) idx = _types.Count;
        _types.Insert(idx, typeof(T));
        return this;
    }

    /// <summary>Replace an existing behavior with a different implementation.</summary>
    public IngestionPipelineBuilder Replace<TOld, TNew>()
        where TNew : IIngestionBehavior
    {
        var idx = _types.IndexOf(typeof(TOld));
        if (idx >= 0) _types[idx] = typeof(TNew);
        return this;
    }

    /// <summary>
    /// Resolves all behaviors from DI and builds a single chained delegate.
    /// Called once at startup — no per-call allocation.
    /// </summary>
    public Pipeline<IngestionContext, IngestionResult> Build(IServiceProvider sp)
    {
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> chain =
            static (ctx, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

        for (var i = _types.Count - 1; i >= 0; i--)
        {
            var behavior = (IIngestionBehavior)sp.GetRequiredService(_types[i]);
            var next = chain;
            chain = (ctx, ct) => behavior.HandleAsync(ctx, ct, next);
        }

        return new Pipeline<IngestionContext, IngestionResult>(chain);
    }
}
```

### Step 5: Create `RetrievalPipelineBuilder.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Builds the retrieval Pipeline from an ordered list of behavior types.
/// </summary>
public sealed class RetrievalPipelineBuilder
{
    private readonly List<Type> _types =
    [
        typeof(ResultCacheBehavior),
        typeof(LostInTheMiddleBehavior),
        typeof(MmrBehavior),
        typeof(RedundancyFilterBehavior),
        typeof(ParentDocumentRetrievalBehavior),
        typeof(RerankingBehavior),
        typeof(MultiQueryBehavior),
        typeof(HydeBehavior),
        typeof(EmbeddingCacheBehavior),
        typeof(VectorStoreBehavior),
    ];

    public RetrievalPipelineBuilder Add<T>(Type? after = null, Type? before = null)
        where T : IRetrievalBehavior
    {
        var idx = after is not null ? _types.IndexOf(after) + 1
                : before is not null ? _types.IndexOf(before)
                : _types.Count;

        if (idx < 0) idx = _types.Count;
        _types.Insert(idx, typeof(T));
        return this;
    }

    public RetrievalPipelineBuilder Replace<TOld, TNew>()
        where TNew : IRetrievalBehavior
    {
        var idx = _types.IndexOf(typeof(TOld));
        if (idx >= 0) _types[idx] = typeof(TNew);
        return this;
    }

    public Pipeline<RetrievalContext, IReadOnlyList<SearchResult>> Build(IServiceProvider sp)
    {
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> chain =
            static (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);

        for (var i = _types.Count - 1; i >= 0; i--)
        {
            var behavior = (IRetrievalBehavior)sp.GetRequiredService(_types[i]);
            var next = chain;
            chain = (ctx, ct) => behavior.HandleAsync(ctx, ct, next);
        }

        return new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(chain);
    }
}
```

### Step 6: Build to verify

```bash
dotnet msbuild src/Rag.NET/Rag.NET.csproj -q
```

Expected: Build succeeded. (Behaviors don't exist yet — the builder `using` directives will fail; comment out the behavior `using` imports in the builders temporarily if needed, or just leave as-is and fix in Task 2.)

### Step 7: Commit

```bash
git add src/Rag.NET/Ingestion/IIngestionBehavior.cs src/Rag.NET/Retrieval/IRetrievalBehavior.cs src/Rag.NET/Pipeline/Pipeline.cs src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs
git commit -m "feat: add behavior interfaces, Pipeline<T> wrapper, and pipeline builder types"
```

---

## Task 2: Context Models

**Files:**
- Create: `src/Rag.NET/Ingestion/IngestionContext.cs`
- Create: `src/Rag.NET/Retrieval/RetrievalContext.cs`

### Step 1: Create `IngestionContext.cs`

Note: Services have been REMOVED. Each behavior injects its own services. `Extensions` allows custom behaviors to carry state through the chain.

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Ingestion;

/// <summary>
/// Mutable per-call context for the ingestion pipeline.
/// Contains only runtime inputs, accumulated state, and an extension bag.
/// Services live on the behaviors, not here.
/// </summary>
public sealed class IngestionContext
{
    // ── Runtime inputs ────────────────────────────────────────────────────
    public required Stream Stream                   { get; init; }
    public required DocumentMetadata Metadata       { get; init; }
    public IngestionOptions? Options                { get; init; }
    public IProgress<IngestionProgress>? Progress  { get; init; }

    // ── Accumulated state (populated by behaviors in order) ───────────────
    public List<DocumentSection> Sections          { get; } = [];
    public List<TextChunk> Chunks                  { get; } = [];
    public List<EmbeddedChunk> EmbeddedChunks      { get; } = [];

    // ── Counter delegate — facade provides this so StorageBehavior
    //    assigns unique BM25 doc IDs across concurrent ingest calls ─────────
    public required Func<int> GetNextBm25DocId     { get; init; }

    // ── Extension bag — custom behaviors store/read state here ───────────
    public Dictionary<string, object?> Extensions  { get; } = new();
}
```

### Step 2: Create `RetrievalContext.cs`

Note: `sealed record` so behaviors can use `ctx with { ... }` to pass modified options downstream.

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Immutable per-call context for the retrieval pipeline.
/// Use <c>ctx with { ... }</c> to derive modified contexts in behaviors.
/// Services live on the behaviors, not here.
/// </summary>
public sealed record RetrievalContext
{
    // ── Runtime inputs ────────────────────────────────────────────────────
    public required string Query                   { get; init; }
    public required RetrievalOptions Options       { get; init; }

    // ── Logger — passed from facade for structured logging in behaviors ───
    public ILogger Logger                          { get; init; } = NullLogger.Instance;

    // ── Extension bag — custom behaviors store/read state here ───────────
    public Dictionary<string, object?> Extensions  { get; init; } = new();
}
```

### Step 3: Build to verify

```bash
dotnet msbuild src/Rag.NET/Rag.NET.csproj -q
```

Expected: Build succeeded, 0 errors.

### Step 4: Commit

```bash
git add src/Rag.NET/Ingestion/IngestionContext.cs src/Rag.NET/Retrieval/RetrievalContext.cs
git commit -m "feat: add lean pipeline context models with Extensions bag"
```

---

## Task 3: Ingestion Behaviors — Overwrite, Parse, Chunking, Metadata

**Files:**
- Create: `src/Rag.NET/Ingestion/Behaviors/OverwriteBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/ChunkingBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/Behaviors/IngestionBehaviorTests.cs`

### Step 1: Create `OverwriteBehavior.cs`

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class OverwriteBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Optional = true)] public IRagDataManager? DataManager { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.Options?.Overwrite == true)
        {
            await VectorStore.DeleteByDocumentIdAsync(ctx.Metadata.DocumentId, ct).ConfigureAwait(false);
            Bm25Index.Remove(ctx.Metadata.DocumentId);
            DataManager?.Remove(ctx.Metadata.DocumentId);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

> **Note on `[Inject(Optional = true)]`:** If ZeroAlloc.Inject 0.11.3 does not support the `Optional` parameter on `[Inject]`, change the property to be populated via `IServiceProvider` directly. Inject `IServiceProvider Sp { get; set; }` and resolve optional services lazily: `private IRagDataManager? DataManager => Sp.GetService<IRagDataManager>()`. Check the ZeroAlloc.Inject docs or source to confirm.

### Step 2: Create `ParseBehavior.cs`

Parse + chunk in one pass (matching the original `ParseAndChunkAsync`). Builds heading breadcrumbs inline.

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParseBehavior : IIngestionBehavior
{
    [Inject] public IEnumerable<IDocumentParser> Parsers { get; set; } = null!;
    [Inject] public IChunkingStrategy ChunkingStrategy { get; set; } = null!;
    [Inject] public ChunkingOptions ChunkingOptions { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var parser = Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{ctx.Metadata.ContentType}'.");

        if (ctx.Options is { } opt && opt.GetType().GetProperty("ParentOptions") is not null)
        {
            // Parent-document check: stream must be seekable for two-pass parsing
        }

        var headingBreadcrumbs = new string?[6];

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            Dictionary<string, string>? headingMetadata = null;

            if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
            {
                headingBreadcrumbs[level - 1] = section.Heading;
                for (var idx = level; idx < 6; idx++) headingBreadcrumbs[idx] = null;

                var parts = new List<string>(level);
                foreach (var h in headingBreadcrumbs[..level])
                    if (h is not null) parts.Add(h);

                headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = section.Heading,
                    ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["heading_breadcrumb"] = string.Join(" > ", parts),
                };
            }

            await foreach (var chunk in ChunkingStrategy.ChunkAsync(section, ChunkingOptions, ct).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);

                ctx.Chunks.Add(chunk);
            }

            ctx.Sections.Add(section);
        }

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Parsing,
            DocumentId = ctx.Metadata.DocumentId,
            Message = "Parsing complete",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

### Step 3: Create `ChunkingBehavior.cs`

Reports chunking progress and short-circuits when no chunks produced.

```csharp
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ChunkingBehavior : IIngestionBehavior
{
    public ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.Chunks.Count == 0)
            return ValueTask.FromResult(new IngestionResult
            {
                DocumentId = ctx.Metadata.DocumentId,
                ChunksStored = 0,
            });

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Chunking,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.Chunks.Count,
            Total = ctx.Chunks.Count,
            Message = $"Chunked into {ctx.Chunks.Count} chunks",
        });

        return next(ctx, ct);
    }
}
```

### Step 4: Create `MetadataBehavior.cs`

```csharp
using System.Runtime.InteropServices;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class MetadataBehavior : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        foreach (ref var chunk in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            foreach (var tag in ctx.Metadata.Tags)
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            chunk.Metadata.TryAdd("document_id", ctx.Metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", ctx.Metadata.FileName);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

### Step 5: Write tests

Create `tests/Rag.NET.Tests/Ingestion/Behaviors/IngestionBehaviorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class IngestionBehaviorTests
{
    private static IngestionContext MakeCtx(
        DocumentMetadata? metadata = null,
        IngestionOptions? options = null)
    {
        return new IngestionContext
        {
            Stream = new MemoryStream("test"u8.ToArray()),
            Metadata = metadata ?? new DocumentMetadata
            {
                DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain",
            },
            Options = options,
            GetNextBm25DocId = () => 1,
        };
    }

    private static ValueTask<IngestionResult> EmptyNext(IngestionContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    // ── OverwriteBehavior ────────────────────────────────────────────────

    [Fact]
    public async Task OverwriteBehavior_WhenOverwriteFalse_DoesNotDeleteFromStores()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var sut = new OverwriteBehavior { VectorStore = vectorStore, Bm25Index = bm25 };
        var ctx = MakeCtx(options: new() { Overwrite = false });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, EmptyNext);

        await vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        bm25.DidNotReceive().Remove(Arg.Any<string>());
    }

    [Fact]
    public async Task OverwriteBehavior_WhenOverwriteTrue_DeletesFromAllStores()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();
        var sut = new OverwriteBehavior
        {
            VectorStore = vectorStore, Bm25Index = bm25, DataManager = dataManager,
        };
        var ctx = MakeCtx(options: new() { Overwrite = true });
        var ct = TestContext.Current.CancellationToken;

        await sut.HandleAsync(ctx, ct, EmptyNext);

        await vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", ct);
        bm25.Received(1).Remove("doc-1");
        dataManager.Received(1).Remove("doc-1");
    }

    // ── ChunkingBehavior ─────────────────────────────────────────────────

    [Fact]
    public async Task ChunkingBehavior_WhenNoChunks_ShortCircuitsWithoutCallingNext()
    {
        var sut = new ChunkingBehavior();
        var ctx = MakeCtx();
        var nextCalled = false;

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, t) =>
        {
            nextCalled = true;
            return EmptyNext(c, t);
        });

        Assert.False(nextCalled);
        Assert.Equal(0, result.ChunksStored);
    }

    [Fact]
    public async Task ChunkingBehavior_WhenChunksExist_CallsNext()
    {
        var sut = new ChunkingBehavior();
        var ctx = MakeCtx();
        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 });
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, t) =>
        {
            nextCalled = true;
            return EmptyNext(c, t);
        });

        Assert.True(nextCalled);
    }

    // ── MetadataBehavior ─────────────────────────────────────────────────

    [Fact]
    public async Task MetadataBehavior_AppliesTagsAndDocumentIdentityToAllChunks()
    {
        var sut = new MetadataBehavior();
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc-1", FileName = "file.txt", ContentType = "text/plain",
            Tags = { ["env"] = "prod" },
        };
        var ctx = MakeCtx(metadata: metadata);
        ctx.Chunks.Add(new TextChunk { Text = "chunk1", DocumentId = "doc-1", ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "chunk2", DocumentId = "doc-1", ChunkIndex = 1 });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, EmptyNext);

        Assert.Equal("prod", ctx.Chunks[0].Metadata["env"]);
        Assert.Equal("doc-1", ctx.Chunks[0].Metadata["document_id"]);
        Assert.Equal("file.txt", ctx.Chunks[0].Metadata["file_name"]);
        Assert.Equal("prod", ctx.Chunks[1].Metadata["env"]);
    }
}
```

### Step 6: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "IngestionBehaviorTests"
```

Expected: all tests pass.

### Step 7: Commit

```bash
git add src/Rag.NET/Ingestion/Behaviors/ tests/Rag.NET.Tests/Ingestion/Behaviors/
git commit -m "feat: add ingestion behaviors - Overwrite, Parse, Chunking, Metadata"
```

---

## Task 4: Ingestion Behaviors — ParentDocument, Embedding, Storage

**Files:**
- Create: `src/Rag.NET/Ingestion/Behaviors/ParentDocumentIngestionBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs`
- Modify: `tests/Rag.NET.Tests/Ingestion/Behaviors/IngestionBehaviorTests.cs` (append new test class)

### Step 1: Create `ParentDocumentIngestionBehavior.cs`

```csharp
using System.Runtime.InteropServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParentDocumentIngestionBehavior : IIngestionBehavior
{
    [Inject(Optional = true)] public IParentChunkStore? ParentStore { get; set; }
    [Inject(Optional = true)] public ParentDocumentOptions? ParentOptions { get; set; }
    [Inject] public IEnumerable<IDocumentParser> Parsers { get; set; } = null!;
    [Inject] public IChunkingStrategy ChunkingStrategy { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ParentOptions is null || ParentStore is null)
            return await next(ctx, ct).ConfigureAwait(false);

        if (!ctx.Stream.CanSeek)
            throw new InvalidOperationException(
                "Parent-document retrieval requires a seekable stream. Wrap the stream in a MemoryStream before calling IngestAsync.");

        ctx.Stream.Position = 0;

        var parentChunkingOptions = new ChunkingOptions
        {
            MaxChunkSize = ParentOptions.ParentChunkSize,
            Overlap = ParentOptions.ParentOverlap,
        };

        var parser = Parsers.First(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"));
        var parentBoundaries = new List<(int start, int end)>();
        var parentIndex = 0;

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            await foreach (var parentChunk in ChunkingStrategy.ChunkAsync(section, parentChunkingOptions, ct).ConfigureAwait(false))
            {
                ParentStore.Add(ctx.Metadata.DocumentId, parentIndex, parentChunk.Text);
                parentBoundaries.Add((parentChunk.StartPosition, parentChunk.EndPosition));
                parentIndex++;
            }
        }

        foreach (ref readonly var child in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            var pIdx = ParentChunkKeyHelper.FindParentIndex(parentBoundaries, child.StartPosition);
            child.Metadata[ParentChunkKeyHelper.ParentKeyMetadata] =
                ParentChunkKeyHelper.GetParentKey(ctx.Metadata.DocumentId, pIdx);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

### Step 2: Create `EmbeddingBehavior.cs`

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class EmbeddingBehavior : IIngestionBehavior
{
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var texts = ctx.Chunks.Select(c => c.Text).ToList();
        var embeddings = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);

        ctx.EmbeddedChunks.AddRange(
            ctx.Chunks.Zip(embeddings, (chunk, embedding) =>
                new EmbeddedChunk { Chunk = chunk, Embedding = embedding.Vector }));

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Embedding,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.EmbeddedChunks.Count,
            Total = ctx.EmbeddedChunks.Count,
            Message = $"Generated {ctx.EmbeddedChunks.Count} embeddings",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

### Step 3: Create `StorageBehavior.cs` (terminal)

```csharp
using System.Runtime.InteropServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class StorageBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Optional = true)] public IRagDataManager? DataManager { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        await VectorStore.StoreAsync(ctx.EmbeddedChunks, ct).ConfigureAwait(false);

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Storing,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.EmbeddedChunks.Count,
            Total = ctx.EmbeddedChunks.Count,
            Message = $"Stored {ctx.EmbeddedChunks.Count} chunks",
        });

        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(ctx.EmbeddedChunks))
            Bm25Index.Add(ctx.GetNextBm25DocId(), ec.Chunk);

        DataManager?.Add(ctx.Metadata, ctx.Chunks);

        // Terminal — does not call next
        return new IngestionResult
        {
            DocumentId = ctx.Metadata.DocumentId,
            ChunksStored = ctx.EmbeddedChunks.Count,
        };
    }
}
```

### Step 4: Append tests to `IngestionBehaviorTests.cs`

Add this class after the existing `IngestionBehaviorTests` class:

```csharp
public class StorageAndEmbeddingBehaviorTests
{
    private static IngestionContext MakeCtx(
        IVectorStore? vectorStore = null,
        IBm25Index? bm25 = null,
        IRagDataManager? dataManager = null)
    {
        return new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt" },
            GetNextBm25DocId = (() => { var n = 0; return () => ++n; })(),
            VectorStore_Unused = vectorStore ?? Substitute.For<IVectorStore>(),
        };
        // Note: context no longer carries services — tests instantiate behaviors directly
    }

    [Fact]
    public async Task EmbeddingBehavior_PopulatesEmbeddedChunksFromChunks()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>([0.1f, 0.2f])]));

        var sut = new EmbeddingBehavior { Embedder = embedder };
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt" },
            GetNextBm25DocId = () => 1,
        };
        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, t) => ValueTask.FromResult(new IngestionResult { DocumentId = "doc-1" }));

        Assert.Single(ctx.EmbeddedChunks);
        Assert.Equal("hello", ctx.EmbeddedChunks[0].Chunk.Text);
    }

    [Fact]
    public async Task StorageBehavior_StoresAndReturnResult()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var counter = 0;
        var sut = new StorageBehavior { VectorStore = vectorStore, Bm25Index = bm25 };
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt" },
            GetNextBm25DocId = () => ++counter,
        };
        var chunk = new TextChunk { Text = "chunk", DocumentId = "doc-1", ChunkIndex = 0 };
        ctx.Chunks.Add(chunk);
        ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = [0.1f] });
        var ct = TestContext.Current.CancellationToken;

        var result = await sut.HandleAsync(ctx, ct,
            (c, t) => ValueTask.FromResult(new IngestionResult { DocumentId = "doc-1" }));

        await vectorStore.Received(1).StoreAsync(ctx.EmbeddedChunks, ct);
        bm25.Received(1).Add(1, chunk);
        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
    }

    [Fact]
    public async Task StorageBehavior_IsTerminal_DoesNotCallNext()
    {
        var sut = new StorageBehavior
        {
            VectorStore = Substitute.For<IVectorStore>(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt" },
            GetNextBm25DocId = () => 1,
        };
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, t) =>
        {
            nextCalled = true;
            return ValueTask.FromResult(new IngestionResult { DocumentId = "doc-1" });
        });

        Assert.False(nextCalled);
    }
}
```

### Step 5: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "IngestionBehaviorTests|StorageAndEmbeddingBehaviorTests"
```

Expected: all tests pass.

### Step 6: Commit

```bash
git add src/Rag.NET/Ingestion/Behaviors/ tests/Rag.NET.Tests/Ingestion/Behaviors/
git commit -m "feat: add ingestion behaviors - ParentDocument, Embedding, Storage"
```

---

## Task 5: PipelineIngestor

**Files:**
- Create: `src/Rag.NET/Ingestion/PipelineIngestor.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs`

### Step 1: Write the failing test

```csharp
// tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorTests
{
    private static PipelineIngestor CreateSut(
        IVectorStore? vectorStore = null,
        IBm25Index? bm25 = null,
        IParentChunkStore? parentStore = null,
        IRagDataManager? dataManager = null,
        Pipeline<IngestionContext, IngestionResult>? pipeline = null)
    {
        return new PipelineIngestor
        {
            Pipeline = pipeline ?? new Pipeline<IngestionContext, IngestionResult>(
                (ctx, _) => ValueTask.FromResult(
                    new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
            VectorStore = vectorStore ?? Substitute.For<IVectorStore>(),
            Bm25Index = bm25 ?? Substitute.For<IBm25Index>(),
            ParentStore = parentStore,
            DataManager = dataManager,
        };
    }

    [Fact]
    public async Task IngestAsync_CreatesContextAndExecutesPipeline()
    {
        var capturedCtx = (IngestionContext?)null;
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((ctx, _) =>
        {
            capturedCtx = ctx;
            return ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 3 });
        });
        var sut = CreateSut(pipeline: pipeline);
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        using var stream = new MemoryStream("hello"u8.ToArray());
        var ct = TestContext.Current.CancellationToken;

        var result = await sut.IngestAsync(stream, metadata, cancellationToken: ct);

        Assert.NotNull(capturedCtx);
        Assert.Same(stream, capturedCtx!.Stream);
        Assert.Same(metadata, capturedCtx.Metadata);
        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(3, result.ChunksStored);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFromAllRegisteredStores()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var parentStore = Substitute.For<IParentChunkStore>();
        var dataManager = Substitute.For<IRagDataManager>();
        var sut = CreateSut(vectorStore: vectorStore, bm25: bm25, parentStore: parentStore, dataManager: dataManager);
        var ct = TestContext.Current.CancellationToken;

        await sut.DeleteAsync("doc-1", ct);

        await vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", ct);
        bm25.Received(1).Remove("doc-1");
        parentStore.Received(1).Remove("doc-1");
        dataManager.Received(1).Remove("doc-1");
    }

    [Fact]
    public async Task DeleteAsync_WhenOptionalStoresNull_DoesNotThrow()
    {
        var sut = CreateSut();

        // Should not throw
        await sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);
    }
}
```

### Step 2: Run to verify it fails

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineIngestorTests"
```

Expected: FAIL — `PipelineIngestor` does not exist.

### Step 3: Create `PipelineIngestor.cs`

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion;

/// <summary>
/// Thin facade over the ingestion pipeline.
/// Services needed by DeleteAsync are injected directly; the pipeline handle is injected for IngestAsync.
/// Replaces DocumentIngestor.
/// </summary>
[Singleton(As = typeof(IIngestor))]
public sealed class PipelineIngestor : IIngestor
{
    [Inject] public Pipeline<IngestionContext, IngestionResult> Pipeline { get; set; } = null!;
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Optional = true)] public IParentChunkStore? ParentStore { get; set; }
    [Inject(Optional = true)] public IRagDataManager? DataManager { get; set; }

    private int _nextBm25DocId;

    public Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new IngestionContext
        {
            Stream = document,
            Metadata = metadata,
            Options = options,
            Progress = progress,
            GetNextBm25DocId = () => System.Threading.Interlocked.Increment(ref _nextBm25DocId),
        };

        return Pipeline.ExecuteAsync(ctx, cancellationToken).AsTask();
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await VectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        Bm25Index.Remove(documentId);
        ParentStore?.Remove(documentId);
        DataManager?.Remove(documentId);
    }
}
```

### Step 4: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineIngestorTests"
```

Expected: all tests pass.

### Step 5: Commit

```bash
git add src/Rag.NET/Ingestion/PipelineIngestor.cs tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs
git commit -m "feat: add PipelineIngestor with property-injected pipeline and delete support"
```

---

## Task 6: Retrieval Behaviors — ResultCache, LostInMiddle, Mmr, RedundancyFilter, ParentDocument

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/ResultCacheBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/LostInTheMiddleBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/MmrBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/RedundancyFilterBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/ParentDocumentRetrievalBehavior.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs`

### Step 1: Create `ResultCacheBehavior.cs`

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class ResultCacheBehavior : IRetrievalBehavior
{
    [Inject(Optional = true)] public HybridCache? Cache { get; set; }
    [Inject(Optional = true)] public CachingOptions? CachingOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCacheResult || Cache is null || CachingOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var cacheKey = CacheKeyGenerator.ForResult(ctx.Query, ctx.Options);

        try
        {
            var results = await Cache.GetOrCreateAsync(
                cacheKey,
                async ct2 =>
                {
                    RagPipelineLog.ResultCacheMiss(ctx.Logger, ctx.Query);
                    var inner = await next(ctx, ct2).ConfigureAwait(false);
                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = CachingOptions.ResultTtl },
                cancellationToken: ct).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ResultCacheFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }
}
```

### Step 2: Create `LostInTheMiddleBehavior.cs`

```csharp
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class LostInTheMiddleBehavior : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        return ctx.Options.UseLostInTheMiddleReordering ? LostInTheMiddleReorderer.Reorder(results) : results;
    }
}
```

### Step 3: Create `MmrBehavior.cs`

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class MmrBehavior : IRetrievalBehavior
{
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseMmr)
            return await next(ctx, ct).ConfigureAwait(false);

        var candidateCount = ctx.Options.MmrCandidateCount ?? ctx.Options.TopK * 3;
        if (candidateCount < ctx.Options.TopK)
            RagPipelineLog.MmrCandidateCountLessThanTopK(ctx.Logger, candidateCount, ctx.Options.TopK);

        var candidates = await next(ctx with { Options = ctx.Options with { TopK = candidateCount, UseMmr = false } }, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return candidates;

        try
        {
            var selected = await MmrSelector.SelectAsync(
                ctx.Query, candidates, Embedder,
                topK: ctx.Options.TopK,
                lambda: ctx.Options.MmrLambda,
                cancellationToken: ct).ConfigureAwait(false);

            RagPipelineLog.MmrSelectionCompleted(ctx.Logger, candidates.Count, selected.Count);
            return selected;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.MmrSelectionFailed(ctx.Logger, ctx.Query, ex);
            return candidates;
        }
    }
}
```

### Step 4: Create `RedundancyFilterBehavior.cs`

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class RedundancyFilterBehavior : IRetrievalBehavior
{
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        if (!ctx.Options.UseRedundancyFilter) return results;

        try
        {
            var filtered = await RedundancyFilter.FilterAsync(results, Embedder, ctx.Options.RedundancyThreshold, ct)
                .ConfigureAwait(false);
            RagPipelineLog.RedundancyFilterCompleted(ctx.Logger, results.Count, filtered.Count);
            return filtered;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RedundancyFilteringFailed(ctx.Logger, ctx.Query, ex);
            return results;
        }
    }
}
```

### Step 5: Create `ParentDocumentRetrievalBehavior.cs`

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class ParentDocumentRetrievalBehavior : IRetrievalBehavior
{
    private const int OverFetchMultiplier = 3;

    [Inject(Optional = true)] public IParentChunkStore? ParentStore { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseParentDocument || ParentStore is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var childResults = await next(
            ctx with { Options = ctx.Options with { TopK = ctx.Options.TopK * OverFetchMultiplier, UseParentDocument = false } },
            ct).ConfigureAwait(false);

        try
        {
            var parentGroups = new Dictionary<string, (SearchResult best, double maxScore)>(StringComparer.Ordinal);
            var noParentResults = new List<SearchResult>();

            foreach (var result in childResults)
            {
                if (!result.Chunk.Metadata.TryGetValue(ParentChunkKeyHelper.ParentKeyMetadata, out var parentKey))
                {
                    noParentResults.Add(result);
                    continue;
                }
                if (!parentGroups.TryGetValue(parentKey, out var existing) || result.Score > existing.maxScore)
                    parentGroups[parentKey] = (result, result.Score);
            }

            var results = new List<SearchResult>(parentGroups.Count + noParentResults.Count);

            foreach (var (parentKey, (best, maxScore)) in parentGroups)
            {
                var parts = parentKey.Split(':');
                if (parts.Length == 2
                    && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var pIdx)
                    && ParentStore.TryGet(parts[0], pIdx, out var parentText))
                {
                    results.Add(new SearchResult { Chunk = best.Chunk with { Text = parentText! }, Score = maxScore });
                }
                else
                {
                    results.Add(best);
                }
            }

            results.AddRange(noParentResults);
            results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            if (results.Count > ctx.Options.TopK)
                results.RemoveRange(ctx.Options.TopK, results.Count - ctx.Options.TopK);

            RagPipelineLog.ParentDocumentRetrieved(ctx.Logger, ctx.Query, childResults.Count, results.Count);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ParentDocumentFailed(ctx.Logger, ctx.Query, ex);
            return childResults;
        }
    }
}
```

### Step 6: Write tests

Create `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class RetrievalBehaviorTests
{
    private static RetrievalContext MakeCtx(
        string query = "test",
        RetrievalOptions? options = null) =>
        new()
        {
            Query = query,
            Options = options ?? new RetrievalOptions(),
        };

    private static IReadOnlyList<SearchResult> MakeResults(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new SearchResult
            {
                Chunk = new TextChunk { Text = $"chunk{i}", DocumentId = "doc", ChunkIndex = i },
                Score = 1.0 - i * 0.1,
            })
            .ToList().AsReadOnly();

    // ── LostInTheMiddleBehavior ──────────────────────────────────────────

    [Fact]
    public async Task LostInTheMiddle_WhenFlagFalse_ReturnsResultsUnchanged()
    {
        var sut = new LostInTheMiddleBehavior();
        var ctx = MakeCtx(options: new() { UseLostInTheMiddleReordering = false });
        var expected = MakeResults(3);

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }

    // ── MmrBehavior ──────────────────────────────────────────────────────

    [Fact]
    public async Task Mmr_WhenFlagFalse_PassesThroughToNext()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var sut = new MmrBehavior { Embedder = embedder };
        var ctx = MakeCtx(options: new() { UseMmr = false });
        var expected = MakeResults(3);

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
        await embedder.DidNotReceive().GenerateAsync(Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── RedundancyFilterBehavior ─────────────────────────────────────────

    [Fact]
    public async Task RedundancyFilter_WhenFlagFalse_ReturnsNextResultsUnchanged()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var sut = new RedundancyFilterBehavior { Embedder = embedder };
        var ctx = MakeCtx(options: new() { UseRedundancyFilter = false });
        var expected = MakeResults(3);

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }

    // ── ParentDocumentRetrievalBehavior ──────────────────────────────────

    [Fact]
    public async Task ParentDocument_WhenStoreNull_ReturnsNextResults()
    {
        var sut = new ParentDocumentRetrievalBehavior { ParentStore = null };
        var ctx = MakeCtx(options: new() { UseParentDocument = true });
        var expected = MakeResults(3);

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task ParentDocument_WhenFlagFalse_ReturnsNextResults()
    {
        var parentStore = Substitute.For<IParentChunkStore>();
        var sut = new ParentDocumentRetrievalBehavior { ParentStore = parentStore };
        var ctx = MakeCtx(options: new() { UseParentDocument = false });
        var expected = MakeResults(3);

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }
}
```

### Step 7: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "RetrievalBehaviorTests"
```

Expected: all tests pass.

### Step 8: Commit

```bash
git add src/Rag.NET/Retrieval/Behaviors/ tests/Rag.NET.Tests/Retrieval/Behaviors/
git commit -m "feat: add retrieval behaviors - ResultCache, LostInMiddle, Mmr, RedundancyFilter, ParentDoc"
```

---

## Task 7: Retrieval Behaviors — Reranking, MultiQuery, Hyde, EmbeddingCache, VectorStore

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/RerankingBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/MultiQueryBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/HydeBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs`
- Modify: `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs` (append)

### Step 1: Create `RerankingBehavior.cs`

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class RerankingBehavior : IRetrievalBehavior
{
    [Inject(Optional = true)] public IReranker? Reranker { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseReranking || Reranker is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var candidateCount = ctx.Options.CandidateCount ?? ctx.Options.TopK * 3;
        var searchResults = await next(
            ctx with { Options = ctx.Options with { TopK = candidateCount, UseReranking = false } },
            ct).ConfigureAwait(false);

        try
        {
            var reranked = await Reranker.RerankAsync(ctx.Query, searchResults, ct).ConfigureAwait(false);
            var results = reranked
                .OrderByDescending(r => r.RelevanceScore)
                .Take(ctx.Options.TopK)
                .Select(r => r.SearchResult)
                .ToList()
                .AsReadOnly();
            RagPipelineLog.RerankingCompleted(ctx.Logger, searchResults.Count, results.Count);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RerankingFailed(ctx.Logger, ctx.Query, ex);
            return searchResults;
        }
    }
}
```

### Step 2: Create `MultiQueryBehavior.cs`

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class MultiQueryBehavior : IRetrievalBehavior
{
    [Inject(Optional = true)] public IQueryExpander? QueryExpander { get; set; }
    [Inject(Optional = true)] public MultiQueryOptions? MultiQueryOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseMultiQuery || QueryExpander is null)
            return await next(ctx, ct).ConfigureAwait(false);

        IReadOnlyList<string> variants;
        try
        {
            var variantCount = MultiQueryOptions?.VariantCount ?? new Models.Options.MultiQueryOptions().VariantCount;
            variants = await QueryExpander.ExpandAsync(ctx.Query, variantCount, ct).ConfigureAwait(false);
            RagPipelineLog.QueryExpansionCompleted(ctx.Logger, ctx.Query, variants.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.QueryExpansionFailed(ctx.Logger, ctx.Query, ex);
            variants = [];
        }

        var allQueries = new List<string>(variants.Count + 1) { ctx.Query };
        allQueries.AddRange(variants);

        var tasks = allQueries.Select(q => SafeRetrieveAsync(q, ctx, ct, next)).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        return allResults
            .Where(r => r is not null)
            .SelectMany(r => r!)
            .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(ctx.Options.TopK)
            .ToList()
            .AsReadOnly();
    }

    private static async Task<IReadOnlyList<SearchResult>?> SafeRetrieveAsync(
        string query, RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        try
        {
            return await next(ctx with { Query = query, Options = ctx.Options with { UseMultiQuery = false } }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.QueryRetrievalFailed(ctx.Logger, query, ex);
            return null;
        }
    }
}
```

### Step 3: Create `HydeBehavior.cs`

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class HydeBehavior : IRetrievalBehavior
{
    [Inject(Optional = true)] public IHypotheticalDocumentGenerator? HydeGenerator { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseHyde || HydeGenerator is null)
            return await next(ctx, ct).ConfigureAwait(false);

        try
        {
            var doc = await HydeGenerator.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
            RagPipelineLog.HydeDocumentGenerated(ctx.Logger, ctx.Query, doc.Length);
            return await next(
                ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = doc } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.HydeGenerationFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx with { Options = ctx.Options with { UseHyde = false } }, ct).ConfigureAwait(false);
        }
    }
}
```

### Step 4: Create `EmbeddingCacheBehavior.cs`

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class EmbeddingCacheBehavior : IRetrievalBehavior
{
    [Inject(Optional = true)] public HybridCache? Cache { get; set; }
    [Inject(Optional = true)] public CachingOptions? CachingOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCacheEmbedding || Cache is null || CachingOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var textToEmbed = ctx.Options.EmbeddingTextOverride ?? ctx.Query;
        var cacheKey = CacheKeyGenerator.ForEmbedding(textToEmbed);

        try
        {
            var results = await Cache.GetOrCreateAsync(
                cacheKey,
                async ct2 =>
                {
                    RagPipelineLog.EmbeddingCacheMiss(ctx.Logger, ctx.Query);
                    var inner = await next(ctx, ct2).ConfigureAwait(false);
                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = CachingOptions.EmbeddingTtl },
                cancellationToken: ct).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.EmbeddingCacheFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }
}
```

### Step 5: Create `VectorStoreBehavior.cs` (terminal)

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Search;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class VectorStoreBehavior : IRetrievalBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var opts = ctx.Options;
        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        var textToEmbed = opts.EmbeddingTextOverride ?? ctx.Query;
        var queryEmbeddings = await Embedder.GenerateAsync([textToEmbed], cancellationToken: ct).ConfigureAwait(false);

        IReadOnlyList<SearchResult> results;
        string searchMode;

        if (opts.UseHybridSearch)
        {
            if (VectorStore is IHybridSearchable hybrid)
            {
                searchMode = "hybrid-native";
                results = await hybrid.HybridSearchAsync(ctx.Query, queryEmbeddings[0].Vector, searchOptions, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                searchMode = "hybrid-bm25-fallback";
                var denseTask = VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct);
                var bm25Hits = Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
                var dense = await denseTask.ConfigureAwait(false);
                results = RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
            }
        }
        else
        {
            searchMode = "dense";
            results = await VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false);
        }

        RagPipelineLog.VectorStoreSearchCompleted(ctx.Logger, searchMode, results.Count);
        // Terminal — does not call next
        return results;
    }
}
```

### Step 6: Append tests to `RetrievalBehaviorTests.cs`

```csharp
// ── RerankingBehavior ─────────────────────────────────────────────────

[Fact]
public async Task Reranking_WhenRerankerNull_ReturnsNextResults()
{
    var sut = new RerankingBehavior { Reranker = null };
    var ctx = MakeCtx(options: new() { UseReranking = true });
    var expected = MakeResults(3);

    var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
        (_, _) => ValueTask.FromResult(expected));

    Assert.Same(expected, result);
}

[Fact]
public async Task Reranking_WhenFlagFalse_ReturnsNextResults()
{
    var reranker = Substitute.For<IReranker>();
    var sut = new RerankingBehavior { Reranker = reranker };
    var ctx = MakeCtx(options: new() { UseReranking = false });
    var expected = MakeResults(3);

    var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
        (_, _) => ValueTask.FromResult(expected));

    Assert.Same(expected, result);
    await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
}

// ── HydeBehavior ─────────────────────────────────────────────────────

[Fact]
public async Task Hyde_WhenGeneratorNull_ReturnsNextResults()
{
    var sut = new HydeBehavior { HydeGenerator = null };
    var ctx = MakeCtx(options: new() { UseHyde = true });
    var expected = MakeResults(3);

    var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
        (_, _) => ValueTask.FromResult(expected));

    Assert.Same(expected, result);
}

[Fact]
public async Task Hyde_WhenEnabled_PassesHypotheticalDocAsEmbeddingOverride()
{
    var generator = Substitute.For<IHypotheticalDocumentGenerator>();
    generator.GenerateAsync("test", Arg.Any<CancellationToken>()).Returns("hypothetical answer");
    var sut = new HydeBehavior { HydeGenerator = generator };
    var ctx = MakeCtx(options: new() { UseHyde = true });
    string? capturedOverride = null;

    await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, _) =>
    {
        capturedOverride = c.Options.EmbeddingTextOverride;
        return ValueTask.FromResult(MakeResults(0));
    });

    Assert.Equal("hypothetical answer", capturedOverride);
}

// ── MultiQueryBehavior ───────────────────────────────────────────────

[Fact]
public async Task MultiQuery_WhenExpanderNull_ReturnsNextResults()
{
    var sut = new MultiQueryBehavior { QueryExpander = null };
    var ctx = MakeCtx(options: new() { UseMultiQuery = true });
    var expected = MakeResults(3);

    var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
        (_, _) => ValueTask.FromResult(expected));

    Assert.Same(expected, result);
}
```

### Step 7: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "RetrievalBehaviorTests"
```

Expected: all tests pass.

### Step 8: Commit

```bash
git add src/Rag.NET/Retrieval/Behaviors/ tests/Rag.NET.Tests/Retrieval/Behaviors/
git commit -m "feat: add retrieval behaviors - Reranking, MultiQuery, Hyde, EmbeddingCache, VectorStore"
```

---

## Task 8: PipelineRetriever

**Files:**
- Create: `src/Rag.NET/Retrieval/PipelineRetriever.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/PipelineRetrieverTests.cs`

### Step 1: Write the failing test

```csharp
// tests/Rag.NET.Tests/Retrieval/PipelineRetrieverTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class PipelineRetrieverTests
{
    private static PipelineRetriever CreateSut(
        Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>? pipeline = null) =>
        new()
        {
            Pipeline = pipeline ?? new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
                (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([])),
        };

    [Fact]
    public async Task RetrieveAsync_CreatesContextAndExecutesPipeline()
    {
        var capturedCtx = (RetrievalContext?)null;
        var pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>((ctx, _) =>
        {
            capturedCtx = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        });
        var sut = CreateSut(pipeline);
        var options = new RetrievalOptions { TopK = 5 };

        await sut.RetrieveAsync("my query", options, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedCtx);
        Assert.Equal("my query", capturedCtx!.Query);
        Assert.Equal(5, capturedCtx.Options.TopK);
    }

    [Fact]
    public async Task RetrieveAsync_WithNullOptions_UsesDefaultOptions()
    {
        RetrievalContext? captured = null;
        var sut = CreateSut(new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>((ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        }));

        await sut.RetrieveAsync("q", null, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Options);
    }
}
```

### Step 2: Run to verify it fails

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineRetrieverTests"
```

Expected: FAIL — `PipelineRetriever` does not exist.

### Step 3: Create `PipelineRetriever.cs`

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval;

/// <summary>
/// Thin facade over the retrieval pipeline.
/// Replaces the nested decorator factory (BuildRetrieverChain).
/// </summary>
[Singleton(As = typeof(IRetriever))]
public sealed class PipelineRetriever : IRetriever
{
    [Inject] public Pipeline<RetrievalContext, IReadOnlyList<SearchResult>> Pipeline { get; set; } = null!;
    [Inject(Optional = true)] public ILogger<PipelineRetriever>? Logger { get; set; }

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new RetrievalContext
        {
            Query = query,
            Options = options ?? new RetrievalOptions(),
            Logger = Logger ?? NullLogger.Instance,
        };

        return Pipeline.ExecuteAsync(ctx, cancellationToken).AsTask();
    }
}
```

### Step 4: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineRetrieverTests"
```

Expected: all tests pass.

### Step 5: Commit

```bash
git add src/Rag.NET/Retrieval/PipelineRetriever.cs tests/Rag.NET.Tests/Retrieval/PipelineRetrieverTests.cs
git commit -m "feat: add PipelineRetriever with property-injected pipeline"
```

---

## Task 9: DI Wiring + Delete Old Files + Full Test Run

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Delete: `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- Delete: `src/Rag.NET/Retrieval/VectorStoreRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/HydeRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/MultiQueryRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/RerankingRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/ParentDocumentRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/RedundancyFilterRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/MmrRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/LostInTheMiddleRetriever.cs`
- Delete: `src/Rag.NET/Retrieval/ResultCacheRetriever.cs`
- Delete: `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs`

### Step 1: Update `ServiceCollectionExtensions.cs`

Replace the body of `AddRagNet` and delete the `BuildRetrieverChain`/`WrapWithQueryDecorators` methods:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Search;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null,
        Action<IngestionPipelineBuilder>? ingestion = null,
        Action<RetrievalPipelineBuilder>? retrieval = null)
    {
        // ZeroAlloc.Inject-generated: registers IDocumentParser (Text, Markdown),
        // IChunkingStrategy (Recursive).
        services.AddRagNETServices();

        services.TryAddSingleton<ChunkingOptions>();
        services.AddSingleton<InMemoryBm25Index>();

        // Register all behavior singletons
        services.AddSingleton<OverwriteBehavior>();
        services.AddSingleton<ParseBehavior>();
        services.AddSingleton<ChunkingBehavior>();
        services.AddSingleton<MetadataBehavior>();
        services.AddSingleton<ParentDocumentIngestionBehavior>();
        services.AddSingleton<EmbeddingBehavior>();
        services.AddSingleton<StorageBehavior>();

        services.AddSingleton<ResultCacheBehavior>();
        services.AddSingleton<LostInTheMiddleBehavior>();
        services.AddSingleton<MmrBehavior>();
        services.AddSingleton<RedundancyFilterBehavior>();
        services.AddSingleton<ParentDocumentRetrievalBehavior>();
        services.AddSingleton<RerankingBehavior>();
        services.AddSingleton<MultiQueryBehavior>();
        services.AddSingleton<HydeBehavior>();
        services.AddSingleton<EmbeddingCacheBehavior>();
        services.AddSingleton<VectorStoreBehavior>();

        // Build pipelines via extensible builders
        var ingestionBuilder = new IngestionPipelineBuilder();
        ingestion?.Invoke(ingestionBuilder);
        services.AddSingleton(sp => ingestionBuilder.Build(sp));

        var retrievalBuilder = new RetrievalPipelineBuilder();
        retrieval?.Invoke(retrievalBuilder);
        services.AddSingleton(sp => retrievalBuilder.Build(sp));

        // Inject properties into facades using ZeroAlloc.Inject
        services.AddSingleton<IIngestor>(sp =>
        {
            var ingestor = new PipelineIngestor();
            sp.InjectRagNETProperties(ingestor);   // ZeroAlloc.Inject-generated method
            return ingestor;
        });

        services.AddSingleton<IRetriever>(sp =>
        {
            var retriever = new PipelineRetriever();
            sp.InjectRagNETProperties(retriever);
            return retriever;
        });

        // Also inject behaviors (needed because they use [Inject] too)
        // Note: if ZeroAlloc.Inject auto-wires all [Singleton]-decorated registrations,
        // this block may not be needed — check generated code.

        services.AddSingleton<IRagPipeline>(sp =>
        {
            var r = sp.GetRequiredService<IRetriever>();
            var i = sp.GetRequiredService<IIngestor>();
            var chatClient = sp.GetService<IChatClient>();
            IAnswerEngine? answerEngine = chatClient is not null ? new ChatAnswerEngine(chatClient) : null;
            return new RagPipeline(r, i, answerEngine);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        services.TryAddSingleton<IBm25Index>(sp => sp.GetRequiredService<InMemoryBm25Index>());

        return services;
    }
}
```

> **Important:** Check what the ZeroAlloc.Inject-generated `AddRagNETServices()` and `InjectRagNETProperties()` methods actually look like in the generated code (inspect `obj/Debug/net10.0/*.g.cs`). The generator may already handle `[Singleton]`-attributed classes differently — it may auto-register them and auto-inject when resolved. Align the manual registration above with what the generator actually produces; do not double-register.

### Step 2: Delete old files

```bash
rm src/Rag.NET/Ingestion/DocumentIngestor.cs
rm src/Rag.NET/Retrieval/VectorStoreRetriever.cs
rm src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs
rm src/Rag.NET/Retrieval/HydeRetriever.cs
rm src/Rag.NET/Retrieval/MultiQueryRetriever.cs
rm src/Rag.NET/Retrieval/RerankingRetriever.cs
rm src/Rag.NET/Retrieval/ParentDocumentRetriever.cs
rm src/Rag.NET/Retrieval/RedundancyFilterRetriever.cs
rm src/Rag.NET/Retrieval/MmrRetriever.cs
rm src/Rag.NET/Retrieval/LostInTheMiddleRetriever.cs
rm src/Rag.NET/Retrieval/ResultCacheRetriever.cs
rm tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs
```

### Step 3: Build and run ALL tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q
```

Expected: build succeeds, all tests pass. If compile errors remain (stale `using` directives referencing deleted classes), remove them.

### Step 4: Commit

```bash
git add -A
git commit -m "refactor: wire pipeline builders in DI; remove DocumentIngestor and decorator chain"
```

---

## Task 10: Pipeline Builder Tests

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/PipelineBuilderTests.cs`

### Step 1: Write tests

```csharp
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class PipelineBuilderTests
{
    // ── IngestionPipelineBuilder ─────────────────────────────────────────

    [Fact]
    public void IngestionBuilder_DefaultContainsAllSevenBehaviors()
    {
        var builder = new IngestionPipelineBuilder();
        var types = builder.GetBehaviorTypes(); // expose internal for testing
        Assert.Equal(7, types.Count);
        Assert.Equal(typeof(StorageBehavior), types[^1]);
    }

    [Fact]
    public void IngestionBuilder_Add_InsertsAfterTarget()
    {
        var builder = new IngestionPipelineBuilder();
        builder.Add<NoOpIngestionBehavior>(after: typeof(ParseBehavior));
        var types = builder.GetBehaviorTypes();
        var parseIdx = types.IndexOf(typeof(ParseBehavior));
        Assert.Equal(typeof(NoOpIngestionBehavior), types[parseIdx + 1]);
    }

    [Fact]
    public void IngestionBuilder_Replace_SwapsType()
    {
        var builder = new IngestionPipelineBuilder();
        builder.Replace<EmbeddingBehavior, NoOpIngestionBehavior>();
        var types = builder.GetBehaviorTypes();
        Assert.DoesNotContain(typeof(EmbeddingBehavior), types);
        Assert.Contains(typeof(NoOpIngestionBehavior), types);
    }

    // ── RetrievalPipelineBuilder ─────────────────────────────────────────

    [Fact]
    public void RetrievalBuilder_Add_InsertsBeforeTarget()
    {
        var builder = new RetrievalPipelineBuilder();
        builder.Add<NoOpRetrievalBehavior>(before: typeof(VectorStoreBehavior));
        var types = builder.GetBehaviorTypes();
        var vsIdx = types.IndexOf(typeof(VectorStoreBehavior));
        Assert.Equal(typeof(NoOpRetrievalBehavior), types[vsIdx - 1]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class NoOpIngestionBehavior : IIngestionBehavior
    {
        public ValueTask<IngestionResult> HandleAsync(
            IngestionContext ctx, CancellationToken ct,
            Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next) => next(ctx, ct);
    }

    private sealed class NoOpRetrievalBehavior : IRetrievalBehavior
    {
        public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
            RetrievalContext ctx, CancellationToken ct,
            Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next) => next(ctx, ct);
    }
}
```

> **Note:** `GetBehaviorTypes()` needs to be added to `IngestionPipelineBuilder` and `RetrievalPipelineBuilder` as an `internal` method returning `IReadOnlyList<Type>`. Since `Rag.NET.Tests` has `InternalsVisibleTo`, this works without making it public.

Add to `IngestionPipelineBuilder`:
```csharp
internal IReadOnlyList<Type> GetBehaviorTypes() => _types.AsReadOnly();
```

Add to `RetrievalPipelineBuilder`:
```csharp
internal IReadOnlyList<Type> GetBehaviorTypes() => _types.AsReadOnly();
```

### Step 2: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineBuilderTests"
```

Expected: all tests pass.

### Step 3: Commit

```bash
git add tests/Rag.NET.Tests/DependencyInjection/ src/Rag.NET/DependencyInjection/
git commit -m "test: add pipeline builder extensibility tests"
```

---

## Task 11: Final Full-Suite Verification

### Step 1: Run the complete test suite

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q
```

Expected: all tests pass, 0 failures.

### Step 2: Build release configuration

```bash
dotnet msbuild src/Rag.NET/Rag.NET.csproj -p:Configuration=Release -q
```

Expected: Build succeeded, 0 errors, 0 warnings.

### Step 3: Commit if any minor fixes were needed

```bash
git add -A
git commit -m "fix: resolve any final compilation or test issues after full suite run"
```
