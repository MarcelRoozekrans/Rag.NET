# Search Result Caching Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add two-level retrieval caching (embedding cache + full result cache) via HybridCache to reduce embedding API and vector store costs on repeated queries.

**Architecture:** Two new IRetriever decorators — `EmbeddingCacheRetriever` (innermost, caches query→embedding) and `ResultCacheRetriever` (outermost, caches final results) — backed by `Microsoft.Extensions.Caching.Hybrid.HybridCache`. Both follow the established decorator pattern with per-call opt-out flags on `RetrievalOptions`.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Caching.Hybrid`, xUnit v3, NSubstitute, BenchmarkDotNet

---

### Task 1: Add NuGet dependency and CachingOptions

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`
- Create: `src/Rag.NET/Models/Options/CachingOptions.cs`

**Step 1: Add HybridCache package reference**

In `src/Rag.NET/Rag.NET.csproj`, add inside the existing `<ItemGroup>` with other `PackageReference` entries (after line 19):

```xml
<PackageReference Include="Microsoft.Extensions.Caching.Hybrid" Version="9.*" />
```

**Step 2: Create CachingOptions**

Create `src/Rag.NET/Models/Options/CachingOptions.cs`:

```csharp
namespace Rag.NET.Models.Options;

public class CachingOptions
{
    public TimeSpan EmbeddingTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ResultTtl { get; set; } = TimeSpan.FromMinutes(5);
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Rag.NET --nologo -v q`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Rag.NET/Rag.NET.csproj src/Rag.NET/Models/Options/CachingOptions.cs
git commit -m "feat: add HybridCache dependency and CachingOptions"
```

---

### Task 2: Add caching flags to RetrievalOptions

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs:40-48`

**Step 1: Add cache flags**

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add after the `UseHyde` property (after line 40) and before `EmbeddingTextOverride`:

```csharp
    /// <summary>
    /// Set to <see langword="false"/> to skip embedding caching for this call,
    /// even when caching is registered via <c>RagBuilder.UseCaching()</c>.
    /// Has no effect when caching is not registered.
    /// </summary>
    public bool UseCacheEmbedding { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip result caching for this call,
    /// even when caching is registered via <c>RagBuilder.UseCaching()</c>.
    /// Has no effect when caching is not registered.
    /// </summary>
    public bool UseCacheResult { get; init; } = true;
```

**Step 2: Build to verify**

Run: `dotnet build src/Rag.NET --nologo -v q`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs
git commit -m "feat: add UseCacheEmbedding and UseCacheResult to RetrievalOptions"
```

---

### Task 3: Add cache key helper

**Files:**
- Create: `src/Rag.NET/Caching/CacheKeyGenerator.cs`
- Create: `tests/Rag.NET.Tests/Caching/CacheKeyGeneratorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Caching/CacheKeyGeneratorTests.cs`:

```csharp
using Rag.NET.Caching;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Caching;

public class CacheKeyGeneratorTests
{
    [Fact]
    public void ForEmbedding_SameText_ReturnsSameKey()
    {
        var key1 = CacheKeyGenerator.ForEmbedding("hello world");
        var key2 = CacheKeyGenerator.ForEmbedding("hello world");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ForEmbedding_DifferentText_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForEmbedding("hello");
        var key2 = CacheKeyGenerator.ForEmbedding("world");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ForEmbedding_HasPrefix()
    {
        var key = CacheKeyGenerator.ForEmbedding("test");

        Assert.StartsWith("rag:embed:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void ForResult_SameOptions_ReturnsSameKey()
    {
        var opts = new RetrievalOptions { TopK = 10 };

        var key1 = CacheKeyGenerator.ForResult("query", opts);
        var key2 = CacheKeyGenerator.ForResult("query", opts);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ForResult_DifferentTopK_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("query", new RetrievalOptions { TopK = 5 });
        var key2 = CacheKeyGenerator.ForResult("query", new RetrievalOptions { TopK = 10 });

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ForResult_DifferentQuery_ReturnsDifferentKey()
    {
        var opts = new RetrievalOptions();

        var key1 = CacheKeyGenerator.ForResult("query1", opts);
        var key2 = CacheKeyGenerator.ForResult("query2", opts);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ForResult_HasPrefix()
    {
        var key = CacheKeyGenerator.ForResult("test", new RetrievalOptions());

        Assert.StartsWith("rag:result:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void ForResult_MetadataFilterAffectsKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions());
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions
        {
            MetadataFilter = new Dictionary<string, string> { ["dept"] = "eng" }
        });

        Assert.NotEqual(key1, key2);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "CacheKeyGenerator" 2>&1 | tail -5`
Expected: Build error — `CacheKeyGenerator` does not exist

**Step 3: Write the implementation**

Create `src/Rag.NET/Caching/CacheKeyGenerator.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Rag.NET.Models.Options;

namespace Rag.NET.Caching;

internal static class CacheKeyGenerator
{
    internal static string ForEmbedding(string textToEmbed)
    {
        return "rag:embed:" + Hash(textToEmbed);
    }

    internal static string ForResult(string query, RetrievalOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(query);
        sb.Append('|').Append(options.TopK);
        sb.Append('|').Append(options.MinScore);
        sb.Append('|').Append(options.UseHybridSearch);
        sb.Append('|').Append(options.UseRedundancyFilter);
        sb.Append('|').Append(options.RedundancyThreshold);
        sb.Append('|').Append(options.UseMultiQuery);
        sb.Append('|').Append(options.UseReranking);
        sb.Append('|').Append(options.CandidateCount);
        sb.Append('|').Append(options.UseHyde);
        sb.Append('|').Append(options.UseLostInTheMiddleReordering);

        if (options.MetadataFilter is { Count: > 0 })
        {
            foreach (var kvp in options.MetadataFilter.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(kvp.Key).Append('=').Append(kvp.Value);
            }
        }

        return "rag:result:" + Hash(sb.ToString());
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "CacheKeyGenerator"`
Expected: Passed! 8 tests

**Step 5: Commit**

```bash
git add src/Rag.NET/Caching/CacheKeyGenerator.cs tests/Rag.NET.Tests/Caching/CacheKeyGeneratorTests.cs
git commit -m "feat: add CacheKeyGenerator with SHA256 hashing"
```

---

### Task 4: Add logging methods for cache events

**Files:**
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs:35-36`

**Step 1: Add log methods**

In `src/Rag.NET/Logging/RagPipelineLog.cs`, add after the `HydeGenerationFailed` method (after line 35):

```csharp
    [LoggerMessage(Level = LogLevel.Debug, Message = "Embedding cache hit for query '{Query}'")]
    internal static partial void EmbeddingCacheHit(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Result cache hit for query '{Query}'")]
    internal static partial void ResultCacheHit(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding cache operation failed for query '{Query}'")]
    internal static partial void EmbeddingCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Result cache operation failed for query '{Query}'")]
    internal static partial void ResultCacheFailed(ILogger logger, string query, Exception exception);
```

**Step 2: Build to verify**

Run: `dotnet build src/Rag.NET --nologo -v q`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add cache hit/fail log messages"
```

---

### Task 5: Implement EmbeddingCacheRetriever

**Files:**
- Create: `src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/EmbeddingCacheRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/EmbeddingCacheRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class EmbeddingCacheRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly HybridCache _cache;
    private readonly CachingOptions _options = new();

    public EmbeddingCacheRetrieverTests()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        _cache = sp.GetRequiredService<HybridCache>();
    }

    [Fact]
    public async Task RetrieveAsync_CallsInnerOnFirstCall()
    {
        var expected = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "hit", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 }
        };
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Count, results.Count);
        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_SecondCallUsesCache()
    {
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);

        await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenOptedOut_SkipsCache()
    {
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        var opts = new RetrievalOptions { UseCacheEmbedding = false };

        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await _inner.Received(2).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_DifferentQueriesGetDifferentCacheEntries()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);

        await sut.RetrieveAsync("query1", cancellationToken: TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query2", cancellationToken: TestContext.Current.CancellationToken);

        await _inner.Received(2).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_UsesEmbeddingTextOverrideForCacheKey()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        var opts = new RetrievalOptions { EmbeddingTextOverride = "hypothetical doc" };

        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "EmbeddingCacheRetriever" 2>&1 | tail -5`
Expected: Build error — `EmbeddingCacheRetriever` does not exist

**Step 3: Write the implementation**

Create `src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs`:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that caches retrieval results keyed by the embedding text
/// (i.e., <see cref="RetrievalOptions.EmbeddingTextOverride"/> ?? query).
/// On cache hit, skips the inner retriever entirely (including embedding generation).
/// </summary>
public sealed class EmbeddingCacheRetriever(
    IRetriever inner,
    HybridCache cache,
    CachingOptions cachingOptions,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseCacheEmbedding)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var textToEmbed = opts.EmbeddingTextOverride ?? query;
        var cacheKey = CacheKeyGenerator.ForEmbedding(textToEmbed);

        try
        {
            var results = await cache.GetOrCreateAsync(
                cacheKey,
                async ct => await inner.RetrieveAsync(query, options, ct).ConfigureAwait(false),
                new HybridCacheEntryOptions { Expiration = cachingOptions.EmbeddingTtl },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.EmbeddingCacheFailed(_logger, query, ex);
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "EmbeddingCacheRetriever"`
Expected: Passed! 5 tests

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/EmbeddingCacheRetriever.cs tests/Rag.NET.Tests/Retrieval/EmbeddingCacheRetrieverTests.cs
git commit -m "feat: add EmbeddingCacheRetriever decorator"
```

---

### Task 6: Implement ResultCacheRetriever

**Files:**
- Create: `src/Rag.NET/Retrieval/ResultCacheRetriever.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/ResultCacheRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/ResultCacheRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class ResultCacheRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly HybridCache _cache;
    private readonly CachingOptions _options = new();

    public ResultCacheRetrieverTests()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        _cache = sp.GetRequiredService<HybridCache>();
    }

    [Fact]
    public async Task RetrieveAsync_CallsInnerOnFirstCall()
    {
        var expected = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "hit", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 }
        };
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new ResultCacheRetriever(_inner, _cache, _options);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Count, results.Count);
        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_SecondCallUsesCache()
    {
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new ResultCacheRetriever(_inner, _cache, _options);

        await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenOptedOut_SkipsCache()
    {
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new ResultCacheRetriever(_inner, _cache, _options);
        var opts = new RetrievalOptions { UseCacheResult = false };

        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await _inner.Received(2).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_DifferentOptionsGetDifferentCacheEntries()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new ResultCacheRetriever(_inner, _cache, _options);

        await sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 }, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", new RetrievalOptions { TopK = 10 }, TestContext.Current.CancellationToken);

        await _inner.Received(2).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_SameOptionsUsesCache()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new ResultCacheRetriever(_inner, _cache, _options);
        var opts = new RetrievalOptions { TopK = 10, UseHybridSearch = true };

        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "ResultCacheRetriever" 2>&1 | tail -5`
Expected: Build error — `ResultCacheRetriever` does not exist

**Step 3: Write the implementation**

Create `src/Rag.NET/Retrieval/ResultCacheRetriever.cs`:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Caching;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Outermost decorator that caches the complete retrieval result (after all
/// post-processing: reranking, redundancy filter, reordering). On cache hit,
/// the entire inner retrieval chain is skipped.
/// </summary>
public sealed class ResultCacheRetriever(
    IRetriever inner,
    HybridCache cache,
    CachingOptions cachingOptions,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseCacheResult)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var cacheKey = CacheKeyGenerator.ForResult(query, opts);

        try
        {
            var results = await cache.GetOrCreateAsync(
                cacheKey,
                async ct => await inner.RetrieveAsync(query, options, ct).ConfigureAwait(false),
                new HybridCacheEntryOptions { Expiration = cachingOptions.ResultTtl },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ResultCacheFailed(_logger, query, ex);
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "ResultCacheRetriever"`
Expected: Passed! 5 tests

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/ResultCacheRetriever.cs tests/Rag.NET.Tests/Retrieval/ResultCacheRetrieverTests.cs
git commit -m "feat: add ResultCacheRetriever decorator"
```

---

### Task 7: Wire into DI and RagBuilder

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs:107`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs:47-87`

**Step 1: Add UseCaching to RagBuilder**

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`, add after the `UseHyde` method (after line 107):

```csharp
    /// <summary>
    /// Enables two-level retrieval caching backed by <see cref="HybridCache"/>.
    /// Embedding cache (L1) caches query→embedding mappings. Result cache (L2)
    /// caches the complete post-processed result list.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseCacheEmbedding = false, UseCacheResult = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="CachingOptions"/>.</param>
    public RagBuilder UseCaching(Action<CachingOptions>? configure = null)
    {
        var options = new CachingOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddHybridCache();
        return this;
    }
```

Add the required `using` at the top of the file:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Rag.NET.Models.Options;
```

Note: `RagBuilder.cs` already imports `Rag.NET.Models.Options` (line 8). Only add `Microsoft.Extensions.Caching.Hybrid`.

**Step 2: Wire decorators into BuildRetrieverChain**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`, modify the `BuildRetrieverChain` method.

After the `VectorStoreRetriever` creation (line 53), add the embedding cache decorator:

```csharp
        var cachingOptions = sp.GetService<CachingOptions>();
        var hybridCache = sp.GetService<HybridCache>();

        if (cachingOptions is not null && hybridCache is not null)
        {
            chain = new EmbeddingCacheRetriever(
                chain,
                hybridCache,
                cachingOptions,
                sp.GetService<ILogger<EmbeddingCacheRetriever>>());
        }
```

After `chain = new LostInTheMiddleRetriever(chain);` (line 84), add the result cache decorator:

```csharp
        if (cachingOptions is not null && hybridCache is not null)
        {
            chain = new ResultCacheRetriever(
                chain,
                hybridCache,
                cachingOptions,
                sp.GetService<ILogger<ResultCacheRetriever>>());
        }
```

Add required `using` at top:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
```

**Step 3: Build to verify**

Run: `dotnet build src/Rag.NET --nologo -v q`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "feat: wire caching decorators into DI and RagBuilder"
```

---

### Task 8: DI integration tests

**Files:**
- Modify: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Add the test package reference**

In `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`, add:

```xml
<PackageReference Include="Microsoft.Extensions.Caching.Hybrid" Version="9.*" />
```

**Step 2: Write the DI integration test**

Add to `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`:

```csharp
    [Fact]
    public async Task AddRagNet_WithCaching_CachesSecondCall()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet(b => b.UseCaching());

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Second call should be cached — embedder called only once
        await embedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }
```

**Step 3: Run the test**

Run: `dotnet test tests/Rag.NET.Tests --nologo --filter "AddRagNet_WithCaching"`
Expected: Passed! 1 test

**Step 4: Commit**

```bash
git add tests/Rag.NET.Tests/Rag.NET.Tests.csproj tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "test: add DI integration test for caching decorators"
```

---

### Task 9: Run full test suite

**Step 1: Run all tests**

Run: `dotnet test tests/Rag.NET.Tests --nologo`
Expected: All tests pass (170+ tests)

**Step 2: Fix any failures**

If any existing tests fail due to the new `UseCacheEmbedding`/`UseCacheResult` properties on `RetrievalOptions`, they should be unaffected since the defaults are `true` and no `CachingOptions`/`HybridCache` is registered in those tests (so the decorators are not in the chain).

---

### Task 10: Update documentation

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/retrieval.md`
- Modify: `docs/observability.md`
- Modify: `docs/features.md`

**Step 1: Update architecture.md**

Update the decorator chain diagram to include the two cache decorators:

```
ResultCacheRetriever               (present when UseCaching() called)
  → LostInTheMiddleRetriever       (always present)
    → RedundancyFilterRetriever    (always present)
      → RerankingRetriever         (present when IReranker registered)
        → MultiQueryRetriever      (present when IQueryExpander registered)
          → HydeRetriever          (present when IHypotheticalDocumentGenerator registered)
            → EmbeddingCacheRetriever  (present when UseCaching() called)
              → VectorStoreRetriever   (base — always present)
```

**Step 2: Update retrieval.md**

Add a "Search Result Caching" section describing:
- Two cache levels (embedding and result)
- How to enable via `UseCaching()`
- Configuration options (TTLs)
- Per-call opt-out (`UseCacheEmbedding = false`, `UseCacheResult = false`)
- Cache invalidation (TTL-based)

Add `UseCacheEmbedding` and `UseCacheResult` to the `RetrievalOptions` listing.

**Step 3: Update observability.md**

Add the four new log messages to the log messages table:
- `EmbeddingCacheHit` (Debug)
- `ResultCacheHit` (Debug)
- `EmbeddingCacheFailed` (Warning)
- `ResultCacheFailed` (Warning)

**Step 4: Update features.md**

Mark "Search Result Caching" as `[x]` in the priority table.

**Step 5: Commit**

```bash
git add docs/architecture.md docs/retrieval.md docs/observability.md docs/features.md
git commit -m "docs: add search result caching documentation"
```

---

### Task 11: Add benchmarks

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/CachingBenchmarks.cs`
- Modify: `docs/benchmarks.md`

**Step 1: Create the benchmark**

Create `benchmarks/Rag.NET.Benchmarks/CachingBenchmarks.cs` following the pattern from `MultiQueryBenchmarks.cs`:
- Build a pipeline with caching enabled
- Benchmark: `CacheHit` — second call (should be near-zero)
- Benchmark: `CacheMiss_Baseline` — first call with `UseCacheResult = false`
- Use `NoOpVectorStore` and `FakeEmbeddingGenerator` to isolate caching overhead

**Step 2: Run the benchmark**

Run: `dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*Caching*"`

**Step 3: Update benchmarks.md**

Add a "Search Result Caching" section with the actual numbers from the benchmark run.

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/CachingBenchmarks.cs docs/benchmarks.md
git commit -m "perf: add caching benchmarks"
```
