# ZeroAlloc Ecosystem Integration — Design

## Goal

Integrate all five ZeroAlloc.Net packages into Rag.NET: ValueObjects, Results, Validation, Mediator, and Specification — in dependency order, with no breaking changes to the pipeline internals.

## Architecture

The integration follows a layered approach: models get richer types (ValueObjects), public contracts get explicit error handling (Results), inputs get guarded at the boundary (Validation), an optional mediator layer decouples the API from the pipeline (Mediator), and post-retrieval filtering gets a composable, typed API (Specification). The pipeline internals — behaviors, contexts, `Pipeline<T>` — are untouched.

**Implementation order:** ValueObjects → Results → Validation → Mediator → Specification

---

## Section 1: Models & ValueObjects

`DocumentId` becomes a proper value object using `[ValueObject]`:

```csharp
// src/Rag.NET/Models/DocumentId.cs
[ValueObject]
public partial class DocumentId
{
    private readonly string _value;
    public DocumentId(string value) => _value = value;
    public override string ToString() => _value;
    public static implicit operator string(DocumentId id) => id._value;
    public static explicit operator DocumentId(string s) => new(s);
}
```

**Files changed:**
- `src/Rag.NET/Models/DocumentId.cs` — new
- `src/Rag.NET/Models/DocumentMetadata.cs` — `string DocumentId` → `DocumentId DocumentId`
- `src/Rag.NET/Models/TextChunk.cs` — `string DocumentId` → `DocumentId DocumentId`
- `src/Rag.NET/Models/IngestionResult.cs` — `string DocumentId` → `DocumentId DocumentId`
- `src/Rag.NET/Abstractions/IVectorStore.cs` — `string documentId` → `DocumentId documentId` in `DeleteAsync`
- Any callers updated accordingly (benchmarks, tests, API)

No `ChunkKey` value object — `ChunkIndex` (int) pairs with `DocumentId` and has no standalone identity.

---

## Section 2: Results & Error Handling

### RagError — discriminated union

```csharp
// src/Rag.NET/Models/RagError.cs
public abstract record RagError
{
    public sealed record ValidationFailed(IReadOnlyList<ValidationFailure> Failures) : RagError;
    public sealed record NoParserFound(string ContentType) : RagError;
    public sealed record StorageFailed(Exception Inner) : RagError;
    public sealed record NonSeekableStream() : RagError;
}
```

### Interface signatures

```csharp
// IIngestor.cs
Task<Result<IngestionResult, RagError>> IngestAsync(
    Stream content, DocumentMetadata metadata, CancellationToken ct = default);

// IRetriever.cs
Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
    string query, RetrievalOptions? options = null, CancellationToken ct = default);
```

### Facade boundary pattern

Pipeline behaviors keep returning `ValueTask<T>` unchanged. The facades catch and wrap:

```csharp
// PipelineIngestor.IngestAsync
try
{
    var r = await Pipeline.ExecuteAsync(ctx, ct);
    return Result.Success(r);
}
catch (NoParserFoundException ex) { return Result.Failure(new RagError.NoParserFound(ex.ContentType)); }
catch (Exception ex)              { return Result.Failure(new RagError.StorageFailed(ex)); }
```

`IRagPipeline` also gets `Result`-typed overloads. The API maps errors: `result.Match(ok => Results.Ok(ok), err => MapRagError(err))`.

---

## Section 3: Validation

`[Validate]` applied to the three public config/model types. Source-generated `Validate()` returns `IReadOnlyList<ValidationFailure>`. Validation fires at the facade boundary before the pipeline executes, producing `RagError.ValidationFailed`.

```csharp
[Validate]
public sealed class RetrievalOptions
{
    [GreaterThan(0)]       public int TopK { get; init; } = 5;
    [Between(0.0f, 1.0f)] public float RedundancyThreshold { get; init; } = 0.95f;
    [Between(0.0f, 1.0f)] public float MmrLambda { get; init; } = 0.5f;
}

[Validate]
public sealed class ChunkingOptions
{
    [GreaterThan(0)] public int MaxChunkSize { get; init; } = 512;
    [GreaterThan(0)] public int Overlap { get; init; } = 50;
}

[Validate]
public sealed class DocumentMetadata
{
    [NotEmpty] public required DocumentId DocumentId { get; init; }
    [NotEmpty] public required string FileName { get; init; }
}
```

**Rule:** No validation inside behaviors — keep the pipeline fast and assumption-free.

---

## Section 4: Mediator

New project `src/Rag.NET.Mediator/` with three request/handler pairs.

### Requests

```csharp
public sealed record IngestCommand(Stream Content, DocumentMetadata Metadata)
    : IRequest<Result<IngestionResult, RagError>>;

public sealed record RetrieveQuery(string Query, RetrievalOptions? Options = null)
    : IRequest<Result<IReadOnlyList<SearchResult>, RagError>>;

public sealed record DeleteCommand(DocumentId DocumentId)
    : IRequest<Result<Unit, RagError>>;
```

### Handlers

Handlers are thin — they delegate to `IIngestor`, `IRetriever`, `IDocumentStore`:

```csharp
public sealed class IngestCommandHandler(IIngestor ingestor)
    : IRequestHandler<IngestCommand, Result<IngestionResult, RagError>>
{
    public Task<Result<IngestionResult, RagError>> HandleAsync(IngestCommand req, CancellationToken ct)
        => ingestor.IngestAsync(req.Content, req.Metadata, ct);
}
```

### DI

```csharp
services.AddRagNETMediator(); // ZeroAlloc.Inject-generated: registers handlers + MediatorService
```

### Rag.NET.Api update

Endpoints swap `IRagPipeline` for `IMediator`. `IRagPipeline` remains available for users who prefer the direct facade.

---

## Section 5: Specification

### Built-in specs

```csharp
// src/Rag.NET/Retrieval/Specifications/
[Specification] public readonly partial struct MinScoreSpec(double threshold)
{
    public bool IsSatisfiedBy(SearchResult r) => r.Score >= threshold;
}

[Specification] public readonly partial struct HasTagSpec(string key, string value)
{
    public bool IsSatisfiedBy(SearchResult r) =>
        r.Chunk.Metadata.TryGetValue(key, out var v) && v == value;
}

[Specification] public readonly partial struct DocumentIdSpec(DocumentId id)
{
    public bool IsSatisfiedBy(SearchResult r) => r.Chunk.DocumentId == id;
}
```

### RetrievalOptions filter property

```csharp
public ISpecification<SearchResult>? Filter { get; init; }
```

### FilterBehavior

Inserted just before `VectorStoreBehavior` in the default retrieval pipeline. Short-circuits when `Filter` is null:

```csharp
[Singleton]
public sealed class FilterBehavior : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        if (ctx.Options.Filter is null) return results;
        return results.Where(r => ctx.Options.Filter.IsSatisfiedBy(r)).ToList();
    }
}
```

### Usage

```csharp
var options = new RetrievalOptions
{
    Filter = new MinScoreSpec(0.8).And(new HasTagSpec("lang", "en"))
};
```

`And`/`Or`/`Not` combinators are source-generated by ZeroAlloc.Specification.

---

## Package Dependencies

| Project | New NuGet reference |
|---|---|
| `Rag.NET` | `ZeroAlloc.ValueObjects`, `ZeroAlloc.Results`, `ZeroAlloc.Validation`, `ZeroAlloc.Specification` |
| `Rag.NET.Mediator` (new) | `ZeroAlloc.Mediator`, `ZeroAlloc.Inject`, project ref to `Rag.NET` |
| `Rag.NET.Api` | project ref to `Rag.NET.Mediator` |
