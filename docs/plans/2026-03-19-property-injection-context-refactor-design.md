# RAG Pipeline — Property Injection & Context Refactor Design

**Date:** 2026-03-19
**Status:** Approved
**Amends:** [2026-03-18-ragpipeline-zeropipeline-design.md](2026-03-18-ragpipeline-zeropipeline-design.md)

---

## Goal

Eliminate context bloat and facade constructor bloat introduced by the ZeroAlloc.Pipeline design. Use `ZeroAlloc.Inject` property injection to give each pipeline behavior ownership of its own service dependencies. Contexts become pure data objects (runtime inputs + accumulated state only). Also introduce a first-class extensibility model so consumers can add, replace, and carry custom state through the pipeline.

---

## Problem

The approved zeropipeline design puts all service references on the context (`IngestionContext` carries ~11 services). This causes two bloat problems:

1. **Facade constructor bloat** — `PipelineIngestor` and `PipelineRetriever` need large constructor parameter lists to assemble the context on every call.
2. **Context bloat** — every behavior receives a god-object context containing services it does not use.

---

## Architecture

Behaviors are DI-registered singletons decorated with `[Inject]` on the service properties they specifically need. The context carries only per-request runtime inputs and accumulated state — no service references.

```
PipelineIngestor  [Singleton via [Inject]]
  Pipeline<IngestionContext>  ← injected
    OverwriteBehavior      [Singleton, [Inject]: IVectorStore, IBm25Index, IRagDataManager?]
    ParseBehavior          [Singleton, [Inject]: IEnumerable<IDocumentParser>]
    ChunkingBehavior       [Singleton, [Inject]: IChunkingStrategy, ChunkingOptions]
    MetadataBehavior       [Singleton, no services]
    ParentDocumentBehavior [Singleton, [Inject]: IParentChunkStore?, ParentDocumentOptions?]
    EmbeddingBehavior      [Singleton, [Inject]: IEmbeddingGenerator<string,Embedding<float>>]
    StorageBehavior        [Singleton, [Inject]: IVectorStore, IBm25Index, IRagDataManager?]

PipelineRetriever  [Singleton via [Inject]]
  Pipeline<RetrievalContext>  ← injected
    ResultCacheBehavior      [Singleton, [Inject]: IResultCache?]
    LostInTheMiddleBehavior  [Singleton, no services]
    RedundancyFilterBehavior [Singleton, no services]
    ParentDocumentBehavior   [Singleton, [Inject]: IParentChunkStore?]
    RerankingBehavior        [Singleton, [Inject]: IReranker?]
    MultiQueryBehavior       [Singleton, [Inject]: IQueryExpander?]
    HydeBehavior             [Singleton, [Inject]: IHypotheticalDocumentGenerator?]
    EmbeddingCacheBehavior   [Singleton, [Inject]: IEmbeddingCache?]
    VectorStoreBehavior      [Singleton, [Inject]: IVectorStore, IBm25Index,
                                                   IEmbeddingGenerator<string,Embedding<float>>]
```

The `IngestionServices` / `RetrievalServices` bags proposed during brainstorming are **not needed** — behaviors own their own dependencies directly.

---

## Context Models

### IngestionContext

Services are removed entirely. Behaviors access their dependencies through their own injected properties.

```csharp
public sealed class IngestionContext
{
    // Runtime inputs
    public required Stream Stream                   { get; init; }
    public required DocumentMetadata Metadata       { get; init; }
    public IngestionOptions? Options                { get; init; }
    public IProgress<IngestionProgress>? Progress  { get; init; }

    // Accumulated state (mutated as chain progresses)
    public List<DocumentSection> Sections          { get; } = [];
    public List<TextChunk> Chunks                  { get; } = [];
    public List<EmbeddedChunk> EmbeddedChunks      { get; } = [];

    // Extension bag for custom behaviors
    public Dictionary<string, object?> Extensions  { get; } = new();
}
```

### RetrievalContext

```csharp
public sealed class RetrievalContext
{
    // Runtime inputs
    public required string Query                   { get; init; }
    public required RetrievalOptions Options       { get; init; }

    // Accumulated state
    public List<SearchResult> Results              { get; } = [];

    // Extension bag for custom behaviors
    public Dictionary<string, object?> Extensions  { get; } = new();
}
```

---

## Behavior Shape

Each behavior is a singleton with `[Inject]` on the properties it needs. Optional services use `[Inject(Optional = true)]`.

```csharp
[Singleton]
public sealed class EmbeddingBehavior : IBehavior<IngestionContext>
{
    [Inject]
    public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask HandleAsync(
        IngestionContext ctx,
        PipelineDelegate<IngestionContext> next,
        CancellationToken ct)
    {
        // uses Embedder directly — no ctx.Services indirection
        ctx.EmbeddedChunks.AddRange(await Embedder.EmbedAsync(ctx.Chunks, ct));
        await next(ctx, ct);
    }
}

[Singleton]
public sealed class RerankingBehavior : IBehavior<RetrievalContext>
{
    [Inject(Optional = true)]
    public IReranker? Reranker { get; set; }

    public async ValueTask HandleAsync(
        RetrievalContext ctx,
        PipelineDelegate<RetrievalContext> next,
        CancellationToken ct)
    {
        await next(ctx, ct);
        if (Reranker is not null && ctx.Options.UseReranking)
            ctx.Results = await Reranker.RerankAsync(ctx.Query, ctx.Results, ct);
    }
}
```

---

## Facade Implementations

Facades shrink to a single injected pipeline. No manual service copying.

```csharp
[Singleton(As = typeof(IIngestor))]
public sealed class PipelineIngestor : IIngestor
{
    [Inject]
    public Pipeline<IngestionContext> Pipeline { get; set; } = null!;

    public Task<IngestionResult> IngestAsync(
        Stream stream,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ctx = new IngestionContext
        {
            Stream = stream, Metadata = metadata,
            Options = options, Progress = progress,
        };
        return Pipeline.ExecuteAsync(ctx, ct).AsTask();
    }
}

[Singleton(As = typeof(IRetriever))]
public sealed class PipelineRetriever : IRetriever
{
    [Inject]
    public Pipeline<RetrievalContext> Pipeline { get; set; } = null!;

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options,
        CancellationToken ct = default)
    {
        var ctx = new RetrievalContext
        {
            Query = query,
            Options = options ?? RetrievalOptions.Default,
        };
        return Pipeline.ExecuteAsync(ctx, ct).AsTask();
    }
}
```

---

## Extensibility Model

Consumers can extend the pipeline in three ways without forking the library.

### 1. Add a custom behavior at a specific position

```csharp
services.AddRagNETServices(
    ingestion: b => b.Add<ContentFilterBehavior>(after: typeof(ParseBehavior)),
    retrieval:  b => b.Add<QueryAuditBehavior>(before: typeof(VectorStoreBehavior))
);
```

`IngestionPipelineBuilder` and `RetrievalPipelineBuilder` hold an ordered list of `Type` entries. `AddRagNETServices` populates defaults; the consumer callback mutates the list; `Pipeline<T>` is built from the final order.

Custom behaviors use constructor injection or their own `[Inject]` — the library imposes no constraint.

### 2. Replace a built-in behavior

```csharp
services.AddRagNETServices(
    ingestion: b => b.Replace<EmbeddingBehavior, GpuEmbeddingBehavior>()
);
```

`Replace<TOld, TNew>()` swaps the type at the same position in the ordered list. `TNew` must implement `IBehavior<IngestionContext>`.

### 3. Custom state in the extension bag

```csharp
// Custom behavior — write
public sealed class EnrichmentBehavior : IBehavior<IngestionContext>
{
    public async ValueTask HandleAsync(IngestionContext ctx, PipelineDelegate<IngestionContext> next, CancellationToken ct)
    {
        ctx.Extensions["enrichment:score"] = ComputeScore(ctx.Metadata);
        await next(ctx, ct);
    }
}

// Downstream behavior — read
var score = ctx.Extensions.TryGetValue("enrichment:score", out var v) ? (double)v! : 0.0;
```

Pattern mirrors `HttpContext.Items` — no changes to the context class required.

---

## DI Registration Changes

`ServiceCollectionExtensions.AddRagNETServices` gains two optional builder callbacks and changes its registrations:

```csharp
public static RagBuilder AddRagNETServices(
    this IServiceCollection services,
    Action<IngestionPipelineBuilder>? ingestion = null,
    Action<RetrievalPipelineBuilder>? retrieval = null)
{
    var ingestionBuilder = new IngestionPipelineBuilder(); // pre-populated with defaults
    ingestion?.Invoke(ingestionBuilder);
    services.AddSingleton(ingestionBuilder.Build());       // Pipeline<IngestionContext>

    var retrievalBuilder = new RetrievalPipelineBuilder();
    retrieval?.Invoke(retrievalBuilder);
    services.AddSingleton(retrievalBuilder.Build());       // Pipeline<RetrievalContext>

    // Behaviors are registered as singletons (auto-wired by ZeroAlloc.Inject)
    // Facades are registered via [Singleton(As = ...)] attribute
}
```

The existing manual 9-param factory lambda for `IIngestor` and `BuildRetrieverChain` helper are **deleted**.

---

## Testing

Per-behavior unit tests are now simpler — instantiate the behavior, set its injected properties directly (no DI, no context services):

```csharp
var behavior = new RerankingBehavior { Reranker = mockReranker };
var ctx = new RetrievalContext
{
    Query = "test", Options = new() { UseReranking = true },
};
ctx.Results.AddRange(fakeResults);
await behavior.HandleAsync(ctx, (c, t) => ValueTask.CompletedTask, CancellationToken.None);
mockReranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
```

Custom behavior extensibility is tested via the pipeline builder:

```csharp
var builder = new IngestionPipelineBuilder();
builder.Add<NoOpBehavior>(after: typeof(ParseBehavior));
var pipeline = builder.Build();
Assert.Contains(typeof(NoOpBehavior), pipeline.BehaviorTypes);
```

---

## Files Changed (delta from zeropipeline design)

| Action | Path | Change |
|--------|------|--------|
| Modify | `src/Rag.NET/Ingestion/IngestionContext.cs` | Remove all service properties; add `Extensions` dictionary |
| Modify | `src/Rag.NET/Retrieval/RetrievalContext.cs` | Remove all service properties; add `Extensions` dictionary; add `Results` list |
| Modify | `src/Rag.NET/Ingestion/PipelineIngestor.cs` | Replace constructor params with `[Inject] Pipeline<IngestionContext>` |
| Modify | `src/Rag.NET/Retrieval/PipelineRetriever.cs` | Replace constructor params with `[Inject] Pipeline<RetrievalContext>` |
| Modify | `src/Rag.NET/Ingestion/Behaviors/*.cs` | Add `[Singleton]` + `[Inject]` service properties; remove ctx.X service access |
| Modify | `src/Rag.NET/Retrieval/Behaviors/*.cs` | Add `[Singleton]` + `[Inject]` service properties; remove ctx.X service access |
| Create | `src/Rag.NET/DependencyInjection/IngestionPipelineBuilder.cs` | Ordered behavior list with `Add<T>` / `Replace<TOld,TNew>` / `Build()` |
| Create | `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs` | Same for retrieval |
| Modify | `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` | Wire builders; delete old factory lambdas |
| Add package | `src/Rag.NET/Rag.NET.csproj` | `ZeroAlloc.Inject` + `ZeroAlloc.Inject.Generators` |
