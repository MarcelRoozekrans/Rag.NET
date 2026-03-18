# RAG Pipeline — ZeroAlloc.Pipeline Refactor Design

**Date:** 2026-03-18
**Status:** Approved

---

## Goal

Replace the runtime OOP decorator chains for retrieval and ingestion with source-generated static lambda chains via `ZeroAlloc.Pipeline`. The public `IRagPipeline` API is unchanged. Benefits: zero-allocation hot path, explicit compile-time ordering, independently testable behaviors.

---

## Architecture

Two source-generated pipelines sit behind the existing internal interfaces:

```
IRagPipeline (public — unchanged)
  ├── IIngestor  ← PipelineIngestor  (thin DI facade)
  │                   └── IngestionChain  (source-generated)
  │                         OverwriteBehavior      (Order=10)
  │                         ParseBehavior          (Order=20)
  │                         ChunkingBehavior       (Order=30)
  │                         MetadataBehavior       (Order=40)
  │                         ParentDocumentBehavior (Order=50)
  │                         EmbeddingBehavior      (Order=60)
  │                         StorageBehavior        (Order=70)
  │
  └── IRetriever ← PipelineRetriever (thin DI facade)
                      └── RetrievalChain  (source-generated)
                            ResultCacheBehavior      (Order=10)
                            LostInTheMiddleBehavior  (Order=20)
                            RedundancyFilterBehavior (Order=30)
                            ParentDocumentBehavior   (Order=40)
                            RerankingBehavior        (Order=50)
                            MultiQueryBehavior       (Order=60)
                            HydeBehavior             (Order=70)
                            EmbeddingCacheBehavior   (Order=80)
                            VectorStoreBehavior      (Order=90)
```

`PipelineIngestor` replaces `DocumentIngestor`. `PipelineRetriever` replaces the nested decorator factory. Both implement the same `IIngestor` / `IRetriever` interfaces — `RagPipeline`, `ServiceCollectionExtensions`, and all existing tests require minimal changes.

---

## NuGet Dependencies

Add to `src/Rag.NET/Rag.NET.csproj`:

```xml
<PackageReference Include="ZeroAlloc.Pipeline" Version="*" />
<PackageReference Include="ZeroAlloc.Pipeline.Generators" Version="*">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
</PackageReference>
```

---

## Context Models

### IngestionContext

```csharp
public sealed class IngestionContext
{
    // Input
    public required Stream Stream                                            { get; init; }
    public required DocumentMetadata Metadata                               { get; init; }
    public IngestionOptions? Options                                        { get; init; }
    public IProgress<IngestionProgress>? Progress                          { get; init; }

    // Accumulated state (mutated as chain progresses)
    public List<DocumentSection> Sections  { get; } = [];
    public List<TextChunk> Chunks          { get; } = [];
    public List<EmbeddedChunk> EmbeddedChunks { get; } = [];

    // Services — required
    public required IEnumerable<IDocumentParser> Parsers                   { get; init; }
    public required IChunkingStrategy ChunkingStrategy                     { get; init; }
    public required ChunkingOptions ChunkingOptions                        { get; init; }
    public required IVectorStore VectorStore                               { get; init; }
    public required IEmbeddingGenerator<string, Embedding<float>> Embedder { get; init; }
    public required IBm25Index Bm25Index                                   { get; init; }

    // Services — optional
    public IParentChunkStore? ParentStore   { get; init; }
    public ParentDocumentOptions? ParentOptions { get; init; }
    public IRagDataManager? DataManager     { get; init; }
}
```

### RetrievalContext

```csharp
public sealed class RetrievalContext
{
    // Input
    public required string Query           { get; init; }
    public required RetrievalOptions Options { get; init; }

    // Services — required
    public required IVectorStore VectorStore                               { get; init; }
    public required IEmbeddingGenerator<string, Embedding<float>> Embedder { get; init; }
    public required IBm25Index Bm25Index                                   { get; init; }

    // Services — optional
    public IReranker? Reranker                             { get; init; }
    public IQueryExpander? QueryExpander                   { get; init; }
    public IHypotheticalDocumentGenerator? HydeGenerator   { get; init; }
    public IParentChunkStore? ParentStore                  { get; init; }
    public IEmbeddingCache? EmbeddingCache                 { get; init; }
    public IResultCache? ResultCache                       { get; init; }
}
```

Services are resolved once from DI by the facade before invoking the generated chain. Each behavior uses only the fields it needs; optional services follow the existing `ctx.X is null → skip` pattern.

---

## Behavior Definitions

### Ingestion Behaviors

Located in `src/Rag.NET/Ingestion/Behaviors/`.

| Behavior | Order | What it does |
|----------|-------|--------------|
| `OverwriteBehavior` | 10 | If `Options.Overwrite`: deletes from vector store, BM25, data manager before proceeding |
| `ParseBehavior` | 20 | Runs `IDocumentParser.ParseAsync` → populates `ctx.Sections`; reports `Parsing` progress |
| `ChunkingBehavior` | 30 | Runs `IChunkingStrategy.ChunkAsync` per section → populates `ctx.Chunks`; builds heading breadcrumb hierarchy |
| `MetadataBehavior` | 40 | Applies `DocumentMetadata.Tags` + `document_id` / `file_name` metadata to all chunks |
| `ParentDocumentBehavior` | 50 | If `ParentStore != null`: resets stream, re-parses with parent options, stores parent chunks, sets `_parentKey` metadata on child chunks |
| `EmbeddingBehavior` | 60 | Batch-embeds `ctx.Chunks` → populates `ctx.EmbeddedChunks`; reports `Embedding` progress |
| `StorageBehavior` | 70 | Stores to vector store + BM25 + data manager; reports `Storing` progress; **terminal — returns `IngestionResult`** |

### Retrieval Behaviors

Located in `src/Rag.NET/Retrieval/Behaviors/`.

| Behavior | Order | What it does |
|----------|-------|--------------|
| `ResultCacheBehavior` | 10 | Short-circuits with cached result if `UseCacheResult` and cache hit |
| `LostInTheMiddleBehavior` | 20 | Reorders results after inner chain to avoid lost-in-the-middle degradation |
| `RedundancyFilterBehavior` | 30 | Removes near-duplicate results above `RedundancyThreshold` |
| `ParentDocumentBehavior` | 40 | Replaces child chunks with their parent chunks from `IParentChunkStore` |
| `RerankingBehavior` | 50 | Re-scores results with `IReranker` if `UseReranking` |
| `MultiQueryBehavior` | 60 | Expands query via `IQueryExpander`, runs sub-queries in parallel, merges via RRF |
| `HydeBehavior` | 70 | Generates hypothetical document via `IHypotheticalDocumentGenerator`, uses it as embedding input |
| `EmbeddingCacheBehavior` | 80 | Caches query embedding via `IEmbeddingCache` |
| `VectorStoreBehavior` | 90 | Embeds query, calls vector store + BM25 (RRF merge if hybrid), **terminal — returns `IReadOnlyList<SearchResult>`** |

---

## Facade Implementations

### PipelineIngestor

```csharp
public sealed class PipelineIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ChunkingOptions chunkingOptions,
    IBm25Index bm25Index,
    IParentChunkStore? parentStore = null,
    ParentDocumentOptions? parentOptions = null,
    IRagDataManager? dataManager = null) : IIngestor
{
    public Task<IngestionResult> IngestAsync(
        Stream document, DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new IngestionContext
        {
            Stream = document, Metadata = metadata,
            Options = options, Progress = progress,
            Parsers = parsers, ChunkingStrategy = chunkingStrategy,
            ChunkingOptions = chunkingOptions, VectorStore = vectorStore,
            Embedder = embedder, Bm25Index = bm25Index,
            ParentStore = parentStore, ParentOptions = parentOptions,
            DataManager = dataManager,
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

### PipelineRetriever

```csharp
public sealed class PipelineRetriever(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IBm25Index bm25Index,
    IReranker? reranker = null,
    IQueryExpander? queryExpander = null,
    IHypotheticalDocumentGenerator? hydeGenerator = null,
    IParentChunkStore? parentStore = null,
    IEmbeddingCache? embeddingCache = null,
    IResultCache? resultCache = null) : IRetriever
{
    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query, RetrievalOptions? options, CancellationToken cancellationToken = default)
    {
        var ctx = new RetrievalContext
        {
            Query = query, Options = options ?? RetrievalOptions.Default,
            VectorStore = vectorStore, Embedder = embedder,
            Bm25Index = bm25Index, Reranker = reranker,
            QueryExpander = queryExpander, HydeGenerator = hydeGenerator,
            ParentStore = parentStore, EmbeddingCache = embeddingCache,
            ResultCache = resultCache,
        };
        return RetrievalChain.ExecuteAsync(ctx, cancellationToken).AsTask();
    }
}
```

---

## DI Registration Changes

`ServiceCollectionExtensions` swaps the two factory registrations:

```csharp
// Replace the nested decorator factory for IRetriever with:
services.AddSingleton<IRetriever, PipelineRetriever>();

// Replace DocumentIngestor factory with:
services.AddSingleton<IIngestor, PipelineIngestor>();
```

All `RagBuilder` fluent methods (`UseReranking<T>()`, `UseParentDocumentRetrieval()`, etc.) are unchanged — they still register the optional services that the facades pick up via `sp.GetService<T>()`.

---

## Error Handling

- `ParseBehavior` throws `NotSupportedException` if no parser matches `ContentType` — same as current `DocumentIngestor`.
- `ParentDocumentBehavior` (ingestion) throws `InvalidOperationException` if stream is non-seekable — same guard as current code.
- All async exceptions propagate naturally through the generated chain.
- `StorageBehavior` and `VectorStoreBehavior` are terminal — neither calls `next()`. If the chain is misconfigured without a terminal behavior, the generated code will produce a compile-time diagnostic via `PipelineDiagnosticRules`.

---

## Testing

### Existing tests

- `DocumentIngestorTests` → ported 1-to-1 to `PipelineIngestorTests` (same structure, swap class name)
- Retriever tests → ported to `PipelineRetrieverTests`

### New: per-behavior unit tests

Each behavior is testable in isolation by calling `Handle` directly — no DI, no pipeline setup:

```csharp
// Example: RerankingBehavior passthrough when reranker is null
var ctx = new RetrievalContext { ..., Reranker = null, Options = new() { UseReranking = true } };
var result = await RerankingBehavior.Handle(ctx, ct,
    (c, t) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(fakeResults));
Assert.Equal(fakeResults, result);

// Example: RerankingBehavior calls reranker when present
ctx = ctx with { Reranker = mockReranker };
result = await RerankingBehavior.Handle(ctx, ct, ...);
mockReranker.Received(1).RerankAsync(...);
```

---

## Files

| Action | Path |
|--------|------|
| Add packages | `src/Rag.NET/Rag.NET.csproj` |
| Create | `src/Rag.NET/Ingestion/IngestionContext.cs` |
| Create | `src/Rag.NET/Retrieval/RetrievalContext.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/OverwriteBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/ChunkingBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/ParentDocumentIngestionBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs` |
| Create | `src/Rag.NET/Ingestion/PipelineIngestor.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/ResultCacheBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/LostInTheMiddleBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/RedundancyFilterBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/ParentDocumentRetrievalBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/RerankingBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/MultiQueryBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/HydeBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs` |
| Create | `src/Rag.NET/Retrieval/PipelineRetriever.cs` |
| Delete | `src/Rag.NET/Ingestion/DocumentIngestor.cs` |
| Delete | `src/Rag.NET/Retrieval/Decorators/` (all decorator files) |
| Modify | `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` |
| Create | `tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs` |
| Create | `tests/Rag.NET.Tests/Ingestion/Behaviors/` (per-behavior tests) |
| Create | `tests/Rag.NET.Tests/Retrieval/PipelineRetrieverTests.cs` |
| Create | `tests/Rag.NET.Tests/Retrieval/Behaviors/` (per-behavior tests) |
| Delete | `tests/Rag.NET.Tests/Ingestion/DocumentIngestorTests.cs` |
