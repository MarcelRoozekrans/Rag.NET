# Decorator Pipeline Refactoring — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Decompose the monolithic `RagPipeline` into composable decorator classes behind `IRetriever`, `IIngestor`, and `IAnswerEngine` interfaces, while keeping `IRagPipeline` as the unchanged public facade.

**Architecture:** Three internal interfaces (`IRetriever`, `IIngestor`, `IAnswerEngine`) with base implementations extracted from `RagPipeline`. Optional features (multi-query, reranking, redundancy filter, lost-in-middle) become retrieval decorators. `RagPipeline` becomes a thin coordinator that delegates to these interfaces. `InMemoryBm25Index` becomes a DI singleton shared between ingestor and retriever.

**Tech Stack:** C# / .NET 10, NSubstitute for mocking, xUnit for tests, Microsoft.Extensions.DependencyInjection

---

## Phase 1: Interfaces & Base Implementations

### Task 1: Make `RetrievalOptions` a record

`RetrievalOptions` must support `with` expressions so decorators can modify options (e.g. override `TopK`) without mutating the caller's instance.

**Files:**
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Test: `tests/Rag.NET.Tests/Models/RetrievalOptionsTests.cs`

**Step 1: Write the failing test**

Create `tests/Rag.NET.Tests/Models/RetrievalOptionsTests.cs`:

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models;

public class RetrievalOptionsTests
{
    [Fact]
    public void With_TopK_ReturnsNewInstanceWithUpdatedValue()
    {
        var original = new RetrievalOptions { TopK = 5 };
        var modified = original with { TopK = 15 };

        Assert.Equal(5, original.TopK);
        Assert.Equal(15, modified.TopK);
    }

    [Fact]
    public void With_PreservesOtherProperties()
    {
        var original = new RetrievalOptions
        {
            TopK = 5,
            MinScore = 0.5,
            UseHybridSearch = true,
            UseReranking = false,
            RedundancyThreshold = 0.8f,
        };
        var modified = original with { TopK = 15 };

        Assert.Equal(0.5, modified.MinScore);
        Assert.True(modified.UseHybridSearch);
        Assert.False(modified.UseReranking);
        Assert.Equal(0.8f, modified.RedundancyThreshold);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RetrievalOptionsTests" --no-restore`
Expected: FAIL — `with` expressions require record types; `RetrievalOptions` is currently a `class`.

**Step 3: Convert `RetrievalOptions` to a record**

Replace the entire content of `src/Rag.NET/Models/Options/RetrievalOptions.cs`:

```csharp
namespace Rag.NET.Models.Options;

public sealed record RetrievalOptions
{
    public int TopK { get; init; } = 5;
    public double MinScore { get; init; } = 0.0;
    public IDictionary<string, string>? MetadataFilter { get; init; }
    public bool UseHybridSearch { get; init; }
    public bool UseLostInTheMiddleReordering { get; init; }
    public bool UseRedundancyFilter { get; init; }
    public float RedundancyThreshold { get; init; } = 0.95f;

    /// <summary>
    /// Set to <see langword="false"/> to skip multi-query expansion for this call,
    /// even when <see cref="Rag.NET.Abstractions.IQueryExpander"/> is registered in DI.
    /// Has no effect when no expander is registered.
    /// </summary>
    public bool UseMultiQuery { get; init; } = true;

    /// <summary>
    /// Set to <see langword="false"/> to skip cross-encoder reranking for this call,
    /// even when <see cref="Rag.NET.Abstractions.IReranker"/> is registered in DI.
    /// Has no effect when no reranker is registered.
    /// </summary>
    public bool UseReranking { get; init; } = true;

    /// <summary>
    /// Number of candidates to fetch from vector search before reranking.
    /// When an <see cref="Rag.NET.Abstractions.IReranker"/> is registered and this is
    /// <see langword="null"/>, defaults to <see cref="TopK"/> * 3.
    /// Ignored when no reranker is registered or <see cref="UseReranking"/> is <see langword="false"/>.
    /// </summary>
    public int? CandidateCount { get; init; }
}
```

**Important:** Change `set` → `init` on all properties. Record types use `init`-only setters. The `with` expression creates a new instance.

**Caution:** This changes `set` to `init`, which means existing code that sets properties after construction (e.g. `opts.TopK = 10;`) will break. Search the codebase for any such usage. In `RagPipeline.cs` the `RetrievalOptions` is constructed via object initializer syntax (e.g. `new RetrievalOptions { TopK = ... }`), which works fine with `init`. The tests also use object initializers. In `RagPipeline.AskAsync` and `AskStreamingAsync`, options are built as `new RetrievalOptions { ... }` — this works. Verify all usages compile.

**Step 4: Fix compilation — update any `set`-based property assignments**

Check `src/Rag.NET/Pipeline/RagPipeline.cs` lines 318-327 and 374-383 — the `AskAsync` / `AskStreamingAsync` methods build `RetrievalOptions` via object initializers. These are fine with `init`.

Check test files — the existing tests in `RagPipelineTests.cs` use object initializers. These are fine.

Run: `dotnet build src/Rag.NET`
Expected: Build succeeds.

**Step 5: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RetrievalOptionsTests" --no-restore`
Expected: PASS

**Step 6: Run all existing tests to verify no regressions**

Run: `dotnet test tests/Rag.NET.Tests --no-restore`
Expected: All tests pass.

**Step 7: Commit**

```bash
git add src/Rag.NET/Models/Options/RetrievalOptions.cs tests/Rag.NET.Tests/Models/RetrievalOptionsTests.cs
git commit -m "refactor: convert RetrievalOptions to record for with-expression support"
```

---

### Task 2: Make `InMemoryBm25Index` public and register as DI singleton

Currently `internal sealed class`. Needs to be injectable so both `DocumentIngestor` and `VectorStoreRetriever` can share one instance.

**Files:**
- Modify: `src/Rag.NET/Search/InMemoryBm25Index.cs` (change `internal` → `public`)
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` (register singleton)

**Step 1: Change visibility**

In `src/Rag.NET/Search/InMemoryBm25Index.cs`, line 10, change:

```csharp
internal sealed class InMemoryBm25Index : IDisposable
```
to:
```csharp
public sealed class InMemoryBm25Index : IDisposable
```

**Step 2: Register in DI**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`, add inside `AddRagNet` before the `IRagPipeline` registration (after the `TryAddSingleton<IChunkingStrategy>` line):

```csharp
services.AddSingleton<InMemoryBm25Index>();
```

Add the using at the top of the file:

```csharp
using Rag.NET.Search;
```

**Step 3: Build and run all tests**

Run: `dotnet build src/Rag.NET && dotnet test tests/Rag.NET.Tests --no-restore`
Expected: All pass. This is a visibility change only — nothing consumes the DI registration yet.

**Step 4: Commit**

```bash
git add src/Rag.NET/Search/InMemoryBm25Index.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "refactor: make InMemoryBm25Index public and register as DI singleton"
```

---

### Task 3: Create `IRetriever` interface

**Files:**
- Create: `src/Rag.NET/Abstractions/IRetriever.cs`

**Step 1: Create the interface**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Retrieves semantically relevant chunks for a given query.
/// Implementations may compose as decorators to add post-retrieval processing.
/// </summary>
public interface IRetriever
{
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

**Step 2: Build**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/Rag.NET/Abstractions/IRetriever.cs
git commit -m "feat: add IRetriever interface"
```

---

### Task 4: Create `IIngestor` interface

**Files:**
- Create: `src/Rag.NET/Abstractions/IIngestor.cs`

**Step 1: Create the interface**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Parses, chunks, embeds, and stores documents.
/// Implementations may compose as decorators to add pre/post-processing.
/// </summary>
public interface IIngestor
{
    Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
```

**Step 2: Build**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/Rag.NET/Abstractions/IIngestor.cs
git commit -m "feat: add IIngestor interface"
```

---

### Task 5: Create `IAnswerEngine` interface

**Files:**
- Create: `src/Rag.NET/Abstractions/IAnswerEngine.cs`

**Step 1: Create the interface**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Generates answers from pre-retrieved search results using an LLM.
/// Implementations may compose as decorators to alter generation strategy.
/// </summary>
public interface IAnswerEngine
{
    Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

**Step 2: Build**

Run: `dotnet build src/Rag.NET`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/Rag.NET/Abstractions/IAnswerEngine.cs
git commit -m "feat: add IAnswerEngine interface"
```

---

### Task 6: Extract `VectorStoreRetriever` (base retriever)

Extract the core retrieval logic from `RagPipeline.RetrieveAsync` and `SearchSingleQueryAsync` into a standalone class.

**Files:**
- Create: `src/Rag.NET/Retrieval/VectorStoreRetriever.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/VectorStoreRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/VectorStoreRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class VectorStoreRetrieverTests
{
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly InMemoryBm25Index _bm25Index = new();
    private readonly VectorStoreRetriever _sut;

    public VectorStoreRetrieverTests()
    {
        _sut = new VectorStoreRetriever(_vectorStore, _embedder, _bm25Index);
    }

    [Fact]
    public async Task RetrieveAsync_EmbedsQueryAndSearchesVectorStore()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = new SearchResult
        {
            Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.95
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([expected]);

        var results = await _sut.RetrieveAsync("test query");

        Assert.Single(results);
        Assert.Equal(0.95, results[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_UseHybridSearch_WithHybridSearchable_CallsHybridSearchAsync()
    {
        var hybridStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var sut = new VectorStoreRetriever(hybridStore, _embedder, _bm25Index);

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = new SearchResult
        {
            Chunk = new TextChunk { Text = "hybrid", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.9
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        ((IHybridSearchable)hybridStore).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([expected]);

        var results = await sut.RetrieveAsync("test", new RetrievalOptions { UseHybridSearch = true });

        Assert.Single(results);
        Assert.Equal("hybrid", results[0].Chunk.Text);
        await ((IHybridSearchable)hybridStore).Received(1).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_UseHybridSearch_WithoutHybridSearchable_UsesBm25Fallback()
    {
        // Add a chunk to BM25 so it has something to return
        _bm25Index.Add(1, new TextChunk { Text = "hello world bm25", DocumentId = "doc-1", ChunkIndex = 0 });

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var denseResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "dense result", DocumentId = "doc-2", ChunkIndex = 0 },
            Score = 0.8
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([denseResult]);

        var results = await _sut.RetrieveAsync("hello world", new RetrievalOptions { UseHybridSearch = true });

        Assert.NotEmpty(results);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~VectorStoreRetrieverTests" --no-restore`
Expected: FAIL — `VectorStoreRetriever` does not exist yet.

**Step 3: Create the implementation**

Create `src/Rag.NET/Retrieval/VectorStoreRetriever.cs`:

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;

namespace Rag.NET.Retrieval;

/// <summary>
/// Base retriever that embeds the query and searches the vector store.
/// Handles hybrid search via <see cref="IHybridSearchable"/> or BM25 fallback + RRF merge.
/// </summary>
public sealed class VectorStoreRetriever(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    InMemoryBm25Index bm25Index) : IRetriever
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (opts.UseHybridSearch)
        {
            if (vectorStore is IHybridSearchable hybrid)
            {
                return await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            var denseTask = vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
            var bm25Hits = bm25Index.Search(query, topK: searchOptions.TopK);
            var dense = await denseTask.ConfigureAwait(false);
            return RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
        }

        return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~VectorStoreRetrieverTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/VectorStoreRetriever.cs tests/Rag.NET.Tests/Retrieval/VectorStoreRetrieverTests.cs
git commit -m "feat: extract VectorStoreRetriever as base IRetriever implementation"
```

---

### Task 7: Extract `DocumentIngestor` (base ingestor)

Extract ingestion logic from `RagPipeline.IngestAsync`, `ParseAndChunkAsync`, `ApplyMetadataTags`, `ReportProgress`, and `DeleteAsync`.

**Files:**
- Create: `src/Rag.NET/Ingestion/DocumentIngestor.cs`
- Test: `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class DocumentIngestorTests
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly InMemoryBm25Index _bm25Index = new();
    private readonly DocumentIngestor _sut;

    public DocumentIngestorTests()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        _sut = new DocumentIngestor([_parser], _chunker, _vectorStore, _embedder, new ChunkingOptions(), _bm25Index);
    }

    [Fact]
    public async Task IngestAsync_OrchestratesParseChunkEmbedStore()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello world"));
        var result = await _sut.IngestAsync(stream, metadata);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
        await _vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(c => c.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_EmptyDocument_ReturnsZeroChunks()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-2", FileName = "empty.txt", ContentType = "text/plain" };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<DocumentSection>());

        using var stream = new MemoryStream();
        var result = await _sut.IngestAsync(stream, metadata);

        Assert.Equal(0, result.ChunksStored);
        await _vectorStore.DidNotReceive().StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToVectorStoreAndBm25()
    {
        await _sut.DeleteAsync("doc-1");

        await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DocumentIngestorTests" --no-restore`
Expected: FAIL — `DocumentIngestor` does not exist.

**Step 3: Create the implementation**

Create `src/Rag.NET/Ingestion/DocumentIngestor.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;

namespace Rag.NET.Ingestion;

/// <summary>
/// Base ingestor that parses, chunks, embeds, and stores documents.
/// </summary>
public sealed class DocumentIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ChunkingOptions chunkingOptions,
    InMemoryBm25Index bm25Index) : IIngestor
{
    private int _nextBm25DocId;

    public async Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        if (options?.Overwrite == true)
        {
            await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
            bm25Index.Remove(metadata.DocumentId);
        }

        var chunks = await ParseAndChunkAsync(parser, document, metadata, cancellationToken).ConfigureAwait(false);

        ReportProgress(progress, IngestionProgressStage.Parsing, metadata.DocumentId, null, null, "Parsing complete");
        ApplyMetadataTags(chunks, metadata);
        ReportProgress(progress, IngestionProgressStage.Chunking, metadata.DocumentId, chunks.Count, chunks.Count, $"Chunked into {chunks.Count} chunks");

        if (chunks.Count == 0)
            return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var embeddedChunks = chunks
            .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk { Chunk = chunk, Embedding = embedding.Vector })
            .ToList();

        ReportProgress(progress, IngestionProgressStage.Embedding, metadata.DocumentId, embeddedChunks.Count, embeddedChunks.Count, $"Generated {embeddedChunks.Count} embeddings");
        await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, IngestionProgressStage.Storing, metadata.DocumentId, embeddedChunks.Count, embeddedChunks.Count, $"Stored {embeddedChunks.Count} chunks");

        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(embeddedChunks))
        {
            var id = System.Threading.Interlocked.Increment(ref _nextBm25DocId);
            bm25Index.Add(id, ec.Chunk);
        }

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        bm25Index.Remove(documentId);
    }

    private async Task<List<TextChunk>> ParseAndChunkAsync(
        IDocumentParser parser,
        Stream document,
        DocumentMetadata metadata,
        CancellationToken cancellationToken)
    {
        var chunks = new List<TextChunk>();
        var headingBreadcrumbs = new string?[6];

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, string>? headingMetadata = null;

            if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
            {
                headingBreadcrumbs[level - 1] = section.Heading;
                var idx = level;
                while (idx < 6)
                {
                    headingBreadcrumbs[idx] = null;
                    idx++;
                }

                var parts = new List<string>(level);
                foreach (var h in headingBreadcrumbs[..level])
                {
                    if (h is not null)
                        parts.Add(h);
                }

                var breadcrumb = string.Join(" > ", parts);
                headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = section.Heading,
                    ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["heading_breadcrumb"] = breadcrumb,
                };
            }

            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                {
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);
                }

                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private static void ApplyMetadataTags(List<TextChunk> chunks, DocumentMetadata metadata)
    {
        foreach (ref var chunk in CollectionsMarshal.AsSpan(chunks))
        {
            foreach (var tag in metadata.Tags)
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", metadata.FileName);
        }
    }

    private static void ReportProgress(
        IProgress<IngestionProgress>? progress,
        IngestionProgressStage stage,
        string documentId,
        int? current,
        int? total,
        string message)
    {
        progress?.Report(new IngestionProgress
        {
            Stage = stage,
            DocumentId = documentId,
            Current = current,
            Total = total,
            Message = message,
        });
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DocumentIngestorTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/DocumentIngestor.cs tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs
git commit -m "feat: extract DocumentIngestor as base IIngestor implementation"
```

---

### Task 8: Extract `ChatAnswerEngine` (base answer engine)

Extract `AskAsync`, `AskStreamingAsync`, and `BuildRagMessages` from `RagPipeline`.

**Files:**
- Create: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`
- Test: `tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class ChatAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly ChatAnswerEngine _sut;

    public ChatAnswerEngineTests()
    {
        _sut = new ChatAnswerEngine(_chatClient);
    }

    [Fact]
    public async Task AskAsync_BuildsPromptAndReturnsResponse()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "Source text", DocumentId = "doc-1", ChunkIndex = 0 }, Score = 0.9 }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The answer")));

        var result = await _sut.AskAsync("What is this?", sources);

        Assert.Equal("The answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_WithCustomSystemPrompt_UsesIt()
    {
        var sources = new List<SearchResult>();
        var opts = new RagOptions { SystemPrompt = "Custom prompt" };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await _sut.AskAsync("q", sources, opts);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs[0].Text == "Custom prompt"),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ChatAnswerEngineTests" --no-restore`
Expected: FAIL — `ChatAnswerEngine` does not exist.

**Step 3: Create the implementation**

Create `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`:

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Generates answers by building a context prompt from search results and calling an <see cref="IChatClient"/>.
/// </summary>
public sealed class ChatAnswerEngine(IChatClient chatClient) : IAnswerEngine
{
    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (messages, chatOptions) = BuildMessages(sources, query, options ?? new RagOptions());

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = response.Text ?? string.Empty,
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

        var (messages, chatOptions) = BuildMessages(sources, query, options ?? new RagOptions());

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is not null)
            {
                yield return new RagStreamingUpdate { TextDelta = update.Text };
            }
        }
    }

    private static (List<ChatMessage> Messages, ChatOptions Options) BuildMessages(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts)
    {
        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
        };

        if (opts.ConversationHistory is { Count: > 0 })
        {
            messages.AddRange(opts.ConversationHistory);
        }

        messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        return (messages, chatOptions);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ChatAnswerEngineTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs
git commit -m "feat: extract ChatAnswerEngine as base IAnswerEngine implementation"
```

---

### Task 9: Rewrite `RagPipeline` as thin coordinator

Replace the monolithic `RagPipeline` with a thin class that delegates to `IRetriever`, `IIngestor`, and `IAnswerEngine`. The existing `IRagPipeline` interface is unchanged.

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Test: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` (update to test delegation)

**Step 1: Write new facade tests**

Create `tests/Rag.NET.Tests/Pipeline/RagPipelineFacadeTests.cs` with delegation tests:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineFacadeTests
{
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();
    private readonly IIngestor _ingestor = Substitute.For<IIngestor>();
    private readonly IAnswerEngine _answerEngine = Substitute.For<IAnswerEngine>();
    private readonly RagPipeline _sut;

    public RagPipelineFacadeTests()
    {
        _sut = new RagPipeline(_retriever, _ingestor, _answerEngine);
    }

    [Fact]
    public async Task RetrieveAsync_DelegatesToRetriever()
    {
        var expected = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "x", DocumentId = "d", ChunkIndex = 0 }, Score = 1.0 }
        };
        var opts = new RetrievalOptions { TopK = 10 };
        _retriever.RetrieveAsync("query", opts, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.RetrieveAsync("query", opts);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task IngestAsync_DelegatesToIngestor()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt", ContentType = "text/plain" };
        var expected = new IngestionResult { DocumentId = "doc-1", ChunksStored = 5 };
        _ingestor.IngestAsync(Arg.Any<Stream>(), metadata, Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        using var stream = new MemoryStream();
        var result = await _sut.IngestAsync(stream, metadata);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToIngestor()
    {
        await _sut.DeleteAsync("doc-1");
        await _ingestor.Received(1).DeleteAsync("doc-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_RetrievesThenDelegatesToAnswerEngine()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "ctx", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 }
        };
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(sources);

        var expected = new RagResponse { Answer = "The answer", Sources = sources };
        _answerEngine.AskAsync("q", sources, Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.AskAsync("q");

        Assert.Equal("The answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_WithoutAnswerEngine_ThrowsInvalidOperationException()
    {
        var sut = new RagPipeline(_retriever, _ingestor, answerEngine: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.AskAsync("q"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagPipelineFacadeTests" --no-restore`
Expected: FAIL — constructor signature doesn't match.

**Step 3: Rewrite `RagPipeline`**

Replace the entire content of `src/Rag.NET/Pipeline/RagPipeline.cs`:

```csharp
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Pipeline;

/// <summary>
/// Thin coordinator that delegates to <see cref="IRetriever"/>, <see cref="IIngestor"/>,
/// and <see cref="IAnswerEngine"/>. The public <see cref="IRagPipeline"/> facade is unchanged.
/// </summary>
public sealed class RagPipeline(
    IRetriever retriever,
    IIngestor ingestor,
    IAnswerEngine? answerEngine = null) : IRagPipeline
{
    public Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ingestor.IngestAsync(document, metadata, options, progress, cancellationToken);

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => retriever.RetrieveAsync(query, options, cancellationToken);

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (answerEngine is null)
            throw new InvalidOperationException(
                "IAnswerEngine is not registered. Register an IChatClient in DI to use AskAsync.");

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
            UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,
            UseRedundancyFilter = opts.UseRedundancyFilter,
            RedundancyThreshold = opts.RedundancyThreshold,
        };
        var sources = await retriever.RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        return await answerEngine.AskAsync(query, sources, opts, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (answerEngine is null)
            throw new InvalidOperationException(
                "IAnswerEngine is not registered. Register an IChatClient in DI to use AskStreamingAsync.");

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
            UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,
            UseRedundancyFilter = opts.UseRedundancyFilter,
            RedundancyThreshold = opts.RedundancyThreshold,
        };
        var sources = await retriever.RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        await foreach (var update in answerEngine.AskStreamingAsync(query, sources, opts, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => ingestor.DeleteAsync(documentId, cancellationToken);
}
```

**Step 4: Run facade tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RagPipelineFacadeTests" --no-restore`
Expected: PASS

**Step 5: Remove the old `RagPipelineTests.cs`**

The old tests directly instantiate `RagPipeline` with 11 constructor params. These no longer compile. Delete `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` — the behaviour is now covered by the per-component tests (`VectorStoreRetrieverTests`, `DocumentIngestorTests`, `ChatAnswerEngineTests`) and the facade tests (`RagPipelineFacadeTests`).

Run: `rm tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 6: Build and run all tests**

Run: `dotnet test tests/Rag.NET.Tests --no-restore`
Expected: All tests pass.

**Step 7: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineFacadeTests.cs
git rm tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "refactor: rewrite RagPipeline as thin coordinator delegating to IRetriever, IIngestor, IAnswerEngine"
```

---

## Phase 2: Retrieval Decorators

### Task 10: Extract `MultiQueryRetriever` decorator

**Files:**
- Create: `src/Rag.NET/Retrieval/MultiQueryRetriever.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/MultiQueryRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/MultiQueryRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class MultiQueryRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IQueryExpander _expander = Substitute.For<IQueryExpander>();
    private readonly MultiQueryRetriever _sut;

    public MultiQueryRetrieverTests()
    {
        _sut = new MultiQueryRetriever(_inner, _expander, new MultiQueryOptions { VariantCount = 2 });
    }

    [Fact]
    public async Task RetrieveAsync_ExpandsQueryAndMergesResults()
    {
        _expander.ExpandAsync("query", 2, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant1", "variant2" });

        var result1 = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 };
        var result2 = new SearchResult { Chunk = new TextChunk { Text = "b", DocumentId = "d2", ChunkIndex = 0 }, Score = 0.8 };
        var result3 = new SearchResult { Chunk = new TextChunk { Text = "c", DocumentId = "d3", ChunkIndex = 0 }, Score = 0.7 };

        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([result1]);
        _inner.RetrieveAsync("variant1", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([result2]);
        _inner.RetrieveAsync("variant2", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([result3]);

        var results = await _sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 });

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task RetrieveAsync_DeduplicatesByDocIdAndChunkIndex()
    {
        _expander.ExpandAsync("query", 2, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant1" });

        var shared = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 };
        var sharedLower = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.5 };

        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([shared]);
        _inner.RetrieveAsync("variant1", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([sharedLower]);

        var results = await _sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 });

        Assert.Single(results);
        Assert.Equal(0.9, results[0].Score); // keeps highest score
    }

    [Fact]
    public async Task RetrieveAsync_UseMultiQueryFalse_SkipsExpansion()
    {
        var expected = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 };
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([expected]);

        var results = await _sut.RetrieveAsync("query", new RetrievalOptions { UseMultiQuery = false });

        Assert.Single(results);
        await _expander.DidNotReceive().ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_ExpanderThrows_FallsBackToSingleQuery()
    {
        _expander.ExpandAsync("query", 2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("LLM down"));

        var expected = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 };
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([expected]);

        var results = await _sut.RetrieveAsync("query");

        Assert.Single(results);
    }

    [Fact]
    public async Task RetrieveAsync_ExpanderThrowsOperationCanceled_Propagates()
    {
        _expander.ExpandAsync("query", 2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RetrieveAsync("query"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~MultiQueryRetrieverTests" --no-restore`
Expected: FAIL — `MultiQueryRetriever` does not exist.

**Step 3: Create the implementation**

Create `src/Rag.NET/Retrieval/MultiQueryRetriever.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that expands the query into multiple variants via <see cref="IQueryExpander"/>,
/// searches each in parallel, and merges/deduplicates results.
/// </summary>
public sealed class MultiQueryRetriever(
    IRetriever inner,
    IQueryExpander queryExpander,
    MultiQueryOptions multiQueryOptions,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseMultiQuery)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> variants;
        try
        {
            variants = await queryExpander.ExpandAsync(query, multiQueryOptions.VariantCount, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.QueryExpansionFailed(_logger, query, ex);
            variants = [];
        }

        var allQueries = new List<string>(variants.Count + 1) { query };
        allQueries.AddRange(variants);

        var tasks = allQueries.Select(q => inner.RetrieveAsync(q, options, cancellationToken)).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        return allResults
            .SelectMany(r => r)
            .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(opts.TopK)
            .ToList()
            .AsReadOnly();
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~MultiQueryRetrieverTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/MultiQueryRetriever.cs tests/Rag.NET.Tests/Retrieval/MultiQueryRetrieverTests.cs
git commit -m "feat: extract MultiQueryRetriever decorator"
```

---

### Task 11: Extract `RerankingRetriever` decorator

**Files:**
- Create: `src/Rag.NET/Retrieval/RerankingRetriever.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/RerankingRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/RerankingRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class RerankingRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IReranker _reranker = Substitute.For<IReranker>();
    private readonly RerankingRetriever _sut;

    public RerankingRetrieverTests()
    {
        _sut = new RerankingRetriever(_inner, _reranker);
    }

    [Fact]
    public async Task RetrieveAsync_OverfetchesAndReranks()
    {
        var candidates = Enumerable.Range(0, 15)
            .Select(i => new SearchResult
            {
                Chunk = new TextChunk { Text = $"chunk-{i}", DocumentId = "d1", ChunkIndex = i },
                Score = 1.0 - i * 0.05
            }).ToList();

        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(candidates);

        _reranker.RerankAsync("query", candidates, Arg.Any<CancellationToken>())
            .Returns(candidates.Select((r, i) => new RerankResult
            {
                SearchResult = r,
                RelevanceScore = i == 5 ? 1.0 : 0.1 // chunk-5 is most relevant
            }).ToList());

        var results = await _sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 });

        Assert.Equal(5, results.Count);
        Assert.Equal("chunk-5", results[0].Chunk.Text); // highest reranker score first
    }

    [Fact]
    public async Task RetrieveAsync_UseRerankingFalse_SkipsReranking()
    {
        var expected = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([expected]);

        var results = await _sut.RetrieveAsync("query", new RetrievalOptions { UseReranking = false });

        Assert.Single(results);
        await _reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_DefaultCandidateCount_IsTopKTimes3()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 });

        await _inner.Received(1).RetrieveAsync("query",
            Arg.Is<RetrievalOptions?>(o => o != null && o.TopK == 15), // 5 * 3
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_ExplicitCandidateCount_UsesIt()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5, CandidateCount = 25 });

        await _inner.Received(1).RetrieveAsync("query",
            Arg.Is<RetrievalOptions?>(o => o != null && o.TopK == 25),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_RerankerThrows_FallsBack()
    {
        var expected = new SearchResult { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([expected]);
        _reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("model unavailable"));

        var results = await _sut.RetrieveAsync("query");

        Assert.Single(results);
    }

    [Fact]
    public async Task RetrieveAsync_RerankerThrowsOperationCanceled_Propagates()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns([]);
        _reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RetrieveAsync("query"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RerankingRetrieverTests" --no-restore`
Expected: FAIL — `RerankingRetriever` does not exist.

**Step 3: Create the implementation**

Create `src/Rag.NET/Retrieval/RerankingRetriever.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that over-fetches candidates from the inner retriever, rescores them
/// via <see cref="IReranker"/>, and returns only the top-K by relevance.
/// </summary>
public sealed class RerankingRetriever(
    IRetriever inner,
    IReranker reranker,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        if (!opts.UseReranking)
            return await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var candidateCount = opts.CandidateCount ?? opts.TopK * 3;
        var expanded = opts with { TopK = candidateCount, UseReranking = false };

        var searchResults = await inner.RetrieveAsync(query, expanded, cancellationToken).ConfigureAwait(false);

        try
        {
            var reranked = await reranker.RerankAsync(query, searchResults, cancellationToken).ConfigureAwait(false);

            var results = reranked
                .OrderByDescending(r => r.RelevanceScore)
                .Take(opts.TopK)
                .Select(r => r.SearchResult)
                .ToList()
                .AsReadOnly();

            RagPipelineLog.RerankingCompleted(_logger, searchResults.Count, results.Count);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RerankingFailed(_logger, query, ex);
            return searchResults;
        }
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RerankingRetrieverTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/RerankingRetriever.cs tests/Rag.NET.Tests/Retrieval/RerankingRetrieverTests.cs
git commit -m "feat: extract RerankingRetriever decorator"
```

---

### Task 12: Extract `RedundancyFilterRetriever` decorator

**Files:**
- Create: `src/Rag.NET/Retrieval/RedundancyFilterRetriever.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/RedundancyFilterRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/RedundancyFilterRetrieverTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class RedundancyFilterRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly RedundancyFilterRetriever _sut;

    public RedundancyFilterRetrieverTests()
    {
        _sut = new RedundancyFilterRetriever(_inner, _embedder);
    }

    [Fact]
    public async Task RetrieveAsync_UseRedundancyFilterTrue_FiltersResults()
    {
        var results = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "hello", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 },
            new() { Chunk = new TextChunk { Text = "hello", DocumentId = "d1", ChunkIndex = 1 }, Score = 0.8 }, // duplicate text
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns(results);

        // Return identical embeddings so cosine similarity = 1.0 (above 0.95 threshold)
        var embedding = new Embedding<float>(new float[] { 1.0f, 0.0f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding, embedding]));

        var filtered = await _sut.RetrieveAsync("query", new RetrievalOptions { UseRedundancyFilter = true });

        Assert.Single(filtered);
    }

    [Fact]
    public async Task RetrieveAsync_UseRedundancyFilterFalse_PassesThrough()
    {
        var results = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 },
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns(results);

        var filtered = await _sut.RetrieveAsync("query", new RetrievalOptions { UseRedundancyFilter = false });

        Assert.Single(filtered);
        await _embedder.DidNotReceive().GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RedundancyFilterRetrieverTests" --no-restore`
Expected: FAIL.

**Step 3: Create the implementation**

Create `src/Rag.NET/Retrieval/RedundancyFilterRetriever.cs`:

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that filters near-duplicate results by cosine similarity.
/// </summary>
public sealed class RedundancyFilterRetriever(
    IRetriever inner,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : IRetriever
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var opts = options ?? new RetrievalOptions();
        if (!opts.UseRedundancyFilter)
            return results;

        return await RedundancyFilter.FilterAsync(results, embeddingGenerator, opts.RedundancyThreshold, cancellationToken)
            .ConfigureAwait(false);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~RedundancyFilterRetrieverTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/RedundancyFilterRetriever.cs tests/Rag.NET.Tests/Retrieval/RedundancyFilterRetrieverTests.cs
git commit -m "feat: extract RedundancyFilterRetriever decorator"
```

---

### Task 13: Extract `LostInTheMiddleRetriever` decorator

**Files:**
- Create: `src/Rag.NET/Retrieval/LostInTheMiddleRetriever.cs`
- Test: `tests/Rag.NET.Tests/Retrieval/LostInTheMiddleRetrieverTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Retrieval/LostInTheMiddleRetrieverTests.cs`:

```csharp
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class LostInTheMiddleRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly LostInTheMiddleRetriever _sut;

    public LostInTheMiddleRetrieverTests()
    {
        _sut = new LostInTheMiddleRetriever(_inner);
    }

    [Fact]
    public async Task RetrieveAsync_UseLostInTheMiddleTrue_ReordersResults()
    {
        var results = Enumerable.Range(0, 5)
            .Select(i => new SearchResult
            {
                Chunk = new TextChunk { Text = $"chunk-{i}", DocumentId = "d1", ChunkIndex = i },
                Score = 1.0 - i * 0.1
            }).ToList();

        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns(results);

        var reordered = await _sut.RetrieveAsync("query", new RetrievalOptions { UseLostInTheMiddleReordering = true });

        // First and last should be the most relevant
        Assert.Equal("chunk-0", reordered[0].Chunk.Text);
        Assert.Equal("chunk-1", reordered[^1].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_UseLostInTheMiddleFalse_PassesThrough()
    {
        var results = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "a", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 },
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns(results);

        var returned = await _sut.RetrieveAsync("query", new RetrievalOptions { UseLostInTheMiddleReordering = false });

        Assert.Same(results, returned);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~LostInTheMiddleRetrieverTests" --no-restore`
Expected: FAIL.

**Step 3: Create the implementation**

Create `src/Rag.NET/Retrieval/LostInTheMiddleRetriever.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Retrieval;

/// <summary>
/// Decorator that reorders results using the lost-in-the-middle pattern (Liu et al. 2023).
/// </summary>
public sealed class LostInTheMiddleRetriever(IRetriever inner) : IRetriever
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = await inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        var opts = options ?? new RetrievalOptions();
        if (!opts.UseLostInTheMiddleReordering)
            return results;

        return LostInTheMiddleReorderer.Reorder(results);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~LostInTheMiddleRetrieverTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/LostInTheMiddleRetriever.cs tests/Rag.NET.Tests/Retrieval/LostInTheMiddleRetrieverTests.cs
git commit -m "feat: extract LostInTheMiddleRetriever decorator"
```

---

## Phase 3: DI Wiring

### Task 14: Update `ServiceCollectionExtensions` and `RagBuilder` to compose decorator chain

Wire all the new classes into DI. The builder still exposes the same fluent API; internally it now composes the `IRetriever` decorator chain.

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Test: `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Step 1: Write the failing integration test**

Create `tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRagNet_RegistersIRagPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddRagNet_RegistersIRetriever()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var retriever = sp.GetRequiredService<IRetriever>();

        Assert.NotNull(retriever);
    }

    [Fact]
    public void AddRagNet_RegistersIIngestor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var ingestor = sp.GetRequiredService<IIngestor>();

        Assert.NotNull(ingestor);
    }

    [Fact]
    public async Task AddRagNet_WithReranking_ChainsRerankingRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var reranker = Substitute.For<IReranker>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(new List<RerankResult>());

        services.AddRagNet(rag => rag.UseReranking<FakeReranker>());

        // Override with the mock to verify it gets called
        services.AddSingleton(reranker);

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", new RetrievalOptions { UseReranking = true });

        await reranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    // Placeholder for DI registration — never actually called
    private class FakeReranker : IReranker
    {
        public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RerankResult>>(new List<RerankResult>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ServiceCollectionExtensionsTests" --no-restore`
Expected: FAIL — DI doesn't register `IRetriever`, `IIngestor`, or compose decorators yet.

**Step 3: Rewrite `ServiceCollectionExtensions`**

Replace the entire content of `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null)
    {
        services.AddSingleton<IDocumentParser, TextDocumentParser>();
        services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();

        services.TryAddSingleton<ChunkingOptions>();
        services.TryAddSingleton<IChunkingStrategy, RecursiveChunkingStrategy>();
        services.AddSingleton<InMemoryBm25Index>();

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        // Base implementations
        services.AddSingleton<IIngestor>(sp => new DocumentIngestor(
            sp.GetServices<IDocumentParser>(),
            sp.GetRequiredService<IChunkingStrategy>(),
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<ChunkingOptions>(),
            sp.GetRequiredService<InMemoryBm25Index>()));

        // Retriever decorator chain (innermost → outermost)
        services.AddSingleton<IRetriever>(sp =>
        {
            IRetriever chain = new VectorStoreRetriever(
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<InMemoryBm25Index>());

            var queryExpander = sp.GetService<IQueryExpander>();
            if (queryExpander is not null)
            {
                chain = new MultiQueryRetriever(
                    chain,
                    queryExpander,
                    sp.GetService<MultiQueryOptions>() ?? new MultiQueryOptions(),
                    sp.GetService<ILogger<MultiQueryRetriever>>());
            }

            var reranker = sp.GetService<IReranker>();
            if (reranker is not null)
            {
                chain = new RerankingRetriever(
                    chain,
                    reranker,
                    sp.GetService<ILogger<RerankingRetriever>>());
            }

            chain = new RedundancyFilterRetriever(
                chain,
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

            chain = new LostInTheMiddleRetriever(chain);

            return chain;
        });

        // Answer engine (optional — requires IChatClient)
        services.AddSingleton<IAnswerEngine?>(sp =>
        {
            var chatClient = sp.GetService<IChatClient>();
            return chatClient is not null ? new ChatAnswerEngine(chatClient) : null;
        });

        // Public facade
        services.AddSingleton<IRagPipeline>(sp => new RagPipeline(
            sp.GetRequiredService<IRetriever>(),
            sp.GetRequiredService<IIngestor>(),
            sp.GetService<IAnswerEngine>()));

        return services;
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~ServiceCollectionExtensionsTests" --no-restore`
Expected: PASS

**Step 5: Run ALL tests to check for regressions**

Run: `dotnet test tests/Rag.NET.Tests --no-restore`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs tests/Rag.NET.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
git commit -m "refactor: wire decorator chain in ServiceCollectionExtensions"
```

---

### Task 15: Clean up unused code and run full test suite

Remove any dead code left behind by the refactoring.

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs` (remove unused imports if any)
- Verify: all projects in solution compile and all tests pass

**Step 1: Remove old unused usings from `RagBuilder.cs`**

Check if `RagBuilder.cs` still imports `Rag.NET.Pipeline` — it no longer references `RagPipeline` directly. Remove the import if unused.

**Step 2: Build the entire solution**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeds with no errors.

**Step 3: Run all tests across all test projects**

Run: `dotnet test Rag.NET.slnx --no-restore`
Expected: All tests pass.

**Step 4: Commit any cleanup**

```bash
git add -u
git commit -m "chore: clean up unused imports after decorator refactoring"
```

---

## Summary

| Phase | Tasks | What it achieves |
|-------|-------|-----------------|
| Phase 1 (Tasks 1-9) | Record conversion, interfaces, base implementations, coordinator rewrite | `RagPipeline` goes from 11 params to 3 |
| Phase 2 (Tasks 10-13) | Four retrieval decorators | Each feature is independently testable and removable |
| Phase 3 (Tasks 14-15) | DI wiring, cleanup | External API unchanged, internal composition via decorators |
