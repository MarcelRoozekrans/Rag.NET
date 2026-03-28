# Time-Weighted Retrieval Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `TimeWeightedRetriever` `IRetriever` decorator that re-scores search results by multiplying similarity scores with an exponential time-decay factor derived from `DocumentMetadata.CreatedAt`.

**Architecture:** `DocumentMetadata` gains a non-nullable `DateTime CreatedAt` property (defaults to `DateTime.UtcNow`). `MetadataBehavior` serialises it into `chunk.Metadata["created_at"]` at ingest time. `TimeWeightedRetriever` wraps any `IRetriever`, reads the timestamp from chunk metadata, computes `score × e^(−λ × age_hours)`, and re-sorts results before returning them to the caller.

**Tech Stack:** .NET 10, xUnit, NSubstitute, ZeroAlloc.Results, Microsoft.Extensions.DependencyInjection.

---

## Codebase orientation

- `src/Rag.NET/Models/DocumentMetadata.cs` — model with `Tags` dict, `DocumentId`, `FileName`
- `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs` — copies tags + reserved keys into every `TextChunk.Metadata`
- `src/Rag.NET/Models/Options/RetrievalOptions.cs` — per-call options record with `UseTagRetrieval`, `UseHyde`, etc.
- `src/Rag.NET/Retrieval/TagRetriever.cs` — reference decorator to copy pattern from
- `src/Rag.NET/DependencyInjection/RagBuilder.cs` — fluent builder; `UseTagRetrieval()` is the reference registration
- `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` — `WireDeepResearch`, `WireTagRetrieval`, `AddRagNet`
- `tests/Rag.NET.Tests/Retrieval/TagRetrieverTests.cs` — reference test file for decorator tests
- `tests/Rag.NET.Tests/DependencyInjection/UseTagRetrievalTests.cs` — reference test file for DI tests

**Key patterns:**
- `Result<IReadOnlyList<SearchResult>, RagError>` — return type of `IRetriever.RetrieveAsync`
- `TestContext.Current.CancellationToken` — cancellation token in xUnit tests
- `StringComparer.Ordinal` — required for all `new Dictionary<string, string>` (MA0002 analyzer)
- `FakeLogger<T>` nested class — copy from `TagRetrieverTests` for tests that verify logging
- Run tests: `dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~ClassName"`

---

## Task 1: `DocumentMetadata.CreatedAt` + `MetadataBehavior` serialisation

**Files:**
- Modify: `src/Rag.NET/Models/DocumentMetadata.cs`
- Modify: `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/MetadataBehaviorCreatedAtTests.cs`

### Step 1: Write the failing tests

Create `tests/Rag.NET.Tests/Ingestion/MetadataBehaviorCreatedAtTests.cs`:

```csharp
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class MetadataBehaviorCreatedAtTests
{
    private static IngestionContext MakeCtx(DocumentMetadata metadata)
    {
        var ctx = new IngestionContext
        {
            Stream           = Stream.Null,
            Metadata         = metadata,
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text       = "hello",
            DocumentId = metadata.DocumentId,
            ChunkIndex = 0,
        });
        return ctx;
    }

    private static ValueTask<IngestionResult> NullNext(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

    [Fact]
    public async Task CreatedAt_SerializedIntoChunkMetadata()
    {
        var ct        = TestContext.Current.CancellationToken;
        var createdAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
            CreatedAt  = createdAt,
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        Assert.True(ctx.Chunks[0].Metadata.TryGetValue("created_at", out var value));
        Assert.Equal(createdAt.ToString("O"), value);
    }

    [Fact]
    public async Task CreatedAt_ExistingTagPreservedViaTryAdd()
    {
        var ct  = TestContext.Current.CancellationToken;
        var ctx = MakeCtx(new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName   = "doc1.txt",
            CreatedAt  = DateTime.UtcNow,
            Tags       = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["created_at"] = "2020-01-01T00:00:00.0000000Z",
            },
        });

        var sut = new MetadataBehavior();
        await sut.HandleAsync(ctx, ct, NullNext);

        // Tags are copied first; TryAdd on "created_at" from CreatedAt property then does nothing
        Assert.Equal("2020-01-01T00:00:00.0000000Z", ctx.Chunks[0].Metadata["created_at"]);
    }
}
```

### Step 2: Run to verify it fails

```
dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~MetadataBehaviorCreatedAtTests"
```

Expected: FAIL — `DocumentMetadata` has no `CreatedAt` property.

### Step 3: Add `CreatedAt` to `DocumentMetadata`

In `src/Rag.NET/Models/DocumentMetadata.cs`, add the property after `Tags`:

```csharp
public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
```

Full file after change:

```csharp
using ZeroAlloc.Validation;

namespace Rag.NET.Models;

[Validate]
public sealed class DocumentMetadata
{
    public required DocumentId DocumentId { get; init; }

    [NotEmpty]
    public required string FileName { get; init; }

    public string? ContentType { get; init; }
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Creation or publication timestamp. Defaults to <see cref="DateTime.UtcNow"/> (ingest time)
    /// when not set explicitly. Serialised into chunk metadata as <c>"created_at"</c> by
    /// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/> for use by
    /// <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/>.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
```

### Step 4: Serialise `CreatedAt` in `MetadataBehavior`

In `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs`, add the `TryAdd` call after `file_name`:

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
            chunk.Metadata.TryAdd("file_name",   ctx.Metadata.FileName);
            chunk.Metadata.TryAdd("created_at",  ctx.Metadata.CreatedAt.ToString("O"));
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

### Step 5: Run to verify tests pass

```
dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~MetadataBehaviorCreatedAtTests"
```

Expected: PASS (2 tests).

### Step 6: Run full suite to check no regressions

```
dotnet test tests/Rag.NET.Tests/
```

Expected: all tests pass.

### Step 7: Commit

```bash
git add src/Rag.NET/Models/DocumentMetadata.cs \
        src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs \
        tests/Rag.NET.Tests/Ingestion/MetadataBehaviorCreatedAtTests.cs
git commit -m "feat: add DocumentMetadata.CreatedAt and serialize to chunk metadata"
```

---

## Task 2: `TimeWeightedOptions` + `TimeWeightedRetriever`

**Files:**
- Create: `src/Rag.NET/Models/Options/TimeWeightedOptions.cs`
- Create: `src/Rag.NET/Retrieval/TimeWeightedRetriever.cs`
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/TimeWeightedRetrieverTests.cs`

### Step 1: Write the failing tests

Create `tests/Rag.NET.Tests/Retrieval/TimeWeightedRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class TimeWeightedRetrieverTests
{
    private static TextChunk MakeChunk(string? createdAt = null)
    {
        var chunk = new TextChunk
        {
            Text       = "content",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
        };
        if (createdAt is not null)
            chunk.Metadata["created_at"] = createdAt;
        return chunk;
    }

    private static IRetriever MockInner(IReadOnlyList<SearchResult> results)
    {
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success(results));
        return inner;
    }

    [Fact]
    public async Task OldDocument_ScoreReducedByDecay()
    {
        var ct        = TestContext.Current.CancellationToken;
        var createdAt = DateTime.UtcNow.AddHours(-100); // 100 hours ago → e^(-0.01×100) ≈ 0.368
        var chunk     = MakeChunk(createdAt.ToString("O"));
        var inner     = MockInner([new SearchResult { Chunk = chunk, Score = 1.0 }]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions { DecayRate = 0.01 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // e^(-1) ≈ 0.368 — accept small timing jitter
        Assert.InRange(result.Value[0].Score, 0.35, 0.39);
    }

    [Fact]
    public async Task TwoResults_ResortedByDecayedScore()
    {
        var ct     = TestContext.Current.CancellationToken;
        var fresh  = MakeChunk(DateTime.UtcNow.AddHours(-1).ToString("O"));   // barely decayed
        var old    = MakeChunk(DateTime.UtcNow.AddHours(-100).ToString("O")); // heavily decayed

        // Old document has higher raw similarity but ages out
        var inner = MockInner([
            new SearchResult { Chunk = old,   Score = 0.95 },
            new SearchResult { Chunk = fresh, Score = 0.80 },
        ]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions { DecayRate = 0.01 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // Fresh document (score 0.80 × ~0.99) must outrank old document (0.95 × ~0.368)
        Assert.Equal(fresh.DocumentId, result.Value[0].Chunk.DocumentId);
        Assert.Equal(old.DocumentId,   result.Value[1].Chunk.DocumentId);
    }

    [Fact]
    public async Task NoTimestamp_ScoreUnchanged()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = MakeChunk(); // no created_at metadata
        var inner = MockInner([new SearchResult { Chunk = chunk, Score = 0.75 }]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.Equal(0.75, result.Value[0].Score);
    }

    [Fact]
    public async Task InvalidTimestamp_TreatedAsNoTimestamp()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = MakeChunk("not-a-date");
        var inner = MockInner([new SearchResult { Chunk = chunk, Score = 0.75 }]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.Equal(0.75, result.Value[0].Score);
    }

    [Fact]
    public async Task UseTimeWeightingFalse_InnerCalledWithOriginalOptions_ScoresUnchanged()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = MakeChunk(DateTime.UtcNow.AddHours(-100).ToString("O"));

        RetrievalOptions? captured = null;
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => captured = o), ct)
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                 [new SearchResult { Chunk = chunk, Score = 0.9 }]));

        var opts   = new RetrievalOptions { UseTimeWeighting = false };
        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions { DecayRate = 0.01 });
        var result = await sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(0.9, result.Value[0].Score);  // score unchanged
        Assert.False(captured?.UseTimeWeighting);   // original options passed through
    }

    [Fact]
    public async Task FallbackMetadataKey_UsedWhenCreatedAtAbsent()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = new TextChunk
        {
            Text       = "content",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
        };
        chunk.Metadata["published_at"] = DateTime.UtcNow.AddHours(-100).ToString("O");
        // no "created_at" key

        var inner  = MockInner([new SearchResult { Chunk = chunk, Score = 1.0 }]);
        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions
        {
            DecayRate         = 0.01,
            FallbackMetadataKeys = ["published_at"],
        });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.InRange(result.Value[0].Score, 0.35, 0.39); // decay applied via fallback
    }

    [Fact]
    public async Task FallbackMetadataKeys_FirstParseableWins()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = new TextChunk
        {
            Text       = "content",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
        };
        chunk.Metadata["key_a"] = "not-a-date";                                        // unparseable
        chunk.Metadata["key_b"] = DateTime.UtcNow.AddHours(-100).ToString("O");        // parseable

        var inner  = MockInner([new SearchResult { Chunk = chunk, Score = 1.0 }]);
        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions
        {
            DecayRate            = 0.01,
            FallbackMetadataKeys = ["key_a", "key_b"],
        });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.InRange(result.Value[0].Score, 0.35, 0.39); // key_b used
    }
}
```

### Step 2: Run to verify it fails

```
dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~TimeWeightedRetrieverTests"
```

Expected: FAIL — `TimeWeightedRetriever` does not exist.

### Step 3: Add `UseTimeWeighting` to `RetrievalOptions`

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add after `UseTagRetrieval`:

```csharp
    /// <summary>
    /// Set to <see langword="false"/> to skip time-weighted re-scoring for this call,
    /// even when <c>RagBuilder.UseTimeWeighting()</c> is registered.
    /// Has no effect when time-weighting is not registered.
    /// </summary>
    public bool UseTimeWeighting { get; init; } = true;
```

### Step 4: Create `TimeWeightedOptions`

Create `src/Rag.NET/Models/Options/TimeWeightedOptions.cs`:

```csharp
namespace Rag.NET.Models.Options;

public sealed class TimeWeightedOptions
{
    /// <summary>
    /// Decay constant λ in <c>score × e^(−λ × age_hours)</c>.
    /// Default 0.01 halves relevance after ~69 hours (~3 days).
    /// </summary>
    public double DecayRate { get; init; } = 0.01;

    /// <summary>
    /// Ordered list of <see cref="Rag.NET.Models.TextChunk"/> metadata keys to check
    /// when the primary <c>"created_at"</c> key is absent.
    /// First key with a parseable ISO 8601 value wins.
    /// Useful for documents from external systems that store timestamps under a different key.
    /// </summary>
    public IReadOnlyList<string> FallbackMetadataKeys { get; init; } = [];
}
```

### Step 5: Create `TimeWeightedRetriever`

Create `src/Rag.NET/Retrieval/TimeWeightedRetriever.cs`:

```csharp
using System.Globalization;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Retrieval;

/// <summary>
/// <see cref="IRetriever"/> decorator that re-scores search results by multiplying each
/// similarity score by <c>e^(−λ × age_hours)</c> where age is derived from
/// <c>chunk.Metadata["created_at"]</c> (written at ingest time by
/// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/> from
/// <see cref="Rag.NET.Models.DocumentMetadata.CreatedAt"/>).
/// Results are re-sorted by the combined score before being returned.
/// </summary>
public sealed class TimeWeightedRetriever : IRetriever
{
    internal const string CreatedAtKey = "created_at";

    private readonly IRetriever _inner;
    private readonly TimeWeightedOptions _options;
    private readonly ILogger<TimeWeightedRetriever>? _logger;

    public TimeWeightedRetriever(
        IRetriever inner,
        TimeWeightedOptions options,
        ILogger<TimeWeightedRetriever>? logger = null)
    {
        _inner   = inner;
        _options = options;
        _logger  = logger;
    }

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new RetrievalOptions();

        if (!effective.UseTimeWeighting)
            return await _inner.RetrieveAsync(query, effective, cancellationToken).ConfigureAwait(false);

        var result = await _inner.RetrieveAsync(query, effective, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return result;

        var now = DateTime.UtcNow;
        List<SearchResult> rescored = result.Value
            .Select(r => r with { Score = r.Score * ComputeDecay(r.Chunk, now) })
            .OrderByDescending(r => r.Score)
            .ToList();

        return Result<IReadOnlyList<SearchResult>, RagError>.Success(rescored);
    }

    private double ComputeDecay(TextChunk chunk, DateTime now)
    {
        var timestamp = ResolveTimestamp(chunk);
        if (timestamp is null)
            return 1.0;

        var ageHours = (now - timestamp.Value).TotalHours;
        return Math.Exp(-_options.DecayRate * ageHours);
    }

    private DateTime? ResolveTimestamp(TextChunk chunk)
    {
        if (chunk.Metadata.TryGetValue(CreatedAtKey, out var raw) &&
            DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        foreach (var key in _options.FallbackMetadataKeys)
        {
            if (chunk.Metadata.TryGetValue(key, out var fallback) &&
                DateTime.TryParse(fallback, null, DateTimeStyles.RoundtripKind, out var fdt))
                return fdt;
        }

        return null;
    }
}
```

### Step 6: Run to verify tests pass

```
dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~TimeWeightedRetrieverTests"
```

Expected: PASS (7 tests).

### Step 7: Run full suite

```
dotnet test tests/Rag.NET.Tests/
```

Expected: all tests pass.

### Step 8: Commit

```bash
git add src/Rag.NET/Models/Options/TimeWeightedOptions.cs \
        src/Rag.NET/Retrieval/TimeWeightedRetriever.cs \
        src/Rag.NET/Models/Options/RetrievalOptions.cs \
        tests/Rag.NET.Tests/Retrieval/TimeWeightedRetrieverTests.cs
git commit -m "feat: implement TimeWeightedRetriever with exponential decay scoring"
```

---

## Task 3: DI wiring

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseTimeWeightingTests.cs`

### Step 1: Write the failing tests

Create `tests/Rag.NET.Tests/DependencyInjection/UseTimeWeightingTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseTimeWeightingTests
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
    public void UseTimeWeighting_IRetrieverIsTimeWeightedRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTimeWeighting()).BuildServiceProvider();
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_DefaultOptions_DecayRateIs001()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTimeWeighting()).BuildServiceProvider();
        Assert.Equal(0.01, sp.GetRequiredService<TimeWeightedOptions>().DecayRate);
    }

    [Fact]
    public void UseTimeWeighting_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions { DecayRate = 0.005 }))
            .BuildServiceProvider();
        Assert.Equal(0.005, sp.GetRequiredService<TimeWeightedOptions>().DecayRate);
    }

    [Fact]
    public void WithoutUseTimeWeighting_IRetrieverIsPipelineRetriever()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.IsType<PipelineRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_And_UseTagRetrieval_TagRetrieverWrapsTimeWeighted()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseTimeWeighting().UseTagRetrieval())
            .BuildServiceProvider();

        // TagRetriever is outermost
        Assert.IsType<TagRetriever>(sp.GetRequiredService<IRetriever>());
        // TimeWeightedRetriever is registered as concrete
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<TimeWeightedRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_And_UseDeepResearch_And_UseTagRetrieval_FullStack()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch().UseTimeWeighting().UseTagRetrieval())
            .BuildServiceProvider();

        // TagRetriever is outermost
        Assert.IsType<TagRetriever>(sp.GetRequiredService<IRetriever>());
        // Both inner decorators registered as concrete types
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<TimeWeightedRetriever>());
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<DeepResearchRetriever>());
    }
}
```

### Step 2: Run to verify it fails

```
dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~UseTimeWeightingTests"
```

Expected: FAIL — `UseTimeWeighting` method does not exist on `RagBuilder`.

### Step 3: Add `UseTimeWeighting` to `RagBuilder`

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`, add after `UseTagRetrieval`:

```csharp
    /// <summary>
    /// Registers <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/> as a decorator over the
    /// existing <see cref="IRetriever"/>. After retrieval, each result's similarity score is
    /// multiplied by <c>e^(−DecayRate × age_hours)</c> where age is derived from
    /// <c>chunk.Metadata["created_at"]</c> written at ingest time by
    /// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/>.
    /// Results are re-sorted by the combined score before being returned.
    /// </summary>
    /// <remarks>
    /// The decorator is wired by <c>AddRagNet</c> after the builder delegate returns.
    /// When combined with other decorators, stacking order (outermost first) is:
    /// <c>TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever</c>.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseTimeWeighting = false }</c>.
    /// </remarks>
    public RagBuilder UseTimeWeighting(TimeWeightedOptions? options = null)
    {
        Services.AddSingleton(options ?? new TimeWeightedOptions());
        return this;
    }
```

Also add `using Rag.NET.Models.Options;` if not already present (it is already present in the file).

### Step 4: Add `WireTimeWeighting` to `ServiceCollectionExtensions`

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`:

**4a.** Add `using Rag.NET.Retrieval;` if not already present (it is).

**4b.** Add the `WireTimeWeighting` static method after `WireDeepResearch`:

```csharp
    private static void WireTimeWeighting(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeWeightedOptions)))
            return;

        // DeepResearchRetriever descriptor is registered by WireDeepResearch (called above in AddRagNet).
        // Ordering is load-bearing: WireDeepResearch must run before WireTimeWeighting.
        bool hasDeepResearch = services.Any(d => d.ServiceType == typeof(DeepResearchRetriever));

        // When DeepResearch is not wired, PipelineRetriever may not be registered as its own
        // concrete type. Register it here so TimeWeightedRetriever can wrap it — same pattern
        // as WireDeepResearch.
        if (!hasDeepResearch)
        {
            services.TryAddSingleton<PipelineRetriever>(sp => new PipelineRetriever
            {
                Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
                Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
            });
        }

        services.AddSingleton<TimeWeightedRetriever>(sp =>
        {
            IRetriever inner = hasDeepResearch
                ? sp.GetRequiredService<DeepResearchRetriever>()
                : (IRetriever)sp.GetRequiredService<PipelineRetriever>();

            return new TimeWeightedRetriever(
                inner,
                sp.GetRequiredService<TimeWeightedOptions>(),
                sp.GetService<ILogger<TimeWeightedRetriever>>());
        });

        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<TimeWeightedRetriever>());
    }
```

**4c.** Update `WireTagRetrieval` to resolve `TimeWeightedRetriever` as its inner when present.

Replace the inner-resolution block in `WireTagRetrieval`. The full updated method:

```csharp
    private static void WireTagRetrieval(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TagRetrievalOptions)))
            return;

        // DeepResearchRetriever and TimeWeightedRetriever descriptors are registered by their
        // respective Wire* methods (called above in AddRagNet).
        // Ordering is load-bearing: WireDeepResearch and WireTimeWeighting must run before WireTagRetrieval.
        bool hasDeepResearch = services.Any(d => d.ServiceType == typeof(DeepResearchRetriever));
        bool hasTimeWeighted = services.Any(d => d.ServiceType == typeof(TimeWeightedRetriever));

        // When neither DeepResearch nor TimeWeighted is wired, PipelineRetriever was never
        // registered as its concrete type. Register it so TagRetriever can wrap it.
        if (!hasDeepResearch && !hasTimeWeighted)
        {
            services.AddSingleton<PipelineRetriever>(sp => new PipelineRetriever
            {
                Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
                Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
            });
        }

        // Stacking order (outermost first):
        // TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever
        services.AddSingleton<TagRetriever>(sp =>
        {
            IRetriever inner;
            if (hasTimeWeighted)
                inner = sp.GetRequiredService<TimeWeightedRetriever>();
            else if (hasDeepResearch)
                inner = sp.GetRequiredService<DeepResearchRetriever>();
            else
                inner = sp.GetRequiredService<PipelineRetriever>();

            return new TagRetriever(
                inner,
                sp.GetRequiredService<ITagIndex>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<TagRetrievalOptions>(),
                sp.GetService<ILogger<TagRetriever>>());
        });

        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<TagRetriever>());
    }
```

**4d.** Update `AddRagNet` to call `WireTimeWeighting` between `WireDeepResearch` and `WireTagRetrieval`:

```csharp
        var builder = new RagBuilder(services);
        configure?.Invoke(builder);
        WireRefinementStrategy(services);
        WireDeepResearch(services);
        WireTimeWeighting(services);    // ← add this line
        WireTagRetrieval(services);
```

### Step 5: Run to verify tests pass

```
dotnet test tests/Rag.NET.Tests/ --filter "FullyQualifiedName~UseTimeWeightingTests"
```

Expected: PASS (6 tests).

### Step 6: Run full suite

```
dotnet test tests/Rag.NET.Tests/
```

Expected: all tests pass. If `UseTagRetrievalTests.UseTagRetrieval_And_UseDeepResearch_TagRetrieverWrapsDeepResearch` fails — that test checks `TagRetriever` wraps `DeepResearchRetriever` when only those two are registered. Verify the stacking logic is correct (no `TimeWeightedRetriever` registered in that test).

### Step 7: Commit

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs \
        src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs \
        tests/Rag.NET.Tests/DependencyInjection/UseTimeWeightingTests.cs
git commit -m "feat: wire TimeWeightedRetriever DI registration and decorator stacking"
```

---

## Task 4: Docs

**Files:**
- Modify: `docs/reference/features.md`
- Modify: `docs/guide/retrieval.md`

### Step 1: Mark feature done in `features.md`

In `docs/reference/features.md`, find the `### Time-Weighted Retrieval` section and add the status line + priority table update.

After the `**Why:**` paragraph, add:

```markdown
**Status:** ✅ Done
```

In the priority table at the bottom, find:
```
| [ ] | Time-Weighted Retrieval |
```
and change `[ ]` to `[x]`.

Wait — check the priority table first. The current file does NOT have a `Time-Weighted Retrieval` row in the table. Add this row after `| [x] | Tag-Based Retrieval | Medium | Hybrid search |`:

```
| [x] | Time-Weighted Retrieval | Medium | None |
```

### Step 2: Add section to `docs/guide/retrieval.md`

Read the existing file first, then append a `## Time-Weighted Retrieval` section after the existing `## Tag-Based Retrieval` section.

The section to add:

```markdown
---

## Time-Weighted Retrieval

Rag.NET can automatically discount older documents by multiplying each result's similarity score by an exponential decay factor. Fresher documents retain their original score; documents older than a few days decay toward zero.

### Enabling

```csharp
services.AddRagNet(rag => rag.UseTimeWeighting());
```

With custom decay rate:

```csharp
services.AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions
{
    DecayRate            = 0.005,                          // slower decay — ~6 days to halve
    FallbackMetadataKeys = ["published_at", "event_date"], // external timestamp fields
}));
```

### `TimeWeightedOptions`

| Option | Default | Description |
|--------|---------|-------------|
| `DecayRate` | `0.01` | λ in `score × e^(−λ × age_hours)`. Default halves relevance at ~69 hours (~3 days). |
| `FallbackMetadataKeys` | `[]` | Metadata keys to try (in order) when `"created_at"` is absent. First parseable ISO 8601 value wins. |

### How timestamps are set

`DocumentMetadata.CreatedAt` defaults to `DateTime.UtcNow` at ingest time. Override it for documents with a known publication date:

```csharp
await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId = new DocumentId("release-notes-v3"),
    FileName   = "release-notes-v3.md",
    CreatedAt  = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
});
```

`MetadataBehavior` serialises `CreatedAt` into each chunk's metadata as `"created_at"` (ISO 8601). `TimeWeightedRetriever` reads this key at query time.

### Per-call opt-out

```csharp
var results = await pipeline.RetrieveAsync("query", new RetrievalOptions
{
    UseTimeWeighting = false,
});
```

### Decorator stacking

When combined with other decorators, the call order is:

```
TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever
```

Tag filtering narrows candidates first; time-weighted re-scoring is applied to the final result set.
```

### Step 3: Run full suite to verify docs changes didn't break anything

```
dotnet test tests/Rag.NET.Tests/
```

Expected: all tests pass.

### Step 4: Commit

```bash
git add docs/reference/features.md docs/guide/retrieval.md
git commit -m "docs: document Time-Weighted Retrieval feature"
```
