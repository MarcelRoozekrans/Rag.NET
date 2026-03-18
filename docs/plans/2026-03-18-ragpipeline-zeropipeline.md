# RAG Pipeline — ZeroAlloc.Pipeline Refactor Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the runtime OOP decorator chains for retrieval and ingestion with static lambda chains via `ZeroAlloc.Pipeline`, while keeping the public `IRagPipeline` API unchanged.

**Architecture:** Two new facades (`PipelineIngestor`, `PipelineRetriever`) implement the existing `IIngestor`/`IRetriever` interfaces. Each facade builds a typed context object and invokes a hand-written static lambda chain (`IngestionChain`, `RetrievalChain`) where every step is a static `Handle` method on a behavior class decorated with `[PipelineBehavior(Order = n)]`. The lambda chain is nested static closures — zero heap allocation on the hot path.

**Tech Stack:** C# / .NET 10, ZeroAlloc.Pipeline, ZeroAlloc.Pipeline.Generators, xUnit v3, NSubstitute

---

## Important Notes

- **`RetrievalContext` must be a `sealed record`** so behaviors can use `with` to pass modified options down the chain (e.g., HydeBehavior sets `EmbeddingTextOverride`; MultiQueryBehavior runs sub-queries with different `Query` values).
- **`IngestionContext` is a `sealed class`** (has mutable `List<>` state that accumulates).
- **Behaviors are static**. No constructor injection. All services travel in the context.
- **Hand-write `IngestionChain.cs` and `RetrievalChain.cs`** — static nested lambda chains. `ZeroAlloc.Pipeline.Generators` provides the generator infrastructure; we use the library for `IPipelineBehavior` / `[PipelineBehavior]` and write the chains manually.
- **`_nextBm25DocId`** counter lives on `PipelineIngestor` (same as `DocumentIngestor`). Pass it to `StorageBehavior` via `Func<int> GetNextBm25DocId` in `IngestionContext`.
- **MmrBehavior** was missing from the design doc — add it at Order=30 between LostInTheMiddle(20) and RedundancyFilter(40).
- Use `dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj` to build (not `dotnet build` — see MSB3492 workaround in `Directory.Build.props`), then `dotnet test --no-build` to run.

---

## Task 1: Add ZeroAlloc.Pipeline + Create Context Models

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`
- Create: `src/Rag.NET/Ingestion/IngestionContext.cs`
- Create: `src/Rag.NET/Retrieval/RetrievalContext.cs`

### Step 1: Add NuGet packages

In `src/Rag.NET/Rag.NET.csproj`, add after the existing `ZeroAlloc.Inject` references:

```xml
<PackageReference Include="ZeroAlloc.Pipeline" Version="*" />
<PackageReference Include="ZeroAlloc.Pipeline.Generators" Version="*">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
</PackageReference>
```

### Step 2: Create `IngestionContext.cs`

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Ingestion;

/// <summary>
/// Mutable per-call context for the ingestion pipeline.
/// Accumulated state (Sections, Chunks, EmbeddedChunks) is populated by behaviors in order.
/// </summary>
public sealed class IngestionContext
{
    // ── Input ────────────────────────────────────────────────────────────
    public required Stream Stream                                             { get; init; }
    public required DocumentMetadata Metadata                                { get; init; }
    public IngestionOptions? Options                                         { get; init; }
    public IProgress<IngestionProgress>? Progress                           { get; init; }

    // ── Accumulated state (mutated as chain progresses) ──────────────────
    public List<DocumentSection> Sections    { get; } = [];
    public List<TextChunk> Chunks            { get; } = [];
    public List<EmbeddedChunk> EmbeddedChunks { get; } = [];

    // ── Services — required ───────────────────────────────────────────────
    public required IEnumerable<IDocumentParser> Parsers                    { get; init; }
    public required IChunkingStrategy ChunkingStrategy                      { get; init; }
    public required ChunkingOptions ChunkingOptions                         { get; init; }
    public required IVectorStore VectorStore                                { get; init; }
    public required IEmbeddingGenerator<string, Embedding<float>> Embedder  { get; init; }
    public required IBm25Index Bm25Index                                    { get; init; }

    // ── Services — optional ───────────────────────────────────────────────
    public IParentChunkStore? ParentStore                                   { get; init; }
    public ParentDocumentOptions? ParentOptions                             { get; init; }
    public IRagDataManager? DataManager                                     { get; init; }

    // ── Counter delegate — facade provides this so StorageBehavior can
    //    assign unique BM25 doc IDs across concurrent ingest calls ─────────
    public required Func<int> GetNextBm25DocId                              { get; init; }
}
```

### Step 3: Create `RetrievalContext.cs`

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Immutable per-call context for the retrieval pipeline.
/// Use <c>with</c> to derive modified contexts (e.g., override query, disable flags).
/// </summary>
public sealed record RetrievalContext
{
    // ── Input ────────────────────────────────────────────────────────────
    public required string Query              { get; init; }
    public required RetrievalOptions Options  { get; init; }

    // ── Services — required ───────────────────────────────────────────────
    public required IVectorStore VectorStore                                { get; init; }
    public required IEmbeddingGenerator<string, Embedding<float>> Embedder  { get; init; }
    public required IBm25Index Bm25Index                                    { get; init; }

    // ── Services — optional ───────────────────────────────────────────────
    public IReranker? Reranker                                              { get; init; }
    public IQueryExpander? QueryExpander                                    { get; init; }
    public MultiQueryOptions? MultiQueryOptions                             { get; init; }
    public IHypotheticalDocumentGenerator? HydeGenerator                   { get; init; }
    public IParentChunkStore? ParentStore                                   { get; init; }
    public HybridCache? Cache                                               { get; init; }
    public CachingOptions? CachingOptions                                   { get; init; }
    public ILogger Logger                                                   { get; init; } = NullLogger.Instance;
}
```

### Step 4: Build to verify

```bash
dotnet msbuild src/Rag.NET/Rag.NET.csproj -q
```

Expected: Build succeeded, 0 errors.

### Step 5: Commit

```bash
git add src/Rag.NET/Rag.NET.csproj src/Rag.NET/Ingestion/IngestionContext.cs src/Rag.NET/Retrieval/RetrievalContext.cs
git commit -m "feat: add ZeroAlloc.Pipeline packages and pipeline context models"
```

---

## Task 2: Ingestion Behaviors (Part 1) — Overwrite, Parse, Chunking, Metadata

**Files:**
- Create: `src/Rag.NET/Ingestion/Behaviors/OverwriteBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/ChunkingBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/Behaviors/IngestionBehaviorTests.cs`

### Step 1: Create behavior files

**`OverwriteBehavior.cs`**
```csharp
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

[PipelineBehavior(Order = 10)]
public sealed class OverwriteBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.Options?.Overwrite == true)
        {
            await ctx.VectorStore.DeleteByDocumentIdAsync(ctx.Metadata.DocumentId, ct).ConfigureAwait(false);
            ctx.Bm25Index.Remove(ctx.Metadata.DocumentId);
            ctx.DataManager?.Remove(ctx.Metadata.DocumentId);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**`ParseBehavior.cs`**
```csharp
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

[PipelineBehavior(Order = 20)]
public sealed class ParseBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var parser = ctx.Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{ctx.Metadata.ContentType}'.");

        if (ctx.ParentOptions is not null && ctx.ParentStore is not null && !ctx.Stream.CanSeek)
            throw new InvalidOperationException(
                "Parent-document retrieval requires a seekable stream. Wrap the stream in a MemoryStream before calling IngestAsync.");

        var headingBreadcrumbs = new string?[6];

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            Dictionary<string, string>? headingMetadata = null;

            if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
            {
                headingBreadcrumbs[level - 1] = section.Heading;
                var idx = level;
                while (idx < 6) { headingBreadcrumbs[idx] = null; idx++; }

                var parts = new List<string>(level);
                foreach (var h in headingBreadcrumbs[..level])
                    if (h is not null) parts.Add(h);

                var breadcrumb = string.Join(" > ", parts);
                headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = section.Heading,
                    ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["heading_breadcrumb"] = breadcrumb,
                };
            }

            await foreach (var chunk in ctx.ChunkingStrategy.ChunkAsync(section, ctx.ChunkingOptions, ct).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);

                ctx.Chunks.Add(chunk);
            }

            ctx.Sections.Add(section);
        }

        ctx.Progress?.Report(new() { Stage = IngestionProgressStage.Parsing, DocumentId = ctx.Metadata.DocumentId, Message = "Parsing complete" });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**`ChunkingBehavior.cs`**

Note: Chunking is now done inline in `ParseBehavior` (sections → chunks in one pass, matching the original `ParseAndChunkAsync`). `ChunkingBehavior` applies progress reporting.

```csharp
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

/// <summary>
/// Reports chunking progress after ParseBehavior has populated ctx.Chunks.
/// </summary>
[PipelineBehavior(Order = 30)]
public sealed class ChunkingBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.Chunks.Count == 0)
            return new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 };

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Chunking,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.Chunks.Count,
            Total = ctx.Chunks.Count,
            Message = $"Chunked into {ctx.Chunks.Count} chunks",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**`MetadataBehavior.cs`**
```csharp
using System.Runtime.InteropServices;
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

[PipelineBehavior(Order = 40)]
public sealed class MetadataBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
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

### Step 2: Write tests

**`tests/Rag.NET.Tests/Ingestion/Behaviors/IngestionBehaviorTests.cs`**

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
        IngestionOptions? options = null,
        IVectorStore? vectorStore = null,
        IBm25Index? bm25Index = null,
        IParentChunkStore? parentStore = null,
        ParentDocumentOptions? parentOptions = null,
        IRagDataManager? dataManager = null,
        Stream? stream = null,
        IEnumerable<IDocumentParser>? parsers = null,
        IChunkingStrategy? chunker = null)
    {
        var ctx = new IngestionContext
        {
            Stream = stream ?? new MemoryStream("test"u8.ToArray()),
            Metadata = metadata ?? new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" },
            Options = options,
            Parsers = parsers ?? [],
            ChunkingStrategy = chunker ?? Substitute.For<IChunkingStrategy>(),
            ChunkingOptions = new ChunkingOptions(),
            VectorStore = vectorStore ?? Substitute.For<IVectorStore>(),
            Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            Bm25Index = bm25Index ?? Substitute.For<IBm25Index>(),
            ParentStore = parentStore,
            ParentOptions = parentOptions,
            DataManager = dataManager,
            GetNextBm25DocId = () => 1,
        };
        return ctx;
    }

    private static ValueTask<IngestionResult> EmptyNext(IngestionContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    // ── OverwriteBehavior ────────────────────────────────────────────────

    [Fact]
    public async Task OverwriteBehavior_WhenOverwriteFalse_DoesNotDeleteFromStores()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var ctx = MakeCtx(options: new() { Overwrite = false }, vectorStore: vectorStore, bm25Index: bm25);
        var ct = TestContext.Current.CancellationToken;

        await OverwriteBehavior.Handle(ctx, ct, EmptyNext);

        await vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        bm25.DidNotReceive().Remove(Arg.Any<string>());
    }

    [Fact]
    public async Task OverwriteBehavior_WhenOverwriteTrue_DeletesFromAllStores()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var bm25 = Substitute.For<IBm25Index>();
        var dataManager = Substitute.For<IRagDataManager>();
        var ctx = MakeCtx(options: new() { Overwrite = true }, vectorStore: vectorStore, bm25Index: bm25, dataManager: dataManager);
        var ct = TestContext.Current.CancellationToken;

        await OverwriteBehavior.Handle(ctx, ct, EmptyNext);

        await vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", ct);
        bm25.Received(1).Remove("doc-1");
        dataManager.Received(1).Remove("doc-1");
    }

    // ── ChunkingBehavior ─────────────────────────────────────────────────

    [Fact]
    public async Task ChunkingBehavior_WhenNoChunks_ReturnsEmptyResultWithoutCallingNext()
    {
        var ctx = MakeCtx();
        var ct = TestContext.Current.CancellationToken;
        var nextCalled = false;

        var result = await ChunkingBehavior.Handle(ctx, ct, (c, t) =>
        {
            nextCalled = true;
            return EmptyNext(c, t);
        });

        Assert.False(nextCalled);
        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(0, result.ChunksStored);
    }

    [Fact]
    public async Task ChunkingBehavior_WhenChunksExist_CallsNext()
    {
        var ctx = MakeCtx();
        ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 });
        var ct = TestContext.Current.CancellationToken;
        var nextCalled = false;

        await ChunkingBehavior.Handle(ctx, ct, (c, t) =>
        {
            nextCalled = true;
            return EmptyNext(c, t);
        });

        Assert.True(nextCalled);
    }

    // ── MetadataBehavior ─────────────────────────────────────────────────

    [Fact]
    public async Task MetadataBehavior_AppliesTagsAndDocumentMetadataToAllChunks()
    {
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc-1", FileName = "file.txt", ContentType = "text/plain",
            Tags = { ["env"] = "prod" },
        };
        var ctx = MakeCtx(metadata: metadata);
        ctx.Chunks.Add(new TextChunk { Text = "chunk1", DocumentId = "doc-1", ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "chunk2", DocumentId = "doc-1", ChunkIndex = 1 });
        var ct = TestContext.Current.CancellationToken;

        await MetadataBehavior.Handle(ctx, ct, EmptyNext);

        Assert.Equal("prod", ctx.Chunks[0].Metadata["env"]);
        Assert.Equal("doc-1", ctx.Chunks[0].Metadata["document_id"]);
        Assert.Equal("file.txt", ctx.Chunks[0].Metadata["file_name"]);
        Assert.Equal("prod", ctx.Chunks[1].Metadata["env"]);
    }
}
```

### Step 3: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "IngestionBehaviorTests"
```

Expected: all tests pass.

### Step 4: Commit

```bash
git add src/Rag.NET/Ingestion/Behaviors/ tests/Rag.NET.Tests/Ingestion/Behaviors/
git commit -m "feat: add ingestion behaviors part 1 (Overwrite, Parse, Chunking, Metadata)"
```

---

## Task 3: Ingestion Behaviors (Part 2) — ParentDocument, Embedding, Storage

**Files:**
- Create: `src/Rag.NET/Ingestion/Behaviors/ParentDocumentIngestionBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs`
- Create: `src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs`
- Modify: `tests/Rag.NET.Tests/Ingestion/Behaviors/IngestionBehaviorTests.cs` (append)

### Step 1: Create behavior files

**`ParentDocumentIngestionBehavior.cs`**
```csharp
using System.Runtime.InteropServices;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

[PipelineBehavior(Order = 50)]
public sealed class ParentDocumentIngestionBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.ParentOptions is null || ctx.ParentStore is null)
            return await next(ctx, ct).ConfigureAwait(false);

        // Reset stream for second parse pass
        ctx.Stream.Position = 0;

        var parentChunkingOptions = new ChunkingOptions
        {
            MaxChunkSize = ctx.ParentOptions.ParentChunkSize,
            Overlap = ctx.ParentOptions.ParentOverlap,
        };

        var parser = ctx.Parsers.First(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"));
        var parentBoundaries = new List<(int start, int end)>();
        var parentIndex = 0;

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            await foreach (var parentChunk in ctx.ChunkingStrategy.ChunkAsync(section, parentChunkingOptions, ct).ConfigureAwait(false))
            {
                ctx.ParentStore.Add(ctx.Metadata.DocumentId, parentIndex, parentChunk.Text);
                parentBoundaries.Add((parentChunk.StartPosition, parentChunk.EndPosition));
                parentIndex++;
            }
        }

        foreach (ref readonly var child in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            var pIdx = ParentChunkKeyHelper.FindParentIndex(parentBoundaries, child.StartPosition);
            child.Metadata[ParentChunkKeyHelper.ParentKeyMetadata] = ParentChunkKeyHelper.GetParentKey(ctx.Metadata.DocumentId, pIdx);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**`EmbeddingBehavior.cs`**
```csharp
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

[PipelineBehavior(Order = 60)]
public sealed class EmbeddingBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var texts = ctx.Chunks.Select(c => c.Text).ToList();
        var embeddings = await ctx.Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);

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

**`StorageBehavior.cs`** (terminal — does not call `next`)
```csharp
using System.Runtime.InteropServices;
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Ingestion.Behaviors;

[PipelineBehavior(Order = 70)]
public sealed class StorageBehavior : IPipelineBehavior
{
    public static async ValueTask<IngestionResult> Handle(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        await ctx.VectorStore.StoreAsync(ctx.EmbeddedChunks, ct).ConfigureAwait(false);

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Storing,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.EmbeddedChunks.Count,
            Total = ctx.EmbeddedChunks.Count,
            Message = $"Stored {ctx.EmbeddedChunks.Count} chunks",
        });

        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(ctx.EmbeddedChunks))
            ctx.Bm25Index.Add(ctx.GetNextBm25DocId(), ec.Chunk);

        ctx.DataManager?.Add(ctx.Metadata, ctx.Chunks);

        return new IngestionResult
        {
            DocumentId = ctx.Metadata.DocumentId,
            ChunksStored = ctx.EmbeddedChunks.Count,
        };
    }
}
```

### Step 2: Write tests (append to `IngestionBehaviorTests.cs`)

```csharp
// ── EmbeddingBehavior ────────────────────────────────────────────────

[Fact]
public async Task EmbeddingBehavior_PopulatesEmbeddedChunksFromChunks()
{
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
    embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    var ctx = MakeCtx();
    ctx.Chunks.Add(new TextChunk { Text = "hello", DocumentId = "doc-1", ChunkIndex = 0 });
    var ct = TestContext.Current.CancellationToken;

    await EmbeddingBehavior.Handle(ctx, ct, EmptyNext);

    Assert.Single(ctx.EmbeddedChunks);
    Assert.Equal("hello", ctx.EmbeddedChunks[0].Chunk.Text);
}

// ── StorageBehavior ──────────────────────────────────────────────────

[Fact]
public async Task StorageBehavior_StoresEmbeddedChunksAndReturnResult()
{
    var vectorStore = Substitute.For<IVectorStore>();
    var bm25 = Substitute.For<IBm25Index>();
    var counter = 0;
    var ctx = new IngestionContext
    {
        Stream = new MemoryStream(),
        Metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt" },
        Parsers = [],
        ChunkingStrategy = Substitute.For<IChunkingStrategy>(),
        ChunkingOptions = new(),
        VectorStore = vectorStore,
        Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
        Bm25Index = bm25,
        GetNextBm25DocId = () => ++counter,
    };
    ctx.Chunks.Add(new TextChunk { Text = "chunk", DocumentId = "doc-1", ChunkIndex = 0 });
    ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = ctx.Chunks[0], Embedding = new float[] { 0.1f } });
    var ct = TestContext.Current.CancellationToken;

    var result = await StorageBehavior.Handle(ctx, ct, EmptyNext);

    await vectorStore.Received(1).StoreAsync(ctx.EmbeddedChunks, ct);
    bm25.Received(1).Add(1, ctx.Chunks[0]);
    Assert.Equal("doc-1", result.DocumentId);
    Assert.Equal(1, result.ChunksStored);
}

[Fact]
public async Task StorageBehavior_DoesNotCallDataManagerWhenNull()
{
    var ctx = MakeCtx(dataManager: null);
    ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = new TextChunk { Text = "x", DocumentId = "doc-1", ChunkIndex = 0 }, Embedding = [] });
    var ct = TestContext.Current.CancellationToken;

    // Should not throw
    await StorageBehavior.Handle(ctx, ct, EmptyNext);
}
```

### Step 3: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "IngestionBehaviorTests"
```

Expected: all tests pass.

### Step 4: Commit

```bash
git add src/Rag.NET/Ingestion/Behaviors/ tests/Rag.NET.Tests/Ingestion/Behaviors/
git commit -m "feat: add ingestion behaviors part 2 (ParentDocument, Embedding, Storage)"
```

---

## Task 4: PipelineIngestor + IngestionChain

**Files:**
- Create: `src/Rag.NET/Ingestion/IngestionChain.cs`
- Create: `src/Rag.NET/Ingestion/PipelineIngestor.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs`

### Step 1: Write the failing test

```csharp
// tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorTests
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly IBm25Index _bm25 = Substitute.For<IBm25Index>();

    private PipelineIngestor CreateSut(
        IParentChunkStore? parentStore = null,
        ParentDocumentOptions? parentOptions = null,
        IRagDataManager? dataManager = null) =>
        new([_parser], _chunker, _vectorStore, _embedder, new ChunkingOptions(), _bm25,
            parentStore, parentOptions, dataManager);

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    [Fact]
    public async Task IngestAsync_BasicDocument_ReturnsResultWithChunkCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.CanParse("text/plain").Returns(true);
        _parser.ParseAsync(Arg.Any<Stream>(), metadata, ct).Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), ct).Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        using var stream = new MemoryStream("hello"u8.ToArray());
        var result = await sut.IngestAsync(stream, metadata, cancellationToken: ct);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
        await _vectorStore.Received(1).StoreAsync(Arg.Any<List<EmbeddedChunk>>(), ct);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFromAllStores()
    {
        var parentStore = Substitute.For<IParentChunkStore>();
        var dataManager = Substitute.For<IRagDataManager>();
        var sut = CreateSut(parentStore: parentStore, dataManager: dataManager);
        var ct = TestContext.Current.CancellationToken;

        await sut.DeleteAsync("doc-1", ct);

        await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", ct);
        _bm25.Received(1).Remove("doc-1");
        parentStore.Received(1).Remove("doc-1");
        dataManager.Received(1).Remove("doc-1");
    }
}
```

### Step 2: Run test to verify it fails

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineIngestorTests"
```

Expected: FAIL — `PipelineIngestor` does not exist yet.

### Step 3: Create `IngestionChain.cs`

```csharp
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;

namespace Rag.NET.Ingestion;

/// <summary>
/// Hand-written static lambda chain for the ingestion pipeline.
/// Zero-allocation: all lambdas are static closures; no per-call delegate allocation.
/// </summary>
internal static class IngestionChain
{
    public static ValueTask<IngestionResult> ExecuteAsync(IngestionContext ctx, CancellationToken ct) =>
        OverwriteBehavior.Handle(ctx, ct, static (ctx, ct) =>
        ParseBehavior.Handle(ctx, ct, static (ctx, ct) =>
        ChunkingBehavior.Handle(ctx, ct, static (ctx, ct) =>
        MetadataBehavior.Handle(ctx, ct, static (ctx, ct) =>
        ParentDocumentIngestionBehavior.Handle(ctx, ct, static (ctx, ct) =>
        EmbeddingBehavior.Handle(ctx, ct, static (ctx, ct) =>
        StorageBehavior.Handle(ctx, ct, static (ctx, ct) =>
            ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 }))))))));
}
```

### Step 4: Create `PipelineIngestor.cs`

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Ingestion;

/// <summary>
/// Facade over the source-generated ingestion pipeline.
/// Implements <see cref="IIngestor"/> — replaces <see cref="DocumentIngestor"/>.
/// </summary>
public sealed class PipelineIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ChunkingOptions chunkingOptions,
    IBm25Index bm25Index,
    IParentChunkStore? parentStore = null,
    ParentDocumentOptions? parentOptions = null,
    IRagDataManager? dataManager = null) : IIngestor
{
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
            Parsers = parsers,
            ChunkingStrategy = chunkingStrategy,
            ChunkingOptions = chunkingOptions,
            VectorStore = vectorStore,
            Embedder = embeddingGenerator,
            Bm25Index = bm25Index,
            ParentStore = parentStore,
            ParentOptions = parentOptions,
            DataManager = dataManager,
            GetNextBm25DocId = () => System.Threading.Interlocked.Increment(ref _nextBm25DocId),
        };

        return IngestionChain.ExecuteAsync(ctx, cancellationToken).AsTask();
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        bm25Index.Remove(documentId);
        parentStore?.Remove(documentId);
        dataManager?.Remove(documentId);
    }
}
```

### Step 5: Run tests to verify they pass

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineIngestorTests"
```

Expected: all tests pass.

### Step 6: Commit

```bash
git add src/Rag.NET/Ingestion/ tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs
git commit -m "feat: add PipelineIngestor + IngestionChain static lambda chain"
```

---

## Task 5: Retrieval Behaviors (Part 1) — ResultCache, LostInTheMiddle, Mmr, RedundancyFilter, ParentDocument

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/ResultCacheBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/LostInTheMiddleBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/MmrBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/RedundancyFilterBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/ParentDocumentRetrievalBehavior.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs`

### Step 1: Create behavior files

**`ResultCacheBehavior.cs`**
```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 10)]
public sealed class ResultCacheBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCacheResult || ctx.Cache is null || ctx.CachingOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var cacheKey = CacheKeyGenerator.ForResult(ctx.Query, ctx.Options);

        try
        {
            var results = await ctx.Cache.GetOrCreateAsync(
                cacheKey,
                async ct2 =>
                {
                    RagPipelineLog.ResultCacheMiss(ctx.Logger, ctx.Query);
                    var inner = await next(ctx, ct2).ConfigureAwait(false);
                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = ctx.CachingOptions.ResultTtl },
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

**`LostInTheMiddleBehavior.cs`**
```csharp
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 20)]
public sealed class LostInTheMiddleBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        return ctx.Options.UseLostInTheMiddleReordering
            ? LostInTheMiddleReorderer.Reorder(results)
            : results;
    }
}
```

**`MmrBehavior.cs`**
```csharp
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 30)]
public sealed class MmrBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseMmr)
            return await next(ctx, ct).ConfigureAwait(false);

        var candidateCount = ctx.Options.MmrCandidateCount ?? ctx.Options.TopK * 3;
        if (candidateCount < ctx.Options.TopK)
            RagPipelineLog.MmrCandidateCountLessThanTopK(ctx.Logger, candidateCount, ctx.Options.TopK);

        var expanded = ctx with { Options = ctx.Options with { TopK = candidateCount, UseMmr = false } };
        var candidates = await next(expanded, ct).ConfigureAwait(false);

        if (candidates.Count == 0)
            return candidates;

        try
        {
            var selected = await MmrSelector.SelectAsync(
                ctx.Query, candidates, ctx.Embedder,
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

**`RedundancyFilterBehavior.cs`**
```csharp
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 40)]
public sealed class RedundancyFilterBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        if (!ctx.Options.UseRedundancyFilter)
            return results;

        try
        {
            var filtered = await RedundancyFilter.FilterAsync(results, ctx.Embedder, ctx.Options.RedundancyThreshold, ct)
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

**`ParentDocumentRetrievalBehavior.cs`**
```csharp
using Rag.NET.Ingestion;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 50)]
public sealed class ParentDocumentRetrievalBehavior : IPipelineBehavior
{
    private const int OverFetchMultiplier = 3;

    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseParentDocument || ctx.ParentStore is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var expanded = ctx with { Options = ctx.Options with { TopK = ctx.Options.TopK * OverFetchMultiplier, UseParentDocument = false } };
        var childResults = await next(expanded, ct).ConfigureAwait(false);

        try
        {
            var parentGroups = new Dictionary<string, (SearchResult bestChild, double maxScore)>(StringComparer.Ordinal);
            var noParentResults = new List<SearchResult>();

            foreach (var result in childResults)
            {
                if (!result.Chunk.Metadata.TryGetValue(ParentChunkKeyHelper.ParentKeyMetadata, out var parentKey))
                {
                    noParentResults.Add(result);
                    continue;
                }
                if (parentGroups.TryGetValue(parentKey, out var existing))
                {
                    if (result.Score > existing.maxScore)
                        parentGroups[parentKey] = (result, result.Score);
                }
                else
                {
                    parentGroups[parentKey] = (result, result.Score);
                }
            }

            var results = new List<SearchResult>(parentGroups.Count + noParentResults.Count);
            foreach (var (parentKey, (bestChild, maxScore)) in parentGroups)
            {
                var parts = parentKey.Split(':');
                if (parts.Length == 2
                    && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pIdx)
                    && ctx.ParentStore.TryGet(parts[0], pIdx, out var parentText))
                {
                    results.Add(new SearchResult { Chunk = bestChild.Chunk with { Text = parentText! }, Score = maxScore });
                }
                else
                {
                    results.Add(bestChild);
                }
            }
            results.AddRange(noParentResults);
            results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            if (results.Count > ctx.Options.TopK) results.RemoveRange(ctx.Options.TopK, results.Count - ctx.Options.TopK);

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

### Step 2: Write tests

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
    private static readonly IReadOnlyList<SearchResult> EmptyResults = [];

    private static RetrievalContext MakeCtx(
        string query = "test",
        RetrievalOptions? options = null,
        IReranker? reranker = null,
        IQueryExpander? queryExpander = null,
        IParentChunkStore? parentStore = null) =>
        new()
        {
            Query = query,
            Options = options ?? new RetrievalOptions(),
            VectorStore = Substitute.For<IVectorStore>(),
            Embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            Bm25Index = Substitute.For<IBm25Index>(),
            Reranker = reranker,
            QueryExpander = queryExpander,
            ParentStore = parentStore,
        };

    private static IReadOnlyList<SearchResult> MakeResults(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new SearchResult { Chunk = new TextChunk { Text = $"chunk{i}", DocumentId = "doc", ChunkIndex = i }, Score = 1.0 - i * 0.1 })
            .ToList().AsReadOnly();

    // ── LostInTheMiddleBehavior ──────────────────────────────────────────

    [Fact]
    public async Task LostInTheMiddle_WhenFlagFalse_ReturnsNextResultsUnchanged()
    {
        var ctx = MakeCtx(options: new() { UseLostInTheMiddleReordering = false });
        var expected = MakeResults(3);
        var ct = TestContext.Current.CancellationToken;

        var result = await LostInTheMiddleBehavior.Handle(ctx, ct, (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }

    // ── RerankingBehavior ────────────────────────────────────────────────

    [Fact]
    public async Task Reranking_WhenRerankerNull_ReturnsNextResults()
    {
        var ctx = MakeCtx(options: new() { UseReranking = true }, reranker: null);
        var expected = MakeResults(3);
        var ct = TestContext.Current.CancellationToken;

        var result = await RerankingBehavior.Handle(ctx, ct, (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task Reranking_WhenFlagFalse_ReturnsNextResults()
    {
        var reranker = Substitute.For<IReranker>();
        var ctx = MakeCtx(options: new() { UseReranking = false }, reranker: reranker);
        var expected = MakeResults(3);
        var ct = TestContext.Current.CancellationToken;

        var result = await RerankingBehavior.Handle(ctx, ct, (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
        await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    // ── ParentDocumentRetrievalBehavior ──────────────────────────────────

    [Fact]
    public async Task ParentDocument_WhenParentStoreNull_ReturnsNextResults()
    {
        var ctx = MakeCtx(options: new() { UseParentDocument = true }, parentStore: null);
        var expected = MakeResults(3);
        var ct = TestContext.Current.CancellationToken;

        var result = await ParentDocumentRetrievalBehavior.Handle(ctx, ct, (_, _) => ValueTask.FromResult(expected));

        Assert.Same(expected, result);
    }
}
```

### Step 3: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "RetrievalBehaviorTests"
```

Expected: all tests pass.

### Step 4: Commit

```bash
git add src/Rag.NET/Retrieval/Behaviors/ tests/Rag.NET.Tests/Retrieval/Behaviors/
git commit -m "feat: add retrieval behaviors part 1 (ResultCache, LostInMiddle, Mmr, RedundancyFilter, ParentDoc)"
```

---

## Task 6: Retrieval Behaviors (Part 2) — Reranking, MultiQuery, Hyde, EmbeddingCache, VectorStore

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/RerankingBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/MultiQueryBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/HydeBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs`
- Create: `src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs`
- Modify: `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs` (append)

### Step 1: Create behavior files

**`RerankingBehavior.cs`**
```csharp
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 60)]
public sealed class RerankingBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseReranking || ctx.Reranker is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var candidateCount = ctx.Options.CandidateCount ?? ctx.Options.TopK * 3;
        var expanded = ctx with { Options = ctx.Options with { TopK = candidateCount, UseReranking = false } };
        var searchResults = await next(expanded, ct).ConfigureAwait(false);

        try
        {
            var reranked = await ctx.Reranker.RerankAsync(ctx.Query, searchResults, ct).ConfigureAwait(false);
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

**`MultiQueryBehavior.cs`**
```csharp
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.MultiQuery;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 70)]
public sealed class MultiQueryBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseMultiQuery || ctx.QueryExpander is null)
            return await next(ctx, ct).ConfigureAwait(false);

        IReadOnlyList<string> variants;
        try
        {
            var variantCount = ctx.MultiQueryOptions?.VariantCount ?? new MultiQueryOptions().VariantCount;
            variants = await ctx.QueryExpander.ExpandAsync(ctx.Query, variantCount, ct).ConfigureAwait(false);
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
        string query,
        RetrievalContext ctx,
        CancellationToken ct,
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

**`HydeBehavior.cs`**
```csharp
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 80)]
public sealed class HydeBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseHyde || ctx.HydeGenerator is null)
            return await next(ctx, ct).ConfigureAwait(false);

        try
        {
            var hypotheticalDoc = await ctx.HydeGenerator.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
            RagPipelineLog.HydeDocumentGenerated(ctx.Logger, ctx.Query, hypotheticalDoc.Length);
            return await next(
                ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = hypotheticalDoc } },
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

**`EmbeddingCacheBehavior.cs`**
```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 90)]
public sealed class EmbeddingCacheBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCacheEmbedding || ctx.Cache is null || ctx.CachingOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var textToEmbed = ctx.Options.EmbeddingTextOverride ?? ctx.Query;
        var cacheKey = CacheKeyGenerator.ForEmbedding(textToEmbed);

        try
        {
            var results = await ctx.Cache.GetOrCreateAsync(
                cacheKey,
                async ct2 =>
                {
                    RagPipelineLog.EmbeddingCacheMiss(ctx.Logger, ctx.Query);
                    var inner = await next(ctx, ct2).ConfigureAwait(false);
                    return inner as List<SearchResult> ?? inner.ToList();
                },
                new HybridCacheEntryOptions { Expiration = ctx.CachingOptions.EmbeddingTtl },
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

**`VectorStoreBehavior.cs`** (terminal — does not call `next`)
```csharp
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Search;
using ZeroAlloc.Pipeline;

namespace Rag.NET.Retrieval.Behaviors;

[PipelineBehavior(Order = 100)]
public sealed class VectorStoreBehavior : IPipelineBehavior
{
    public static async ValueTask<IReadOnlyList<SearchResult>> Handle(
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
        var queryEmbeddings = await ctx.Embedder.GenerateAsync([textToEmbed], cancellationToken: ct).ConfigureAwait(false);

        IReadOnlyList<SearchResult> results;
        string searchMode;

        if (opts.UseHybridSearch)
        {
            if (ctx.VectorStore is IHybridSearchable hybrid)
            {
                searchMode = "hybrid-native";
                results = await hybrid.HybridSearchAsync(ctx.Query, queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false);
            }
            else
            {
                searchMode = "hybrid-bm25-fallback";
                var denseTask = ctx.VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct);
                var bm25Hits = ctx.Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
                var dense = await denseTask.ConfigureAwait(false);
                results = RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
            }
        }
        else
        {
            searchMode = "dense";
            results = await ctx.VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false);
        }

        RagPipelineLog.VectorStoreSearchCompleted(ctx.Logger, searchMode, results.Count);
        return results;
    }
}
```

### Step 2: Append tests to `RetrievalBehaviorTests.cs`

```csharp
// ── HydeBehavior ─────────────────────────────────────────────────────

[Fact]
public async Task Hyde_WhenGeneratorNull_ReturnsNextResults()
{
    var ctx = MakeCtx(options: new() { UseHyde = true }) with { HydeGenerator = null };
    var expected = MakeResults(3);
    var ct = TestContext.Current.CancellationToken;

    var result = await HydeBehavior.Handle(ctx, ct, (_, _) => ValueTask.FromResult(expected));

    Assert.Same(expected, result);
}

[Fact]
public async Task Hyde_WhenEnabled_PassesHypotheticalDocAsEmbeddingOverride()
{
    var generator = Substitute.For<IHypotheticalDocumentGenerator>();
    generator.GenerateAsync("test", Arg.Any<CancellationToken>()).Returns("hypothetical answer");
    var ctx = MakeCtx(options: new() { UseHyde = true }) with { HydeGenerator = generator };
    string? capturedOverride = null;
    var ct = TestContext.Current.CancellationToken;

    await HydeBehavior.Handle(ctx, ct, (c, t) =>
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
    var ctx = MakeCtx(options: new() { UseMultiQuery = true }, queryExpander: null);
    var expected = MakeResults(3);
    var ct = TestContext.Current.CancellationToken;

    var result = await MultiQueryBehavior.Handle(ctx, ct, (_, _) => ValueTask.FromResult(expected));

    Assert.Same(expected, result);
}
```

### Step 3: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "RetrievalBehaviorTests"
```

Expected: all tests pass.

### Step 4: Commit

```bash
git add src/Rag.NET/Retrieval/Behaviors/ tests/Rag.NET.Tests/Retrieval/Behaviors/
git commit -m "feat: add retrieval behaviors part 2 (Reranking, MultiQuery, Hyde, EmbeddingCache, VectorStore)"
```

---

## Task 7: PipelineRetriever + RetrievalChain

**Files:**
- Create: `src/Rag.NET/Retrieval/RetrievalChain.cs`
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
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class PipelineRetrieverTests
{
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly IBm25Index _bm25 = Substitute.For<IBm25Index>();

    private PipelineRetriever CreateSut(IReranker? reranker = null, IParentChunkStore? parentStore = null) =>
        new(_vectorStore, _embedder, _bm25, reranker: reranker, parentStore: parentStore);

    [Fact]
    public async Task RetrieveAsync_CallsVectorStoreWithEmbeddedQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        var embedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
            .Returns([]);

        var result = await sut.RetrieveAsync("hello", new RetrievalOptions { TopK = 3 }, ct);

        Assert.NotNull(result);
        await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct);
    }
}
```

### Step 2: Run test to verify it fails

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineRetrieverTests"
```

Expected: FAIL — `PipelineRetriever` does not exist yet.

### Step 3: Create `RetrievalChain.cs`

```csharp
using Rag.NET.Models;
using Rag.NET.Retrieval.Behaviors;

namespace Rag.NET.Retrieval;

/// <summary>
/// Hand-written static lambda chain for the retrieval pipeline.
/// Zero-allocation: all lambdas are static closures; no per-call delegate allocation.
/// Order: ResultCache(10) → LostInMiddle(20) → Mmr(30) → RedundancyFilter(40)
///      → ParentDoc(50) → Reranking(60) → MultiQuery(70) → Hyde(80)
///      → EmbeddingCache(90) → VectorStore(100, terminal)
/// </summary>
internal static class RetrievalChain
{
    public static ValueTask<IReadOnlyList<SearchResult>> ExecuteAsync(RetrievalContext ctx, CancellationToken ct) =>
        ResultCacheBehavior.Handle(ctx, ct, static (ctx, ct) =>
        LostInTheMiddleBehavior.Handle(ctx, ct, static (ctx, ct) =>
        MmrBehavior.Handle(ctx, ct, static (ctx, ct) =>
        RedundancyFilterBehavior.Handle(ctx, ct, static (ctx, ct) =>
        ParentDocumentRetrievalBehavior.Handle(ctx, ct, static (ctx, ct) =>
        RerankingBehavior.Handle(ctx, ct, static (ctx, ct) =>
        MultiQueryBehavior.Handle(ctx, ct, static (ctx, ct) =>
        HydeBehavior.Handle(ctx, ct, static (ctx, ct) =>
        EmbeddingCacheBehavior.Handle(ctx, ct, static (ctx, ct) =>
        VectorStoreBehavior.Handle(ctx, ct, static (_, _) =>
            ValueTask.FromResult<IReadOnlyList<SearchResult>>([])))))))))));
}
```

### Step 4: Create `PipelineRetriever.cs`

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;

namespace Rag.NET.Retrieval;

/// <summary>
/// Facade over the source-generated retrieval pipeline.
/// Implements <see cref="IRetriever"/> — replaces the nested decorator factory.
/// </summary>
public sealed class PipelineRetriever(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IBm25Index bm25Index,
    IReranker? reranker = null,
    IQueryExpander? queryExpander = null,
    MultiQueryOptions? multiQueryOptions = null,
    IHypotheticalDocumentGenerator? hydeGenerator = null,
    IParentChunkStore? parentStore = null,
    HybridCache? cache = null,
    CachingOptions? cachingOptions = null,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new RetrievalContext
        {
            Query = query,
            Options = options ?? new RetrievalOptions(),
            VectorStore = vectorStore,
            Embedder = embeddingGenerator,
            Bm25Index = bm25Index,
            Reranker = reranker,
            QueryExpander = queryExpander,
            MultiQueryOptions = multiQueryOptions,
            HydeGenerator = hydeGenerator,
            ParentStore = parentStore,
            Cache = cache,
            CachingOptions = cachingOptions,
            Logger = _logger,
        };

        return RetrievalChain.ExecuteAsync(ctx, cancellationToken).AsTask();
    }
}
```

### Step 5: Run tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q --filter "PipelineRetrieverTests"
```

Expected: all tests pass.

### Step 6: Commit

```bash
git add src/Rag.NET/Retrieval/ tests/Rag.NET.Tests/Retrieval/PipelineRetrieverTests.cs
git commit -m "feat: add PipelineRetriever + RetrievalChain static lambda chain"
```

---

## Task 8: Update DI Wiring

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`

### Step 1: Replace `DocumentIngestor` factory with `PipelineIngestor`

Find the `AddSingleton<IIngestor>(sp => new DocumentIngestor(...))` block and replace with:

```csharp
services.AddSingleton<IIngestor>(sp => new PipelineIngestor(
    sp.GetServices<IDocumentParser>(),
    sp.GetRequiredService<IChunkingStrategy>(),
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
    sp.GetRequiredService<ChunkingOptions>(),
    sp.GetRequiredService<IBm25Index>(),
    sp.GetService<IParentChunkStore>(),
    sp.GetService<ParentDocumentOptions>(),
    sp.GetService<IRagDataManager>()));
```

Add `using Rag.NET.Ingestion;` at the top if not already present.

### Step 2: Replace `BuildRetrieverChain` factory with `PipelineRetriever`

Remove the entire `BuildRetrieverChain` and `WrapWithQueryDecorators` private methods. Replace `services.AddSingleton<IRetriever>(sp => BuildRetrieverChain(sp));` with:

```csharp
services.AddSingleton<IRetriever>(sp => new PipelineRetriever(
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
    sp.GetRequiredService<IBm25Index>(),
    reranker: sp.GetService<IReranker>(),
    queryExpander: sp.GetService<IQueryExpander>(),
    multiQueryOptions: sp.GetService<MultiQueryOptions>(),
    hydeGenerator: sp.GetService<IHypotheticalDocumentGenerator>(),
    parentStore: sp.GetService<IParentChunkStore>(),
    cache: sp.GetService<HybridCache>(),
    cachingOptions: sp.GetService<CachingOptions>(),
    logger: sp.GetService<ILogger<PipelineRetriever>>()));
```

Remove the now-unused `using Rag.NET.Retrieval;` imports for the individual decorators if any remain.

### Step 3: Build and run ALL tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q
```

Expected: all 321+ tests pass, 0 failures.

### Step 4: Commit

```bash
git add src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "refactor: wire PipelineIngestor and PipelineRetriever in DI"
```

---

## Task 9: Delete Old Files + Final Verification

**Files to delete:**
- `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- `src/Rag.NET/Retrieval/VectorStoreRetriever.cs`
- `src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs`
- `src/Rag.NET/Retrieval/HydeRetriever.cs`
- `src/Rag.NET/Retrieval/MultiQueryRetriever.cs`
- `src/Rag.NET/Retrieval/RerankingRetriever.cs`
- `src/Rag.NET/Retrieval/ParentDocumentRetriever.cs`
- `src/Rag.NET/Retrieval/RedundancyFilterRetriever.cs`
- `src/Rag.NET/Retrieval/MmrRetriever.cs`
- `src/Rag.NET/Retrieval/LostInTheMiddleRetriever.cs`
- `src/Rag.NET/Retrieval/ResultCacheRetriever.cs`

**Tests to delete:**
- `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs` (replaced by `PipelineIngestorTests.cs`)

### Step 1: Delete files

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

### Step 2: Build and run ALL tests

```bash
dotnet msbuild tests/Rag.NET.Tests/Rag.NET.Tests.csproj -q
dotnet test tests/Rag.NET.Tests --no-build -q
```

Expected: build succeeds, all tests pass, 0 failures.
If any compile errors reference deleted classes, fix them (likely in `ServiceCollectionExtensions.cs` — remove stale `using` directives).

### Step 3: Commit

```bash
git add -A
git commit -m "refactor: remove DocumentIngestor and retrieval decorator chain (replaced by pipeline behaviors)"
```
