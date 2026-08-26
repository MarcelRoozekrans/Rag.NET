---
id: architecture
title: Architecture
sidebar_position: 1
---

# Architecture

Understanding the internal structure of Rag.NET helps you choose the right extension points and diagnose unexpected behaviour. The library is built around three internal interfaces — `IRetriever`, `IIngestor`, `IAnswerEngine` — assembled as behavior pipelines and exposed through a single public facade, `IRagPipeline`.

## Data flow

### Ingestion path

```mermaid
flowchart TD
    A["Stream + DocumentMetadata"] --> B["IDocumentParser.ParseAsync()"]
    B --> C["DocumentSection[ ]<br>text · heading · page · section index"]
    C --> D["IChunkingStrategy.ChunkAsync()"]
    D --> E["TextChunk[ ]<br>text · DocumentId · ChunkIndex · Metadata"]
    E --> F["Apply heading breadcrumbs<br>& DocumentMetadata.Tags"]
    F --> G["IEmbeddingGenerator.GenerateAsync()"]
    G --> H["EmbeddedChunk[ ]<br>TextChunk + ReadOnlyMemory&lt;float&gt;"]
    H --> I["IVectorStore.StoreAsync()"]
    H --> J["InMemoryBm25Index.Add()<br>hybrid search fallback"]
    E --> K["InMemoryParentChunkStore.Add()<br>parent-document retrieval (optional)"]

    style J fill:#e8f4fd,stroke:#4a90d9
    style K fill:#e8f4fd,stroke:#4a90d9
```

### Retrieval path

```mermaid
flowchart TD
    Q["string query"] --> EMB["IEmbeddingGenerator.GenerateAsync()"]

    EMB --> HYBRID_CHECK{UseHybridSearch?}

    HYBRID_CHECK -- "yes + IHybridSearchable" --> NATIVE["IHybridSearchable.HybridSearchAsync()"]
    HYBRID_CHECK -- "yes + fallback" --> DENSE["IVectorStore.SearchAsync()"]
    HYBRID_CHECK -- "yes + fallback" --> BM25["InMemoryBm25Index.Search()"]
    DENSE --> RRF["RrfMerger.Merge()<br>Reciprocal Rank Fusion"]
    BM25 --> RRF
    HYBRID_CHECK -- no --> SEMANTIC["IVectorStore.SearchAsync()"]

    NATIVE --> POST
    RRF --> POST
    SEMANTIC --> POST

    POST["post-retrieval"] --> HYDE["[optional]<br>HydeRetriever<br>embed hypothetical doc"]
    HYDE --> REDUN["[optional]<br>RedundancyFilter.FilterAsync()"]
    REDUN --> RERANK["[optional]<br>IReranker.RerankAsync()"]
    RERANK --> LITM["[optional]<br>LostInTheMiddleReorderer.Reorder()"]
    LITM --> RESULT["IReadOnlyList&lt;SearchResult&gt;"]

    style BM25 fill:#e8f4fd,stroke:#4a90d9
    style RRF fill:#e8f4fd,stroke:#4a90d9
    style RERANK fill:#e8f4fd,stroke:#4a90d9
```

### Ask path

```mermaid
flowchart TD
    R["IReadOnlyList&lt;SearchResult&gt;"] --> PROMPT["Build prompt<br>SystemMessage · ConversationHistory · Context + Query"]
    PROMPT --> CHAT["IChatClient"]
    CHAT -- GetResponseAsync --> RESP["RagResponse<br>Answer + Sources"]
    CHAT -- GetStreamingResponseAsync --> STREAM["IAsyncEnumerable&lt;RagStreamingUpdate&gt;<br>Sources first · TextDelta stream"]
```

## Core interfaces

### `IRagPipeline`

The single public entry point that application code should depend on.

```csharp
public interface IRagPipeline
{
    Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
```

### `IVectorStore`

Implemented by each vector store package. Stores embedded chunks and performs dense ANN search.

```csharp
public interface IVectorStore
{
    Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default);
}
```

### `IHybridSearchable`

Optional interface that vector stores may implement to provide native hybrid search (e.g., Azure AI Search or Weaviate BM25 + vector). When a store implements both `IVectorStore` and `IHybridSearchable`, the pipeline uses `HybridSearchAsync` directly instead of the in-memory BM25 fallback.

```csharp
public interface IHybridSearchable
{
    Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);
}
```

### `IDocumentParser`

```csharp
public interface IDocumentParser
{
    bool CanParse(string contentType);
    IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default);
}
```

Multiple parsers can be registered. The pipeline calls `CanParse` on each in registration order and uses the first match.

### `IChunkingStrategy`

```csharp
public interface IChunkingStrategy
{
    IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
```

### `ICollectionManageable`

Optional interface for vector stores that support programmatic index/collection lifecycle management.

```csharp
public interface ICollectionManageable
{
    Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default);
    Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default);
}
```

`AzureAISearchVectorStore` implements this interface.

## Core models

| Type | Purpose |
|------|---------|
| `DocumentMetadata` | Input descriptor: `DocumentId`, `FileName`, `ContentType`, `Tags` |
| `DocumentSection` | Parser output: `Text`, `DocumentId`, optional `HeadingLevel`, `Heading`, `PageNumber`, `SectionIndex` |
| `TextChunk` | Chunker output: `Text`, `DocumentId`, `ChunkIndex`, `StartPosition`, `EndPosition`, `Metadata` |
| `EmbeddedChunk` | Internal: `TextChunk` + `ReadOnlyMemory<float>` embedding |
| `SearchResult` | Retrieval output: `TextChunk` + `double Score` |
| `IngestionResult` | `IngestAsync` return: `DocumentId` + `ChunksStored` |
| `RagResponse` | `AskAsync` return: `string Answer` + `IReadOnlyList<SearchResult> Sources` |
| `RerankResult` | Reranker output: `SearchResult` + `double RelevanceScore` |
| `RagStreamingUpdate` | `AskStreamingAsync` yield: `string? TextDelta` + `IReadOnlyList<SearchResult>? Sources` |

## Internal architecture — behavior pipeline

`RagPipeline` is a thin coordinator with three constructor parameters, each resolved from DI:

```csharp
public sealed class RagPipeline(
    IRetriever retriever,
    IIngestor ingestor,
    IAnswerEngine? answerEngine = null) : IRagPipeline
```

Each method delegates directly to the appropriate interface. `IAnswerEngine` is optional — `AskAsync` and `AskStreamingAsync` throw `InvalidOperationException` if no `IChatClient` is registered.

### Internal interfaces

| Interface | Implementation | Responsibility |
|-----------|----------------|----------------|
| `IRetriever` | `PipelineRetriever` | Run retrieval behavior chain → results |
| `IIngestor` | `PipelineIngestor` | Run ingestion behavior chain → store chunks |
| `IAnswerEngine` | `ChatAnswerEngine` | Build prompt from sources → call `IChatClient` |

### Ingestion behavior chain

Ingestion is handled by `PipelineIngestor`, which executes a fixed sequence of singleton behaviors assembled by `IngestionPipelineBuilder`. Each behavior owns its own injected services and receives a lean `IngestionContext` carrying only runtime inputs and accumulated state:

```
OverwriteBehavior
  → ParseBehavior
    → ChunkingBehavior
      → MetadataBehavior
        → ParentDocumentIngestionBehavior   (present when UseParentDocumentRetrieval() called)
          → EmbeddingBehavior
            → StorageBehavior
```

### Retrieval behavior chain

Retrieval is handled by `PipelineRetriever`, which executes a sequence of singleton behaviors assembled by `RetrievalPipelineBuilder`. Each behavior checks per-call flags on `RetrievalOptions` and either applies its logic or passes through to the next behavior:

```
ResultCacheBehavior              (present when UseCaching() called)
  → LostInTheMiddleBehavior      (always present)
    → MmrBehavior                (always present)
      → RedundancyFilterBehavior (always present)
        → ParentDocumentRetrievalBehavior  (present when UseParentDocumentRetrieval() called)
          → RerankingBehavior    (present when IReranker registered)
            → MultiQueryBehavior (present when IQueryExpander registered)
              → HydeBehavior     (present when IHypotheticalDocumentGenerator registered)
                → EmbeddingCacheBehavior  (present when UseCaching() called)
                  → FilterBehavior        (always present — applies RetrievalOptions.Filter spec)
                    → VectorStoreBehavior (base — always present)
```

Behaviors catch non-cancellation exceptions and fall back gracefully. `RetrievalOptions` is a `sealed record` so behaviors can use `with` expressions to modify options (e.g., over-fetch `TopK`) without mutating the caller's instance.

## In-memory BM25 index

`InMemoryBm25Index` is a DI singleton (BM25 parameters: k1=1.5, b=0.75) shared between `StorageBehavior` (add/remove during ingestion) and `VectorStoreBehavior` (search during retrieval). Every chunk stored via `IngestAsync` is also added to this index. When `UseHybridSearch = true` and the vector store does not implement `IHybridSearchable`, the retrieval behavior queries both the dense index and the BM25 index concurrently and merges results using Reciprocal Rank Fusion (k=60).

`InMemoryBm25Index` is process-scoped and not persisted — and nothing rebuilds it. It starts empty on every application start and fills only from `IngestAsync` calls made in that same process, so re-running ingestion is what repopulates it. If you need a BM25 index that survives a restart, use `SqliteBm25Index` (`Rag.NET.Storage.Sqlite`), which wraps this type write-through and does load its persisted rows on first use, or a vector store that natively implements `IHybridSearchable`.

`InMemoryParentChunkStore` follows the same lifecycle: a DI singleton populated during ingestion and lost on restart. It must be rebuilt by re-running ingestion after each application start.

## DI wiring

See [Getting Started](getting-started.md) for the call sequence. Internally, `ServiceCollectionExtensions.AddRagNet`:

1. Registers `TextDocumentParser` and `MarkdownDocumentParser` as built-in parsers.
2. Registers `RecursiveChunkingStrategy` as the default `IChunkingStrategy` (unless overridden).
3. Registers default `ChunkingOptions` (MaxChunkSize=512, Overlap=50).
4. Registers `InMemoryBm25Index` and `InMemoryParentChunkStore` as singletons.
5. Accepts optional `ingestion:` and `retrieval:` builder callbacks for pipeline extensibility.
6. Assembles the `IIngestor` behavior chain via `IngestionPipelineBuilder` and the `IRetriever` behavior chain via `RetrievalPipelineBuilder`, registering the resulting `PipelineIngestor` and `PipelineRetriever`.
7. Registers `IRagPipeline` (`RagPipeline`).
8. Runs the user-supplied `Action<RagBuilder>` for additional configuration.

### Named pipelines

One container can hold several isolated pipelines, each with its own stores:

```csharp
services.AddRagNetShared(rag => rag.UseOnnxEmbeddings(o => { ... }));   // one model, shared
services.AddRagNet("docs",    rag => rag.UseAzureAISearch(endpoint, "docs-index",    credential));
services.AddRagNet("support", rag => rag.UseAzureAISearch(endpoint, "support-index", credential));

var docs = provider.GetRequiredService<IRagPipelineFactory>().Get("docs");
```

Each name gets its own vector store, BM25 index, caches and behaviours, built lazily on first
`Get(name)` — a misconfigured named pipeline surfaces there, not when the container is built.
Services declared in `AddRagNetShared` stay singular across every name: four types hold an ONNX
`InferenceSession` — the embedding generator, the token embedding generator, the SPLADE encoder
and the reranker — and MiniLM alone is roughly 90 MB, so one instance per pipeline would load the
same model repeatedly.

**The inherited and shared services are built on the first `Get(name)`, not before.** Both are
resolved from the root provider just before that name's child is built, so `Contains(name)` and
resolving `IRagPipelineFactory` itself construct nothing. An instance is the only shape that keeps
ownership with the root — a factory descriptor would have the first child disposed dispose something
it does not own — and resolving to get that instance is why the timing matters.

They were resolved earlier, while `IRagPipelineFactory` itself was being constructed, until #396.
That re-entered the container for a service still under construction, and **a host singleton that
depends on `IRagPipelineFactory` deadlocked outright** — reported as a Blazor app that hung at
startup and worked as a console app. Deferring to `Get(name)` means the same registration resolves
after this factory exists, which is both correct and cheaper. A container with `AddRagNetShared(rag => rag.UseOnnxEmbeddings(...))` loads the
ONNX model on the first `IRagPipelineFactory` resolution, not on the first `Get(name)` that would
actually use it.

**What a named pipeline can see, in precedence order.** Its own block wins; a type declared in
`AddRagNetShared` replaces that, because declaring a type shared says "one of these for every
pipeline"; and the host's own **singleton** registrations on the `IServiceCollection` are inherited
as a default wherever the pipeline registered nothing. That last rule is what makes the canonical
registration work:

```csharp
services.AddChatClient(chatClient);              // on the collection, as Microsoft.Extensions.AI
services.AddEmbeddingGenerator(embeddingGenerator); // documents it
services.AddRagNet("abc", rag => rag.UseAzureAISearch(endpoint, "abc-index", credential));
```

Before this rule existed the named form saw *less* than `AddRagNet(rag => …)` for identical
registrations, because the unnamed form registers into the caller's own collection while a named
one builds a child container (#390).

Isolation is unaffected: a child collection is built by `AddRagNet(configure)` and so already
registers every service type this library owns, which means another pipeline's store, chunker or
behaviours can never arrive this way.

**Named pipelines still run with logging off, but not because nothing is inherited.**
`ILoggerFactory` is a closed-type singleton and *is* inherited. `ILogger<T>` is not: it comes from
the open generic `ILogger<>`, and open generics are skipped (see the next paragraph). Every Rag.NET
component resolves logging optionally (`sp.GetService<ILogger<T>>()`), so this fails silently rather
than throwing — a named pipeline logs nothing where the unnamed one logs normally.

**Only singleton, non-keyed, closed-generic registrations cross into a child**, whether they come
from `AddRagNetShared` or from the host's collection. A transient, a scoped registration, a keyed
one, or an open generic like `IOptions<>` or `ILogger<>` stays in the root and is never reachable
from a named pipeline's provider. A named pipeline that needs one of those must register it in its
own block instead. Forwarding also
resolves *every* root registration for a shared type, not just the last one: declare a
multi-registered type such as `IDocumentParser` shared, and a child sees the root's instances in
place of its own rather than adding to them. Not everything a shared block touches is a shared-block
registration, either — `UseMediator()` registers `IngestCommandHandler`, `RetrieveQueryHandler` and
`DeleteCommandHandler` as transients (`MediatorBuilderExtensions`), so calling it inside
`AddRagNetShared` still leaves those three in the root, unforwarded, same as if it had been called
inside `AddRagNet` directly.

**`AddRagNet(rag => ...)` is unchanged.** It still registers into the root container and its
pipeline is still resolved with `GetRequiredService<IRagPipeline>()`, exactly as described above
and in [Getting Started](../getting-started.md). Named pipelines are additive; the two forms
coexist in one container, and a caller who never wants a second pipeline never has to meet
`IRagPipelineFactory` at all.

**Why not a metadata filter?** `RetrievalOptions.MetadataFilter` is caller-supplied per query, so
forgetting it once is a cross-read between indexes. An index binding on a named pipeline cannot be
forgotten the same way — isolation lives in the registration instead of in every call site.

This is isolation by registration, not a security boundary: both pipelines still run in the same
process and the same root container.

Referencing `Rag.NET` now brings in the concrete `Microsoft.Extensions.DependencyInjection`
package transitively, not just `.Abstractions` — building a named pipeline's child provider
requires the implementation, not just the interfaces. Consumers wiring `Rag.NET` into a
third-party DI container should be aware their dependency graph now includes it.
