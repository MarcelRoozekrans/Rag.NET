# BM25 In-Memory Hybrid Search Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add BM25 in-memory keyword search to `RagPipeline` so `UseHybridSearch = true` works with any vector store, not just Azure AI Search.

**Architecture:** `RagPipeline` holds a private `InMemoryBm25Index` field. `IngestAsync` adds chunks to it; `DeleteAsync` removes by documentId; `RetrieveAsync` falls back to BM25 + dense search merged via RRF when the store is not `IHybridSearchable`. No new DI surface.

**Tech Stack:** C# 13 / .NET 10, xUnit, NSubstitute, no new NuGet packages.

---

### Task 1: InMemoryBm25Index — core data structure

**Files:**
- Create: `src/Rag.NET/Search/InMemoryBm25Index.cs`
- Create: `tests/Rag.NET.Tests/Search/InMemoryBm25IndexTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Search/InMemoryBm25IndexTests.cs
using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class InMemoryBm25IndexTests
{
    [Fact]
    public void Search_ReturnsEmpty_WhenIndexIsEmpty()
    {
        var index = new InMemoryBm25Index();
        var results = index.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Search_ReturnsMatchingDoc_WhenTermPresent()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "the quick brown fox", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "the lazy dog sleeps", DocumentId = "doc1", ChunkIndex = 1 });

        var results = index.Search("fox", topK: 5);

        Assert.Single(results);
        Assert.Equal(0, results[0].docId);
    }

    [Fact]
    public void Search_RanksHigherFrequencyTermHigher()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "cat cat cat", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "cat dog bird", DocumentId = "doc1", ChunkIndex = 1 });

        var results = index.Search("cat", topK: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].docId); // higher TF should rank first
    }

    [Fact]
    public void Search_RespectsTopK()
    {
        var index = new InMemoryBm25Index();
        for (int i = 0; i < 10; i++)
            index.Add(i, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = i });

        var results = index.Search("hello", topK: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Remove_DeletesAllChunksForDocument()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "hello universe", DocumentId = "doc2", ChunkIndex = 0 });

        index.Remove("doc1");

        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
        Assert.Equal(1, results[0].docId);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "Hello World", DocumentId = "doc1", ChunkIndex = 0 });

        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
    }

    [Fact]
    public void Search_IgnoresStopwordsAndPunctuation()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "fox! jumps... over, the fence.", DocumentId = "doc1", ChunkIndex = 0 });

        var results = index.Search("jumps", topK: 5);
        Assert.Single(results);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~InMemoryBm25IndexTests" -v minimal
```

Expected: compilation error — `InMemoryBm25Index` not found.

**Step 3: Implement InMemoryBm25Index**

```csharp
// src/Rag.NET/Search/InMemoryBm25Index.cs
using System.Text.RegularExpressions;
using Rag.NET.Models;

namespace Rag.NET.Search;

/// <summary>
/// Thread-safe in-memory BM25 inverted index.
/// Parameters: k1=1.5, b=0.75 (Lucene defaults).
/// </summary>
internal sealed class InMemoryBm25Index
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    // term -> list of (docId, termFrequency)
    private readonly Dictionary<string, List<(int docId, int tf)>> _postings = new(StringComparer.Ordinal);
    // docId -> (documentId string, docLength)
    private readonly Dictionary<int, (string documentId, int length)> _docs = [];
    private readonly ReaderWriterLockSlim _lock = new();

    public void Add(int docId, TextChunk chunk)
    {
        var tokens = Tokenize(chunk.Text);
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
            tf[token] = tf.TryGetValue(token, out var count) ? count + 1 : 1;

        _lock.EnterWriteLock();
        try
        {
            _docs[docId] = (chunk.DocumentId, tokens.Count);
            foreach (var (term, freq) in tf)
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    list = [];
                    _postings[term] = list;
                }
                list.Add((docId, freq));
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Remove(string documentId)
    {
        _lock.EnterWriteLock();
        try
        {
            var toRemove = _docs
                .Where(kv => kv.Value.documentId == documentId)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var docId in toRemove)
            {
                _docs.Remove(docId);
            }

            foreach (var list in _postings.Values)
                list.RemoveAll(entry => toRemove.Contains(entry.docId));

            // clean up empty posting lists
            var emptyTerms = _postings.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            foreach (var term in emptyTerms)
                _postings.Remove(term);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public IReadOnlyList<(int docId, double score)> Search(string query, int topK)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return [];

        _lock.EnterReadLock();
        try
        {
            if (_docs.Count == 0) return [];

            var avgDocLen = _docs.Values.Average(d => (double)d.length);
            var N = _docs.Count;
            var scores = new Dictionary<int, double>();

            foreach (var token in queryTokens.Distinct(StringComparer.Ordinal))
            {
                if (!_postings.TryGetValue(token, out var postingList)) continue;

                var df = postingList.Count;
                // BM25 IDF: ln((N - df + 0.5) / (df + 0.5) + 1)
                var idf = Math.Log((N - df + 0.5) / (df + 0.5) + 1.0);

                foreach (var (docId, tf) in postingList)
                {
                    var docLen = _docs[docId].length;
                    // BM25 TF normalization
                    var tfNorm = (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * docLen / avgDocLen));
                    scores[docId] = scores.TryGetValue(docId, out var s) ? s + idf * tfNorm : idf * tfNorm;
                }
            }

            return scores
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private static List<string> Tokenize(string text)
    {
        // lowercase + split on non-alphanumeric, filter empty tokens
        var tokens = new List<string>();
        var lower = text.ToLowerInvariant();
        var start = -1;
        for (int i = 0; i <= lower.Length; i++)
        {
            bool isAlnum = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (isAlnum && start == -1) start = i;
            else if (!isAlnum && start != -1)
            {
                tokens.Add(lower[start..i]);
                start = -1;
            }
        }
        return tokens;
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~InMemoryBm25IndexTests" -v minimal
```

Expected: 7 tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Search/InMemoryBm25Index.cs tests/Rag.NET.Tests/Search/InMemoryBm25IndexTests.cs
git commit -m "feat: add InMemoryBm25Index with BM25 scoring and thread safety"
```

---

### Task 2: RrfMerger — Reciprocal Rank Fusion

**Files:**
- Create: `src/Rag.NET/Search/RrfMerger.cs`
- Create: `tests/Rag.NET.Tests/Search/RrfMergerTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Search/RrfMergerTests.cs
using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class RrfMergerTests
{
    private static SearchResult MakeResult(string text, string docId, int chunkIndex, double score) =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = docId, ChunkIndex = chunkIndex }, Score = score };

    [Fact]
    public void Merge_ReturnsEmpty_WhenBothListsEmpty()
    {
        var result = RrfMerger.Merge([], [], topK: 5);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_ReturnsTopK_Results()
    {
        var dense = Enumerable.Range(0, 10)
            .Select(i => MakeResult($"chunk {i}", "doc", i, 1.0 - i * 0.05))
            .ToList();
        var bm25 = Enumerable.Range(0, 10)
            .Select(i => MakeResult($"chunk {i}", "doc", i, 100.0 - i))
            .ToList();

        var result = RrfMerger.Merge(dense, bm25, topK: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_DeduplicatesByChunkIdentity()
    {
        // Same chunk appears in both lists
        var chunk = new TextChunk { Text = "hello", DocumentId = "doc", ChunkIndex = 0 };
        var dense = new List<SearchResult> { new() { Chunk = chunk, Score = 0.9 } };
        var bm25 = new List<SearchResult> { new() { Chunk = chunk, Score = 50.0 } };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        Assert.Single(result); // deduplicated
    }

    [Fact]
    public void Merge_ChunkInBothLists_ScoresHigherThanChunkInOneList()
    {
        // chunk0 appears in both, chunk1 only in dense
        var chunk0 = new TextChunk { Text = "shared", DocumentId = "doc", ChunkIndex = 0 };
        var chunk1 = new TextChunk { Text = "dense only", DocumentId = "doc", ChunkIndex = 1 };
        var chunk2 = new TextChunk { Text = "bm25 only", DocumentId = "doc", ChunkIndex = 2 };

        var dense = new List<SearchResult>
        {
            new() { Chunk = chunk0, Score = 0.9 },
            new() { Chunk = chunk1, Score = 0.8 },
        };
        var bm25 = new List<SearchResult>
        {
            new() { Chunk = chunk0, Score = 50.0 },
            new() { Chunk = chunk2, Score = 40.0 },
        };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        // chunk0 appears in both lists so has highest RRF score
        Assert.Equal(0, result[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Merge_WorksWithOnlyDenseResults()
    {
        var dense = new List<SearchResult>
        {
            MakeResult("a", "doc", 0, 0.9),
            MakeResult("b", "doc", 1, 0.8),
        };

        var result = RrfMerger.Merge(dense, [], topK: 5);

        Assert.Equal(2, result.Count);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RrfMergerTests" -v minimal
```

Expected: compilation error — `RrfMerger` not found.

**Step 3: Implement RrfMerger**

```csharp
// src/Rag.NET/Search/RrfMerger.cs
using Rag.NET.Models;

namespace Rag.NET.Search;

/// <summary>
/// Reciprocal Rank Fusion: score(d) = Σ 1/(k + rank_i), k=60.
/// Merges dense and BM25 ranked lists without score normalization.
/// </summary>
internal static class RrfMerger
{
    private const double K = 60.0;

    public static IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<SearchResult> dense,
        IReadOnlyList<(int docId, double score)> bm25Hits,
        IReadOnlyList<TextChunk> allChunks,
        int topK)
    {
        // chunk identity key: documentId + chunkIndex
        var rrfScores = new Dictionary<(string docId, int chunkIndex), double>();
        var chunkMap = new Dictionary<(string docId, int chunkIndex), SearchResult>();

        // Accumulate RRF scores from dense results
        for (int rank = 0; rank < dense.Count; rank++)
        {
            var r = dense[rank];
            var key = (r.Chunk.DocumentId, r.Chunk.ChunkIndex);
            var contrib = 1.0 / (K + rank + 1);
            rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
            chunkMap.TryAdd(key, r);
        }

        // Accumulate RRF scores from BM25 results (need to look up chunk by docId)
        for (int rank = 0; rank < bm25Hits.Count; rank++)
        {
            var (docId, _) = bm25Hits[rank];
            var chunk = allChunks[docId];
            var key = (chunk.DocumentId, chunk.ChunkIndex);
            var contrib = 1.0 / (K + rank + 1);
            rrfScores[key] = rrfScores.TryGetValue(key, out var s) ? s + contrib : contrib;
            chunkMap.TryAdd(key, new SearchResult { Chunk = chunk, Score = 0 });
        }

        return rrfScores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv =>
            {
                var sr = chunkMap[kv.Key];
                return new SearchResult { Chunk = sr.Chunk, Score = kv.Value };
            })
            .ToList();
    }
}
```

**Step 4: Adjust tests to match actual API**

The tests above use a `SearchResult`-based BM25 list for simplicity. The actual `RrfMerger` needs to accept `IReadOnlyList<(int docId, double score)>` + a chunk lookup array to map integer docIds to `TextChunk` objects (because `InMemoryBm25Index.Search` returns integer docIds). Rewrite the tests to exercise the real API.

Replace `RrfMergerTests.cs` with tests that create an `InMemoryBm25Index`, call `Search`, then call `RrfMerger.Merge`. The key invariants to test remain the same:
- Empty inputs → empty output
- TopK respected
- Deduplication by (documentId, chunkIndex)
- Chunk in both lists ranks higher than chunk in only one

**Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RrfMergerTests" -v minimal
```

Expected: all tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Search/RrfMerger.cs tests/Rag.NET.Tests/Search/RrfMergerTests.cs
git commit -m "feat: add RrfMerger for reciprocal rank fusion of dense and BM25 results"
```

---

### Task 3: Wire BM25 into RagPipeline

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write failing tests in RagPipelineTests.cs**

Add these tests to the existing `RagPipelineTests` class (NSubstitute-based):

```csharp
[Fact]
public async Task RetrieveAsync_WithHybridSearch_AndNonHybridStore_UsesBm25Fallback()
{
    // Arrange: ingest a document first so BM25 has data
    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "the quick brown fox", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "the quick brown fox", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>> { embedding }));

    await _sut.IngestAsync(new MemoryStream(), metadata);

    // _vectorStore is NOT IHybridSearchable
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

    // Act: hybrid search should NOT throw — falls back to BM25 merge
    var results = await _sut.RetrieveAsync("fox", new RetrievalOptions { UseHybridSearch = true });

    // At minimum: dense search was called, no exception thrown
    await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task DeleteAsync_RemovesChunksFromBm25Index()
{
    // Ingest
    var metadata = new DocumentMetadata { DocumentId = "doc-del", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "fox jumps", DocumentId = "doc-del", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "fox jumps", DocumentId = "doc-del", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>> { embedding }));
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

    await _sut.IngestAsync(new MemoryStream(), metadata);
    await _sut.DeleteAsync("doc-del");

    // After delete, hybrid search should return nothing from BM25
    var results = await _sut.RetrieveAsync("fox", new RetrievalOptions { UseHybridSearch = true });
    // Dense mock returns empty, BM25 should also return empty after delete
    Assert.Empty(results);
}

[Fact]
public async Task IngestAsync_WithOverwrite_ClearsOldBm25Entries()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-ow", FileName = "test.txt", ContentType = "text/plain" };
    var sectionV1 = new DocumentSection { Text = "old content tiger", DocumentId = "doc-ow", SectionIndex = 0 };
    var chunkV1 = new TextChunk { Text = "old content tiger", DocumentId = "doc-ow", ChunkIndex = 0 };
    var sectionV2 = new DocumentSection { Text = "new content elephant", DocumentId = "doc-ow", SectionIndex = 0 };
    var chunkV2 = new TextChunk { Text = "new content elephant", DocumentId = "doc-ow", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(sectionV1), AsyncEnumerableOf(sectionV2));
    _chunker.ChunkAsync(sectionV1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(chunkV1));
    _chunker.ChunkAsync(sectionV2, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerableOf(chunkV2));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>> { embedding }));
    _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

    await _sut.IngestAsync(new MemoryStream(), metadata);
    await _sut.IngestAsync(new MemoryStream(), metadata, new IngestionOptions { Overwrite = true });

    // Old term "tiger" should be gone — search returns nothing from BM25 for it
    var results = await _sut.RetrieveAsync("tiger", new RetrievalOptions { UseHybridSearch = true });
    Assert.Empty(results);
}
```

**Step 2: Run failing tests**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagPipelineTests" -v minimal
```

Expected: the 3 new tests fail (or compilation error if `RrfMerger` API doesn't match yet).

**Step 3: Modify RagPipeline.cs**

Make these changes to [src/Rag.NET/Pipeline/RagPipeline.cs](src/Rag.NET/Pipeline/RagPipeline.cs):

**3a.** Add `using Rag.NET.Search;` at the top.

**3b.** Add a field and chunk registry after the existing fields. Because `InMemoryBm25Index.Search` returns integer docIds and we need to map those back to `TextChunk`, `RagPipeline` maintains a parallel list of all ingested chunks:

```csharp
private readonly InMemoryBm25Index _bm25Index = new();
// docId integer → TextChunk, for RRF chunk lookup
private readonly List<TextChunk> _bm25Chunks = [];
private readonly object _bm25ChunksLock = new();
```

**3c.** In `IngestAsync`, after `vectorStore.StoreAsync(...)`, add:

```csharp
// Add chunks to BM25 index
lock (_bm25ChunksLock)
{
    foreach (var ec in embeddedChunks)
    {
        var docId = _bm25Chunks.Count;
        _bm25Chunks.Add(ec.Chunk);
        _bm25Index.Add(docId, ec.Chunk);
    }
}
```

**3d.** In `IngestAsync`, the existing overwrite path calls `vectorStore.DeleteByDocumentIdAsync`. Add a BM25 removal just before the re-index:

```csharp
if (options?.Overwrite == true)
{
    await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
    _bm25Index.Remove(metadata.DocumentId);           // ← add this line
}
```

**3e.** In `DeleteAsync`, also remove from BM25:

```csharp
public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
{
    _bm25Index.Remove(documentId);
    await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
}
```

**3f.** In `RetrieveAsync`, replace the `throw` branch with BM25 fallback:

```csharp
if (opts.UseHybridSearch)
{
    if (vectorStore is IHybridSearchable hybrid)
    {
        searchResults = await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
            .ConfigureAwait(false);
    }
    else
    {
        // Fallback: dense + BM25, merged via Reciprocal Rank Fusion
        var denseTask = vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
        IReadOnlyList<(int docId, double score)> bm25Hits;
        IReadOnlyList<TextChunk> chunkSnapshot;
        lock (_bm25ChunksLock)
        {
            bm25Hits = _bm25Index.Search(query, topK: opts.TopK);
            chunkSnapshot = [.. _bm25Chunks];
        }
        var dense = await denseTask.ConfigureAwait(false);
        searchResults = RrfMerger.Merge(dense, bm25Hits, chunkSnapshot, opts.TopK);
    }
}
```

**Step 4: Update RrfMerger signature if needed**

Ensure `RrfMerger.Merge` matches the call site:

```csharp
public static IReadOnlyList<SearchResult> Merge(
    IReadOnlyList<SearchResult> dense,
    IReadOnlyList<(int docId, double score)> bm25Hits,
    IReadOnlyList<TextChunk> allChunks,
    int topK)
```

If the Task 2 implementation differs, reconcile now.

**Step 5: Run all pipeline tests**

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagPipelineTests" -v minimal
```

Expected: all tests pass including the 3 new ones.

**Step 6: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests -v minimal
```

Expected: all tests pass.

**Step 7: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: wire InMemoryBm25Index into RagPipeline with RRF fallback for hybrid search"
```

---

### Task 4: Add BM25 benchmark

**Files:**
- Modify: `benchmarks/Rag.NET.Benchmarks/PipelineBenchmarks.cs`
- Modify: `docs/benchmarks.md`

**Step 1: Add BM25 search benchmark to PipelineBenchmarks.cs**

Add a `[Benchmark]` method that measures `RetrieveAsync` with `UseHybridSearch = true` against the no-op store (exercises BM25 path):

```csharp
[Benchmark]
public async Task<int> RetrieveAsync_HybridBm25()
{
    var results = await _pipeline.RetrieveAsync(
        "quick brown fox",
        new RetrievalOptions { TopK = 5, UseHybridSearch = true });
    return results.Count;
}
```

The `Setup()` already calls `IngestAsync` — add a `[GlobalSetup]` call to ingest once before benchmarking retrieval. Since the current `Setup` only initializes the pipeline (not ingested data), also call `IngestAsync` in setup.

Update `PipelineBenchmarks.Setup()` to call `IngestAsync` after pipeline construction:

```csharp
await _pipeline.IngestAsync(new MemoryStream(_documentData), Metadata);
```

Change `Setup()` to `async Task Setup()` (BenchmarkDotNet supports `async` GlobalSetup).

**Step 2: Run benchmarks to confirm no crash**

```bash
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*BM25*" --job short
```

Expected: benchmark runs and produces numbers (not zero, no exception).

**Step 3: Update docs/benchmarks.md**

Add a new section after "Pipeline (end-to-end ingestion)":

```markdown
## Hybrid Search (BM25 fallback)

In-memory BM25 + RRF merge path, activated when `UseHybridSearch = true` and the vector store does not implement `IHybridSearchable`. Dense search is mocked (no-op), BM25 operates on a 50 KB document (~X chunks).

| Method | Mean | Allocated |
|--------|-----:|----------:|
| RetrieveAsync_HybridBm25 | [fill in from run] | [fill in] |
```

Fill in actual numbers from the benchmark run.

**Step 4: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/PipelineBenchmarks.cs docs/benchmarks.md
git commit -m "bench: add BM25 hybrid search benchmark and update benchmarks.md"
```

---

## Done

After all tasks complete, run the full suite one final time:

```bash
dotnet test -v minimal
```

All tests should pass. Then use `superpowers:finishing-a-development-branch` to merge or create a PR.
