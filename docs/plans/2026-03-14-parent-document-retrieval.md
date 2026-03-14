# Parent-Document Retrieval Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Index small chunks for precise embedding matching but return their larger parent chunks to the LLM for answer generation.

**Architecture:** Ingestion runs two chunking passes — one for parent chunks (stored in an in-memory store) and one for child chunks (embedded and stored in the vector store). A retrieval decorator replaces child text with parent text after scoring. Follows the established decorator pattern with per-call opt-out.

**Tech Stack:** .NET 10, xUnit, NSubstitute, BenchmarkDotNet. No new NuGet packages.

---

### Task 1: Add ParentDocumentOptions

**Files:**
- Create: `src/Rag.NET/Models/Options/ParentDocumentOptions.cs`

**Step 1: Create ParentDocumentOptions**

```csharp
namespace Rag.NET.Models.Options;

public class ParentDocumentOptions
{
    public int ParentChunkSize { get; set; } = 2048;
    public int ParentOverlap { get; set; } = 100;
}
```

**Step 2: Run build**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Rag.NET/Models/Options/ParentDocumentOptions.cs
git commit -m "feat: add ParentDocumentOptions"
```

---

### Task 2: Add UseParentDocument to RetrievalOptions

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`

**Step 1: Add UseParentDocument property**

Add after `UseCacheResult` (line 54):

```csharp
/// <summary>
/// Set to <see langword="false"/> to skip parent-document text replacement for this call,
/// even when parent-document retrieval is registered via <c>RagBuilder.UseParentDocumentRetrieval()</c>.
/// Has no effect when parent-document retrieval is not registered.
/// </summary>
public bool UseParentDocument { get; init; } = true;
```

**Step 2: Add UseParentDocument to CacheKeyGenerator.ForResult**

Modify `src/Rag.NET/Caching/CacheKeyGenerator.cs`. Add after the `UseLostInTheMiddleReordering` line (line 27):

```csharp
sb.Append('|').Append(options.UseParentDocument);
```

**Step 3: Add cache key test**

Add to `tests/Rag.NET.Tests/Caching/CacheKeyGeneratorTests.cs`:

```csharp
[Fact]
public void ForResult_DifferentUseParentDocument_ReturnsDifferentKey()
{
    var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseParentDocument = false });
    var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseParentDocument = true });
    Assert.NotEqual(key1, key2);
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "CacheKeyGenerator"`
Expected: All pass

**Step 5: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs src/Rag.NET/Caching/CacheKeyGenerator.cs tests/Rag.NET.Tests/Caching/CacheKeyGeneratorTests.cs
git commit -m "feat: add UseParentDocument to RetrievalOptions and cache key"
```

---

### Task 3: Implement InMemoryParentChunkStore

**Files:**
- Create: `src/Rag.NET/Storage/InMemoryParentChunkStore.cs`
- Create: `tests/Rag.NET.Tests/Storage/InMemoryParentChunkStoreTests.cs`

**Step 1: Write failing tests**

```csharp
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class InMemoryParentChunkStoreTests
{
    private readonly InMemoryParentChunkStore _store = new();

    [Fact]
    public void Add_And_TryGet_ReturnsStoredText()
    {
        _store.Add("doc1", 0, "parent text");
        var found = _store.TryGet("doc1", 0, out var text);
        Assert.True(found);
        Assert.Equal("parent text", text);
    }

    [Fact]
    public void TryGet_NotFound_ReturnsFalse()
    {
        var found = _store.TryGet("missing", 0, out var text);
        Assert.False(found);
        Assert.Null(text);
    }

    [Fact]
    public void Remove_DeletesByDocumentId()
    {
        _store.Add("doc1", 0, "chunk 0");
        _store.Add("doc1", 1, "chunk 1");
        _store.Add("doc2", 0, "other doc");

        _store.Remove("doc1");

        Assert.False(_store.TryGet("doc1", 0, out _));
        Assert.False(_store.TryGet("doc1", 1, out _));
        Assert.True(_store.TryGet("doc2", 0, out _));
    }

    [Fact]
    public void GetParentKey_FormatsCorrectly()
    {
        var key = InMemoryParentChunkStore.GetParentKey("doc-123", 7);
        Assert.Equal("doc-123:7", key);
    }

    [Fact]
    public void FindParentIndex_ReturnsCorrectParent()
    {
        // Parents at positions 0-99, 100-199
        var parentBoundaries = new List<(int start, int end)> { (0, 99), (100, 199) };
        var idx = InMemoryParentChunkStore.FindParentIndex(parentBoundaries, childStart: 50);
        Assert.Equal(0, idx);

        idx = InMemoryParentChunkStore.FindParentIndex(parentBoundaries, childStart: 150);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void FindParentIndex_ChildOutsideAllBoundaries_ReturnsLastParent()
    {
        var parentBoundaries = new List<(int start, int end)> { (0, 99) };
        var idx = InMemoryParentChunkStore.FindParentIndex(parentBoundaries, childStart: 200);
        Assert.Equal(0, idx);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "InMemoryParentChunkStore"`
Expected: FAIL — class not found

**Step 3: Implement InMemoryParentChunkStore**

```csharp
using System.Collections.Concurrent;

namespace Rag.NET.Storage;

/// <summary>
/// Thread-safe in-memory store for parent chunk text.
/// Process-scoped, not persisted — rebuilt on re-ingestion (same trade-off as <see cref="Search.InMemoryBm25Index"/>).
/// </summary>
public sealed class InMemoryParentChunkStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public void Add(string documentId, int parentChunkIndex, string text)
    {
        var key = GetParentKey(documentId, parentChunkIndex);
        _store[key] = text;
    }

    public bool TryGet(string documentId, int parentChunkIndex, out string? text)
    {
        var key = GetParentKey(documentId, parentChunkIndex);
        if (_store.TryGetValue(key, out var value))
        {
            text = value;
            return true;
        }

        text = null;
        return false;
    }

    public void Remove(string documentId)
    {
        var prefix = documentId + ":";
        foreach (var key in _store.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _store.TryRemove(key, out _);
        }
    }

    public static string GetParentKey(string documentId, int parentChunkIndex)
        => $"{documentId}:{parentChunkIndex}";

    /// <summary>
    /// Finds which parent chunk contains a child chunk based on start position.
    /// </summary>
    public static int FindParentIndex(List<(int start, int end)> parentBoundaries, int childStart)
    {
        for (int i = 0; i < parentBoundaries.Count; i++)
        {
            if (childStart >= parentBoundaries[i].start && childStart <= parentBoundaries[i].end)
                return i;
        }

        // Fallback: assign to last parent
        return parentBoundaries.Count - 1;
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "InMemoryParentChunkStore"`
Expected: All pass

**Step 5: Commit**

```bash
git add src/Rag.NET/Storage/InMemoryParentChunkStore.cs tests/Rag.NET.Tests/Storage/InMemoryParentChunkStoreTests.cs
git commit -m "feat: add InMemoryParentChunkStore"
```

---

### Task 4: Add logging methods for parent-document events

**Files:**
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`

**Step 1: Add log methods**

Add at the end of the class (before closing brace), after the `ResultCacheFailed` method:

```csharp
[LoggerMessage(Level = LogLevel.Debug, Message = "Parent document retrieved for query '{Query}': {ChildCount} children -> {ParentCount} parents")]
internal static partial void ParentDocumentRetrieved(ILogger logger, string query, int childCount, int parentCount);

[LoggerMessage(Level = LogLevel.Warning, Message = "Parent document lookup failed for query '{Query}', returning child chunks")]
internal static partial void ParentDocumentFailed(ILogger logger, string query, Exception exception);
```

**Step 2: Run build**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Rag.NET/Logging/RagPipelineLog.cs
git commit -m "feat: add parent-document log messages"
```

---

### Task 5: Implement ParentDocumentRetriever

**Files:**
- Create: `src/Rag.NET/Retrieval/ParentDocumentRetriever.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/ParentDocumentRetrieverTests.cs`

**Step 1: Write failing tests**

```csharp
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class ParentDocumentRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly InMemoryParentChunkStore _parentStore = new();

    private ParentDocumentRetriever CreateSut() => new(_inner, _parentStore);

    [Fact]
    public async Task RetrieveAsync_ReplacesChildTextWithParentText()
    {
        _parentStore.Add("doc1", 0, "full parent text that is much larger");

        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "small child",
                    DocumentId = "doc1",
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["_parentKey"] = "doc1:0"
                    }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("full parent text that is much larger", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_DeduplicatesChildrenSharingParent()
    {
        _parentStore.Add("doc1", 0, "parent text");

        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child A", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child B", DocumentId = "doc1", ChunkIndex = 1,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.7
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("parent text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_UsesMaxChildScore()
    {
        _parentStore.Add("doc1", 0, "parent text");

        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child A", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.7
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child B", DocumentId = "doc1", ChunkIndex = 1,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.9, results[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_WhenOptedOut_ReturnsChildChunks()
    {
        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child text", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var opts = new RetrievalOptions { UseParentDocument = false };
        var results = await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        Assert.Equal("child text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WhenParentNotFound_ReturnsChildChunk()
    {
        // No parent stored — should fall back to child
        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child text", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:99" }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("child text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WhenNoParentKey_ReturnsChildChunk()
    {
        // Child has no _parentKey metadata — should pass through unchanged
        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child text", DocumentId = "doc1", ChunkIndex = 0,
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("child text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_OverFetchesToCompensateForDeduplication()
    {
        _parentStore.Add("doc1", 0, "parent text");

        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = CreateSut();
        var opts = new RetrievalOptions { TopK = 5 };
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        // Verify inner was called with higher TopK to compensate for deduplication
        await _inner.Received(1).RetrieveAsync(
            "query",
            Arg.Is<RetrievalOptions>(o => o.TopK > 5 && !o.UseParentDocument),
            Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "ParentDocumentRetriever"`
Expected: FAIL — class not found

**Step 3: Implement ParentDocumentRetriever**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that replaces child chunk text with parent chunk text after retrieval.
/// Multiple children sharing the same parent are deduplicated; the parent gets the
/// highest child score.
/// </summary>
public sealed class ParentDocumentRetriever(
    IRetriever inner,
    InMemoryParentChunkStore parentStore,
    ILogger? logger = null) : IRetriever
{
    private const string ParentKeyMetadata = "_parentKey";
    private const int OverFetchMultiplier = 3;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseParentDocument)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        // Over-fetch to compensate for deduplication (multiple children → one parent)
        var expanded = opts with { TopK = opts.TopK * OverFetchMultiplier, UseParentDocument = false };
        var childResults = await inner.RetrieveAsync(query, expanded, cancellationToken).ConfigureAwait(false);

        try
        {
            return ReplaceWithParents(childResults, query, opts.TopK);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ParentDocumentFailed(_logger, query, ex);
            return childResults;
        }
    }

    private List<SearchResult> ReplaceWithParents(
        IReadOnlyList<SearchResult> childResults,
        string query,
        int topK)
    {
        // Group by parent key, taking max score per parent
        var parentGroups = new Dictionary<string, (SearchResult bestChild, double maxScore)>(StringComparer.Ordinal);
        var noParentResults = new List<SearchResult>();

        foreach (var result in childResults)
        {
            if (!result.Chunk.Metadata.TryGetValue(ParentKeyMetadata, out var parentKey))
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
                && int.TryParse(parts[1], out var parentChunkIndex)
                && parentStore.TryGet(parts[0], parentChunkIndex, out var parentText))
            {
                results.Add(new SearchResult
                {
                    Chunk = bestChild.Chunk with { Text = parentText },
                    Score = maxScore
                });
            }
            else
            {
                // Parent not found — return child as-is
                results.Add(bestChild);
            }
        }

        results.AddRange(noParentResults);
        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        RagPipelineLog.ParentDocumentRetrieved(_logger, query, childResults.Count, results.Count);
        return results;
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "ParentDocumentRetriever"`
Expected: All pass

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/ParentDocumentRetriever.cs tests/Rag.NET.Tests/Retrieval/ParentDocumentRetrieverTests.cs
git commit -m "feat: add ParentDocumentRetriever decorator"
```

---

### Task 6: Modify DocumentIngestor for dual chunking

**Files:**
- Modify: `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/DocumentIngestorParentChunkTests.cs`

**Step 1: Write failing tests**

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class DocumentIngestorParentChunkTests
{
    [Fact]
    public async Task IngestAsync_WithParentOptions_StoresParentChunks()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in texts)
                    result.Add(new Embedding<float>(new float[] { 0.1f }));
                return result;
            });

        var parentStore = new InMemoryParentChunkStore();
        var parentOptions = new ParentDocumentOptions { ParentChunkSize = 100, ParentOverlap = 0 };

        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 },
            new InMemoryBm25Index(),
            parentStore,
            parentOptions);

        var text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"word{i}"));
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc1",
            FileName = "test.txt",
            ContentType = "text/plain"
        };

        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        // Parent chunks should be stored
        Assert.True(parentStore.TryGet("doc1", 0, out var parentText));
        Assert.NotNull(parentText);
        Assert.True(parentText.Length > 0);
    }

    [Fact]
    public async Task IngestAsync_WithParentOptions_ChildChunksHaveParentKey()
    {
        var storedChunks = new List<EmbeddedChunk>();
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedChunks.AddRange(ci.Arg<IReadOnlyList<EmbeddedChunk>>());
                return Task.CompletedTask;
            });

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in texts)
                    result.Add(new Embedding<float>(new float[] { 0.1f }));
                return result;
            });

        var parentStore = new InMemoryParentChunkStore();
        var parentOptions = new ParentDocumentOptions { ParentChunkSize = 200, ParentOverlap = 0 };

        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 },
            new InMemoryBm25Index(),
            parentStore,
            parentOptions);

        var text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"word{i}"));
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc1",
            FileName = "test.txt",
            ContentType = "text/plain"
        };

        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(storedChunks);
        // Every child chunk should have _parentKey metadata
        Assert.All(storedChunks, ec =>
            Assert.True(ec.Chunk.Metadata.ContainsKey("_parentKey"),
                $"Chunk {ec.Chunk.ChunkIndex} missing _parentKey"));
    }

    [Fact]
    public async Task IngestAsync_WithoutParentOptions_NoParentKeyMetadata()
    {
        var storedChunks = new List<EmbeddedChunk>();
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedChunks.AddRange(ci.Arg<IReadOnlyList<EmbeddedChunk>>());
                return Task.CompletedTask;
            });

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in texts)
                    result.Add(new Embedding<float>(new float[] { 0.1f }));
                return result;
            });

        // No parentStore, no parentOptions
        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 },
            new InMemoryBm25Index());

        var text = "Hello world this is a test document with some words.";
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc1",
            FileName = "test.txt",
            ContentType = "text/plain"
        };

        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        // No child should have _parentKey
        Assert.All(storedChunks, ec =>
            Assert.False(ec.Chunk.Metadata.ContainsKey("_parentKey")));
    }

    [Fact]
    public async Task DeleteAsync_WithParentOptions_RemovesFromParentStore()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var parentStore = new InMemoryParentChunkStore();
        parentStore.Add("doc1", 0, "parent text");

        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions(),
            new InMemoryBm25Index(),
            parentStore,
            new ParentDocumentOptions());

        await sut.DeleteAsync("doc1", TestContext.Current.CancellationToken);

        Assert.False(parentStore.TryGet("doc1", 0, out _));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests --filter "DocumentIngestorParentChunk"`
Expected: FAIL — constructor signature mismatch

**Step 3: Modify DocumentIngestor**

Modify `src/Rag.NET/Ingestion/DocumentIngestor.cs`:

1. Add optional constructor parameters `InMemoryParentChunkStore? parentStore = null` and `ParentDocumentOptions? parentOptions = null`
2. After `ParseAndChunkAsync`, if `parentOptions` is not null, run a second chunking pass with `ParentChunkSize` and store results in `parentStore`, then assign `_parentKey` metadata to each child chunk
3. In `DeleteAsync`, also call `parentStore?.Remove(documentId)`

Key changes to the constructor (remove `[Singleton]` attribute since DI wiring is done manually in `ServiceCollectionExtensions`):

```csharp
public sealed class DocumentIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ChunkingOptions chunkingOptions,
    InMemoryBm25Index bm25Index,
    InMemoryParentChunkStore? parentStore = null,
    ParentDocumentOptions? parentOptions = null) : IIngestor
```

Add parent chunking logic after `ParseAndChunkAsync` returns (after line 42), before `ApplyMetadataTags`:

```csharp
if (parentOptions is not null && parentStore is not null)
{
    await ChunkAndStoreParentsAsync(parser, document, metadata, chunks, cancellationToken).ConfigureAwait(false);
}
```

Add the new method:

```csharp
private async Task ChunkAndStoreParentsAsync(
    IDocumentParser parser,
    Stream document,
    DocumentMetadata metadata,
    List<TextChunk> childChunks,
    CancellationToken cancellationToken)
{
    // Reset stream for second parse pass
    document.Position = 0;

    var parentChunkingOptions = new ChunkingOptions
    {
        MaxChunkSize = parentOptions!.ParentChunkSize,
        Overlap = parentOptions.ParentOverlap
    };

    var parentBoundaries = new List<(int start, int end, int index)>();
    var parentIndex = 0;

    await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
    {
        await foreach (var parentChunk in chunkingStrategy.ChunkAsync(section, parentChunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            parentStore!.Add(metadata.DocumentId, parentIndex, parentChunk.Text);
            parentBoundaries.Add((parentChunk.StartPosition, parentChunk.EndPosition, parentIndex));
            parentIndex++;
        }
    }

    // Assign _parentKey to each child chunk
    foreach (var child in childChunks)
    {
        var pIdx = InMemoryParentChunkStore.FindParentIndex(
            parentBoundaries.Select(b => (b.start, b.end)).ToList(),
            child.StartPosition);
        child.Metadata["_parentKey"] = InMemoryParentChunkStore.GetParentKey(metadata.DocumentId, pIdx);
    }
}
```

Update `DeleteAsync` to also remove from parent store:

```csharp
public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
{
    await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
    bm25Index.Remove(documentId);
    parentStore?.Remove(documentId);
}
```

**Important:** The `[Singleton(As = typeof(IIngestor))]` ZInject attribute must be removed since `DocumentIngestor` needs optional parameters that ZInject can't resolve. The DI registration is already handled manually in `ServiceCollectionExtensions`. Check that `AddRagNETServices()` doesn't conflict — if it does, register `IIngestor` manually instead.

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "DocumentIngestor"`
Expected: All pass

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/DocumentIngestor.cs tests/Rag.NET.Tests/Ingestion/DocumentIngestorParentChunkTests.cs
git commit -m "feat: add dual chunking pass for parent-document ingestion"
```

---

### Task 7: Wire into DI and RagBuilder

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Add UseParentDocumentRetrieval to RagBuilder**

Add after the `UseCaching` method in `src/Rag.NET/DependencyInjection/RagBuilder.cs`:

```csharp
/// <summary>
/// Enables parent-document retrieval. At ingestion, documents are chunked twice:
/// small child chunks are embedded for precise matching, large parent chunks are
/// stored in-memory for context-rich answer generation. At retrieval, child matches
/// are replaced with their parent text.
/// </summary>
/// <remarks>
/// Per-call opt-out: pass <c>new RetrievalOptions { UseParentDocument = false }</c>.
/// </remarks>
/// <param name="configure">Optional delegate to configure <see cref="ParentDocumentOptions"/>.</param>
public RagBuilder UseParentDocumentRetrieval(Action<ParentDocumentOptions>? configure = null)
{
    var options = new ParentDocumentOptions();
    configure?.Invoke(options);
    Services.AddSingleton(options);
    Services.AddSingleton<InMemoryParentChunkStore>();
    return this;
}
```

Add `using Rag.NET.Storage;` at the top.

**Step 2: Add ParentDocumentRetriever to BuildRetrieverChain**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`, add after the `RerankingRetriever` block (after line 94) and before `RedundancyFilterRetriever` (line 96):

```csharp
var parentDocOptions = sp.GetService<ParentDocumentOptions>();
var parentStore = sp.GetService<InMemoryParentChunkStore>();
if (parentDocOptions is not null && parentStore is not null)
{
    chain = new ParentDocumentRetriever(
        chain,
        parentStore,
        sp.GetService<ILogger<ParentDocumentRetriever>>());
}
```

Add `using Rag.NET.Storage;` at the top if not already present.

**Step 3: Run all tests**

Run: `dotnet test tests/Rag.NET.Tests`
Expected: All pass

**Step 4: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "feat: wire parent-document retrieval into DI via RagBuilder"
```

---

### Task 8: DI integration tests

**Files:**
- Modify: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Add integration tests**

```csharp
[Fact]
public async Task AddRagNet_WithParentDocumentRetrieval_ReplacesChildWithParentText()
{
    var services = new ServiceCollection();
    var vectorStore = Substitute.For<IVectorStore>();
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    services.AddSingleton(vectorStore);
    services.AddSingleton(embedder);
    embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
    vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "small child", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            }
        });

    services.AddRagNet(b => b.UseParentDocumentRetrieval());

    var sp = services.BuildServiceProvider();

    // Manually add parent text to the store
    var parentStore = sp.GetRequiredService<Rag.NET.Storage.InMemoryParentChunkStore>();
    parentStore.Add("doc1", 0, "large parent context text");

    var pipeline = sp.GetRequiredService<IRagPipeline>();
    var results = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Single(results);
    Assert.Equal("large parent context text", results[0].Chunk.Text);
}

[Fact]
public async Task AddRagNet_WithoutParentDocumentRetrieval_ReturnsChildText()
{
    var services = new ServiceCollection();
    var vectorStore = Substitute.For<IVectorStore>();
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    services.AddSingleton(vectorStore);
    services.AddSingleton(embedder);
    embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
    vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "small child", DocumentId = "doc1", ChunkIndex = 0,
                },
                Score = 0.9
            }
        });

    services.AddRagNet(); // no UseParentDocumentRetrieval

    var sp = services.BuildServiceProvider();
    var pipeline = sp.GetRequiredService<IRagPipeline>();
    var results = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Single(results);
    Assert.Equal("small child", results[0].Chunk.Text);
}
```

**Step 2: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "ServiceCollectionExtensions"`
Expected: All pass

**Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "test: add DI integration tests for parent-document retrieval"
```

---

### Task 9: Run full test suite

Run: `dotnet test tests/Rag.NET.Tests`
Expected: All tests pass

---

### Task 10: Update documentation

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/retrieval.md`
- Modify: `docs/observability.md`
- Modify: `docs/features.md`

**Step 1: Update architecture.md**

Update the decorator chain listing to include `ParentDocumentRetriever` between `RerankingRetriever` and `RedundancyFilterRetriever`:

```
ResultCacheRetriever              (present when UseCaching() called)
  → LostInTheMiddleRetriever      (always present)
    → RedundancyFilterRetriever   (always present)
      → ParentDocumentRetriever   (present when UseParentDocumentRetrieval() called)
        → RerankingRetriever      (present when IReranker registered)
          → MultiQueryRetriever   (present when IQueryExpander registered)
            → HydeRetriever       (present when IHypotheticalDocumentGenerator registered)
              → EmbeddingCacheRetriever  (present when UseCaching() called)
                → VectorStoreRetriever   (base — always present)
```

Also add `InMemoryParentChunkStore` to the retrieval path diagram and mention it alongside `InMemoryBm25Index` in the "In-memory BM25 index" section.

**Step 2: Update retrieval.md**

Add a new "Parent-Document Retrieval" section covering:
- What it does and why (small chunks for matching, large parents for context)
- Builder registration: `UseParentDocumentRetrieval()`
- Configuration: `ParentChunkSize`, `ParentOverlap`
- Per-call opt-out: `UseParentDocument = false`
- How it works at ingestion and retrieval
- In-memory store trade-off (same as BM25)

**Step 3: Update observability.md**

Add the two new log messages to the table:

| `ParentDocumentRetriever` | `Debug` | `ParentDocumentRetrieved` | `Parent document retrieved for query '{Query}': {ChildCount} children → {ParentCount} parents` |
| `ParentDocumentRetriever` | `Warning` | `ParentDocumentFailed` | `Parent document lookup failed for query '{Query}', returning child chunks` |

**Step 4: Update features.md**

Mark Parent-Document Retrieval as done: change `[ ]` to `[x]` for `Parent-Document Retrieval`.

**Step 5: Commit**

```bash
git add docs/architecture.md docs/retrieval.md docs/observability.md docs/features.md
git commit -m "docs: add parent-document retrieval documentation"
```

---

### Task 11: Add benchmarks

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/ParentDocumentBenchmarks.cs`
- Modify: `docs/benchmarks.md`

**Step 1: Create benchmark**

Create `benchmarks/Rag.NET.Benchmarks/ParentDocumentBenchmarks.cs` following the same pattern as `CachingBenchmarks.cs`:

- `NoParentDocument_Baseline` — retrieval without parent-document decorator
- `WithParentDocument` — retrieval with parent-document decorator (parent lookup + text replacement)
- Both use mocked vector store and embedder (zero I/O)
- Pre-ingest a 50 KB document, pre-populate parent store
- Use `Host.CreateApplicationBuilder` pattern if DI is needed

**Step 2: Run benchmark**

Run: `dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*ParentDocument*"`

**Step 3: Update docs/benchmarks.md**

Add a "Parent-Document Retrieval" section with the real numbers.

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/ParentDocumentBenchmarks.cs docs/benchmarks.md
git commit -m "perf: add parent-document retrieval benchmarks"
```
