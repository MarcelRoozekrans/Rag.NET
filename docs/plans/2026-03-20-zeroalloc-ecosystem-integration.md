# ZeroAlloc Ecosystem Integration — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Integrate ZeroAlloc.ValueObjects, ZeroAlloc.Results, ZeroAlloc.Validation, ZeroAlloc.Mediator, and ZeroAlloc.Specification into Rag.NET in dependency order.

**Architecture:** Pipeline behaviors remain untouched — `ValueTask<T>` throughout. New types layer around the outside: value objects enrich the model, `Result<T, E>` wraps the facades, validation guards inputs at the boundary, the mediator is an opt-in dispatch layer on top of `IRagPipeline`, and `FilterBehavior` applies composable specs post-search. Each task is a vertical slice with TDD throughout.

**Tech Stack:** `ZeroAlloc.ValueObjects`, `ZeroAlloc.Results`, `ZeroAlloc.Validation`, `ZeroAlloc.Mediator`, `ZeroAlloc.Specification`, xunit v3, NSubstitute

---

## Background

**Important type facts:**

- `DocumentId` will have `implicit operator string(DocumentId id)` — a `DocumentId` can be passed wherever `string` is expected.
- `DocumentId` will have `explicit operator DocumentId(string s)` — converting a string literal requires a cast.
- In tests, use `new DocumentId("doc-1")` or `(DocumentId)"doc-1"` as the canonical forms.
- `DocumentId.ToString()` returns the underlying value.
- Interfaces `IBm25Index`, `IParentChunkStore`, `IRagDataManager` keep `string documentId` parameters — the implicit conversion from `DocumentId` → `string` handles all call sites.
- `ValidationResult.Failures` is a `ReadOnlySpan<ValidationFailure>` — materialise to array with `result.Failures.ToArray()` when storing in `RagError.ValidationFailed`.
- `ZeroAlloc.Mediator` handler method is `Handle`, **not** `HandleAsync`. Dispatch is `mediator.Send(cmd, ct)`, **not** `SendAsync`.
- `Result<T,E>` API: `Result<T,E>.Success(v)`, `Result<T,E>.Failure(e)`, `.Match(onSuccess, onFailure)`, `.IsSuccess`, `.Value`, `.Error`.

---

## Task 1: DocumentId Value Object

**Files:**
- Create: `src/Rag.NET/Models/DocumentId.cs`
- Modify: `src/Rag.NET/Rag.NET.csproj`
- Modify: `src/Rag.NET/Models/DocumentMetadata.cs`
- Modify: `src/Rag.NET/Models/TextChunk.cs`
- Modify: `src/Rag.NET/Models/IngestionResult.cs`
- Modify (all files using `string DocumentId` on the model type): any storage, behavior, API, and test files that construct `TextChunk`, `DocumentMetadata`, or `IngestionResult` with string literals
- Create: `tests/Rag.NET.Tests/Models/DocumentIdTests.cs`

### Step 1: Add NuGet packages

In `src/Rag.NET/Rag.NET.csproj`, add inside the existing `<ItemGroup>` with PackageReferences:

```xml
<PackageReference Include="ZeroAlloc.ValueObjects" Version="1.*" />
<PackageReference Include="ZeroAlloc.ValueObjects.Generator" Version="1.*" PrivateAssets="all" ExcludeAssets="runtime" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: PASS (no new types yet, just packages added)

### Step 2: Write failing test

Create `tests/Rag.NET.Tests/Models/DocumentIdTests.cs`:

```csharp
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class DocumentIdTests
{
    [Fact]
    public void DocumentId_EqualityByValue()
    {
        var a = new DocumentId("doc-1");
        var b = new DocumentId("doc-1");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void DocumentId_InequalityByValue()
    {
        var a = new DocumentId("doc-1");
        var b = new DocumentId("doc-2");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void DocumentId_ImplicitToString()
    {
        var id = new DocumentId("doc-1");
        string s = id;
        Assert.Equal("doc-1", s);
    }

    [Fact]
    public void DocumentId_ExplicitFromString()
    {
        var id = (DocumentId)"doc-1";
        Assert.Equal(new DocumentId("doc-1"), id);
    }

    [Fact]
    public void DocumentId_ToStringReturnsValue()
    {
        Assert.Equal("doc-1", new DocumentId("doc-1").ToString());
    }

    [Fact]
    public void DocumentId_UsableAsDictionaryKey()
    {
        var dict = new Dictionary<DocumentId, int>();
        var id = new DocumentId("doc-1");
        dict[id] = 42;
        Assert.Equal(42, dict[new DocumentId("doc-1")]);
    }
}
```

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "DocumentIdTests" -v minimal`
Expected: FAIL — `DocumentId` type does not exist yet.

### Step 3: Create DocumentId value object

Create `src/Rag.NET/Models/DocumentId.cs`:

```csharp
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[ValueObject]
public sealed partial class DocumentId
{
    private readonly string _value;

    public DocumentId(string value) => _value = value;

    public override string ToString() => _value;

    public static implicit operator string(DocumentId id) => id._value;
    public static explicit operator DocumentId(string s) => new(s);
}
```

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "DocumentIdTests" -v minimal`
Expected: PASS (all 6 tests)

### Step 4: Update model properties

**`src/Rag.NET/Models/DocumentMetadata.cs`** — change `string DocumentId` to `DocumentId DocumentId`:

```csharp
namespace Rag.NET.Models;

public sealed record DocumentMetadata
{
    public required DocumentId DocumentId { get; init; }
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
```

**`src/Rag.NET/Models/TextChunk.cs`** — change `string DocumentId` to `DocumentId DocumentId`:

```csharp
namespace Rag.NET.Models;

public sealed record TextChunk
{
    public required string Text { get; init; }
    public required DocumentId DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public int StartPosition { get; init; }
    public int EndPosition { get; init; }
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
```

**`src/Rag.NET/Models/IngestionResult.cs`** — change `string DocumentId` to `DocumentId DocumentId`:

```csharp
namespace Rag.NET.Models;

public sealed record IngestionResult
{
    public required DocumentId DocumentId { get; init; }
    public required int ChunksStored { get; init; }
}
```

### Step 5: Fix all compilation errors from model change

Run: `dotnet build src/Rag.NET/Rag.NET.csproj 2>&1 | grep -E "error|warning" | head -30`

**Pattern for all internal src files:** Anywhere that does `DocumentId = "some-string"`, change to `DocumentId = new DocumentId("some-string")` or `DocumentId = (DocumentId)variable` when sourcing from a string variable.

**Key files to update in `src/Rag.NET/`:**

1. `Ingestion/Behaviors/ParseBehavior.cs` — `ctx.Progress?.Report` references `ctx.Metadata.DocumentId` (now `DocumentId` type; implicit to string for any string context).

2. `Storage/SqliteDocumentStore.cs` — reads `DocumentId` from DB (string column), wrap with `new DocumentId(...)`.

3. `Storage/SqliteParentChunkStore.cs` — same pattern for DB reads.

4. `Storage/SqliteBm25Index.cs` — same pattern for DB reads.

5. `Logging/RagPipelineLog.cs` — if it formats `documentId` as string, `DocumentId` implicit conversion handles it.

**Key files to update in `src/Rag.NET.Qdrant/`, `src/Rag.NET.PgVector/`, `src/Rag.NET.AzureAISearch/`:**
Each reads `DocumentId` back from DB/search results and constructs `TextChunk`. Change to `new DocumentId(rawStringFromDb)`.

Run: `dotnet build 2>&1 | grep error | head -20`
Expected: No errors.

### Step 6: Fix all test compilation errors

Run: `dotnet build tests/Rag.NET.Tests/Rag.NET.Tests.csproj 2>&1 | grep error | head -30`

**Pattern:** Every test file that writes `DocumentId = "doc-1"` (or similar string literal) must change to `DocumentId = new DocumentId("doc-1")`.

Every test assertion `Assert.Equal("doc-1", chunk.DocumentId)` must change to `Assert.Equal(new DocumentId("doc-1"), chunk.DocumentId)` or `Assert.Equal("doc-1", chunk.DocumentId.ToString())`.

Apply these changes to the ~27 affected test files. The implicit-to-string conversion means no changes are needed where `DocumentId` is passed to methods that take `string` (e.g., `IBm25Index.Remove`, mock `DeleteByDocumentIdAsync(string, ...)` calls).

Run: `dotnet build tests/ 2>&1 | grep error | head -20`
Expected: No errors.

### Step 7: Run full test suite

Run: `dotnet test --no-build -v minimal 2>&1 | tail -20`
Expected: All tests pass.

### Step 8: Commit

```bash
git add src/Rag.NET/Models/DocumentId.cs src/Rag.NET/Models/DocumentMetadata.cs src/Rag.NET/Models/TextChunk.cs src/Rag.NET/Models/IngestionResult.cs src/Rag.NET/Rag.NET.csproj
git add tests/Rag.NET.Tests/Models/DocumentIdTests.cs
git add src/Rag.NET.Qdrant/ src/Rag.NET.PgVector/ src/Rag.NET.AzureAISearch/
git add tests/
git commit -m "feat: introduce DocumentId value object (ZeroAlloc.ValueObjects)"
```

---

## Task 2: RagError Discriminated Union + Result Interfaces

**Files:**
- Create: `src/Rag.NET/Models/RagError.cs`
- Modify: `src/Rag.NET/Rag.NET.csproj` (add ZeroAlloc.Results)
- Modify: `src/Rag.NET/Abstractions/IIngestor.cs`
- Modify: `src/Rag.NET/Abstractions/IRetriever.cs`
- Modify: `src/Rag.NET/Abstractions/IRagPipeline.cs`
- Modify: `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs` (throw typed exception)
- Create: `src/Rag.NET/Ingestion/NoParserFoundException.cs`
- Modify: `src/Rag.NET/Ingestion/PipelineIngestor.cs`
- Modify: `src/Rag.NET/Retrieval/PipelineRetriever.cs`
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `src/Rag.NET.Api/DependencyInjection/EndpointRouteBuilderExtensions.cs`
- Modify: all test files referencing `IIngestor`, `IRetriever`, or `IRagPipeline`
- Create: `tests/Rag.NET.Tests/Models/RagErrorTests.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/PipelineIngestorResultTests.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/PipelineRetrieverResultTests.cs`

### Step 1: Add ZeroAlloc.Results package

In `src/Rag.NET/Rag.NET.csproj`:

```xml
<PackageReference Include="ZeroAlloc.Results" Version="1.*" />
```

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: PASS

### Step 2: Write failing test for RagError

Create `tests/Rag.NET.Tests/Models/RagErrorTests.cs`:

```csharp
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class RagErrorTests
{
    [Fact]
    public void ValidationFailed_HoldsFailures()
    {
        var failures = new[] { new ValidationFailure("TopK", "must be > 0") };
        var error = new RagError.ValidationFailed(failures);
        Assert.Single(error.Failures);
        Assert.Equal("TopK", error.Failures[0].PropertyName);
    }

    [Fact]
    public void NoParserFound_HoldsContentType()
    {
        var error = new RagError.NoParserFound("application/pdf");
        Assert.Equal("application/pdf", error.ContentType);
    }

    [Fact]
    public void StorageFailed_HoldsException()
    {
        var ex = new InvalidOperationException("db down");
        var error = new RagError.StorageFailed(ex);
        Assert.Same(ex, error.Inner);
    }

    [Fact]
    public void NonSeekableStream_IsDistinctSubtype()
    {
        RagError error = new RagError.NonSeekableStream();
        Assert.IsType<RagError.NonSeekableStream>(error);
    }
}
```

Note: `ValidationFailure` here is a plain record we define alongside `RagError` — it is NOT from ZeroAlloc.Validation yet (that comes in Task 3). For now it holds `PropertyName` and `ErrorMessage` strings.

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagErrorTests" -v minimal`
Expected: FAIL — `RagError` does not exist.

### Step 3: Create RagError and ValidationFailure

Create `src/Rag.NET/Models/RagError.cs`:

```csharp
namespace Rag.NET.Models;

/// <summary>
/// Discriminated union of all errors that can occur at the Rag.NET facade boundary.
/// Pattern-match with a switch expression to handle specific subtypes.
/// </summary>
public abstract record RagError
{
    /// <summary>One or more input validation rules failed.</summary>
    public sealed record ValidationFailed(IReadOnlyList<ValidationFailure> Failures) : RagError;

    /// <summary>No registered <see cref="Abstractions.IDocumentParser"/> handles the content type.</summary>
    public sealed record NoParserFound(string ContentType) : RagError;

    /// <summary>An exception was thrown by a storage operation.</summary>
    public sealed record StorageFailed(Exception Inner) : RagError;

    /// <summary>The ingestion stream is not readable.</summary>
    public sealed record NonSeekableStream() : RagError;
}

/// <summary>A single validation rule failure. Produced by the facade boundary validator.</summary>
public sealed record ValidationFailure(string PropertyName, string ErrorMessage);
```

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagErrorTests" -v minimal`
Expected: PASS (all 4 tests)

### Step 4: Create NoParserFoundException

Create `src/Rag.NET/Ingestion/NoParserFoundException.cs`:

```csharp
namespace Rag.NET.Ingestion;

/// <summary>Thrown by <see cref="Behaviors.ParseBehavior"/> when no parser is registered for the content type.</summary>
public sealed class NoParserFoundException(string contentType)
    : InvalidOperationException($"No parser registered for content type '{contentType}'.")
{
    public string ContentType { get; } = contentType;
}
```

Update `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs` — replace the existing `throw new InvalidOperationException(...)` with `throw new NoParserFoundException(ctx.Metadata.ContentType ?? "text/plain")`.

Before:
```csharp
var parser = Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
    ?? throw new InvalidOperationException(
        $"No parser registered for content type '{ctx.Metadata.ContentType}'.");
```

After:
```csharp
var parser = Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
    ?? throw new NoParserFoundException(ctx.Metadata.ContentType ?? "text/plain");
```

### Step 5: Update IIngestor and IRetriever

**`src/Rag.NET/Abstractions/IIngestor.cs`:**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Abstractions;

public interface IIngestor
{
    Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
```

**`src/Rag.NET/Abstractions/IRetriever.cs`:**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Abstractions;

public interface IRetriever
{
    Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

### Step 6: Update PipelineIngestor facade

Replace `PipelineIngestor.IngestAsync` body to wrap exceptions:

```csharp
public async Task<Result<IngestionResult, RagError>> IngestAsync(
    Stream document,
    DocumentMetadata metadata,
    IngestionOptions? options = null,
    IProgress<IngestionProgress>? progress = null,
    CancellationToken cancellationToken = default)
{
    if (!document.CanRead)
        return Result<IngestionResult, RagError>.Failure(new RagError.NonSeekableStream());

    var ctx = new IngestionContext
    {
        Stream = document,
        Metadata = metadata,
        Options = options,
        Progress = progress,
        GetNextBm25DocId = () => System.Threading.Interlocked.Increment(ref _nextBm25DocId),
    };

    try
    {
        var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
        return Result<IngestionResult, RagError>.Success(result);
    }
    catch (NoParserFoundException ex)
    {
        return Result<IngestionResult, RagError>.Failure(new RagError.NoParserFound(ex.ContentType));
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        return Result<IngestionResult, RagError>.Failure(new RagError.StorageFailed(ex));
    }
}
```

### Step 7: Update PipelineRetriever facade

Replace `PipelineRetriever.RetrieveAsync` body:

```csharp
public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
    string query,
    RetrievalOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var ctx = new RetrievalContext
    {
        Query = query,
        Options = options ?? new RetrievalOptions(),
        Logger = (ILogger?)Logger ?? NullLogger.Instance,
    };

    try
    {
        var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<SearchResult>, RagError>.Success(result);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        return Result<IReadOnlyList<SearchResult>, RagError>.Failure(new RagError.StorageFailed(ex));
    }
}
```

### Step 8: Update IRagPipeline and RagPipeline

**`src/Rag.NET/Abstractions/IRagPipeline.cs`** — change `IngestAsync` and `RetrieveAsync` return types to `Result`-wrapped:

```csharp
Task<Result<IngestionResult, RagError>> IngestAsync(...);
Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(...);
// AskAsync, AskStreamingAsync, DeleteAsync signatures unchanged
```

**`src/Rag.NET/Pipeline/RagPipeline.cs`** — delegate to ingestor/retriever and propagate the `Result`:

```csharp
public Task<Result<IngestionResult, RagError>> IngestAsync(
    Stream document, DocumentMetadata metadata, IngestionOptions? options = null,
    IProgress<IngestionProgress>? progress = null, CancellationToken cancellationToken = default)
    => ingestor.IngestAsync(document, metadata, options, progress, cancellationToken);

public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
    string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    => retriever.RetrieveAsync(query, options, cancellationToken);
```

For `AskAsync` and `AskStreamingAsync`, call `retriever.RetrieveAsync` and unwrap with `.Match`:

```csharp
public async Task<RagResponse> AskAsync(string query, RagOptions? options = null, CancellationToken cancellationToken = default)
{
    if (answerEngine is null)
        throw new InvalidOperationException("IAnswerEngine is not registered. Register an IChatClient in DI to use AskAsync.");

    var opts = options ?? new RagOptions();
    var retrievalOptions = BuildRetrievalOptions(opts);
    var retrievalResult = await retriever.RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);
    var sources = retrievalResult.Match(
        onSuccess: s => s,
        onFailure: err => throw new InvalidOperationException($"Retrieval failed: {err}"));

    return await answerEngine.AskAsync(query, sources, opts, cancellationToken).ConfigureAwait(false);
}
```

Extract the `RetrievalOptions` building into a private helper `BuildRetrievalOptions(RagOptions opts)` (existing logic in both AskAsync and AskStreamingAsync).

### Step 9: Update API endpoints

In `src/Rag.NET.Api/DependencyInjection/EndpointRouteBuilderExtensions.cs`:

Add a private helper:

```csharp
private static IResult MapRagError(RagError err) => err switch
{
    RagError.ValidationFailed v => Results.UnprocessableEntity(new { errors = v.Failures.Select(f => new { f.PropertyName, f.ErrorMessage }) }),
    RagError.NoParserFound n   => Results.BadRequest(new { error = $"No parser for content type: {n.ContentType}" }),
    RagError.NonSeekableStream => Results.BadRequest(new { error = "Document stream is not readable." }),
    RagError.StorageFailed s   => Results.Problem($"Storage error: {s.Inner.Message}"),
    _                          => Results.StatusCode(500),
};
```

Update `/ingest` endpoint:
```csharp
var result = await pipeline.IngestAsync(stream, metadata, cancellationToken: ct).ConfigureAwait(false);
return result.Match(
    onSuccess: r => Results.Ok(new IngestResponse(r.DocumentId, r.ChunksStored)),
    onFailure: MapRagError);
```

Update `/retrieve` endpoint:
```csharp
var results = await pipeline.RetrieveAsync(req.Query, retrievalOptions, ct).ConfigureAwait(false);
return results.Match(
    onSuccess: r => Results.Ok(new RetrieveResponse(r.Select(SearchResultMapper.ToDto).ToList())),
    onFailure: MapRagError);
```

### Step 10: Fix all test compilation errors

Run: `dotnet build 2>&1 | grep error | head -30`

**Pattern for mock setup** — anywhere NSubstitute mocks `IIngestor.IngestAsync` or `IRetriever.RetrieveAsync`:

Before:
```csharp
_ingestor.IngestAsync(...).Returns(Task.FromResult(new IngestionResult { ... }));
```
After:
```csharp
_ingestor.IngestAsync(...).Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(new IngestionResult { ... })));
```

**Pattern for assertions:**

Before:
```csharp
var result = await sut.IngestAsync(...);
Assert.Equal("doc-1", result.DocumentId);
```
After:
```csharp
var result = await sut.IngestAsync(...);
Assert.True(result.IsSuccess);
Assert.Equal(new DocumentId("doc-1"), result.Value.DocumentId);
```

Key test files to update:
- `tests/Rag.NET.Tests/Ingestion/PipelineIngestorTests.cs`
- `tests/Rag.NET.Tests/Pipeline/RagPipelineFacadeTests.cs`
- `tests/Rag.NET.Api.Tests/Integration/RagApiIntegrationTests.cs`

### Step 11: Write new facade boundary tests

Create `tests/Rag.NET.Tests/Ingestion/PipelineIngestorResultTests.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Pipeline;
using NSubstitute;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorResultTests
{
    private static PipelineIngestor CreateSut(
        Pipeline<IngestionContext, IngestionResult>? pipeline = null) => new()
    {
        Pipeline = pipeline ?? new Pipeline<IngestionContext, IngestionResult>(
            (ctx, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
        VectorStore = Substitute.For<IVectorStore>(),
        Bm25Index = Substitute.For<IBm25Index>(),
    };

    [Fact]
    public async Task IngestAsync_NonReadableStream_ReturnsNonSeekableStream()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };
        // Create a non-readable stream
        var stream = new MemoryStream([], writable: false);
        stream.Close(); // closed stream is not readable

        var result = await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.IsType<RagError.NonSeekableStream>(result.Error);
    }

    [Fact]
    public async Task IngestAsync_NoParser_ReturnsNoParserFound()
    {
        var pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (_, _) => throw new NoParserFoundException("text/rtf"));
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.rtf", ContentType = "text/rtf" };

        var result = await sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.NoParserFound>(result.Error);
        Assert.Equal("text/rtf", error.ContentType);
    }

    [Fact]
    public async Task IngestAsync_PipelineSuccess_ReturnsSuccess()
    {
        var expected = new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 3 };
        var pipeline = new Pipeline<IngestionContext, IngestionResult>((_, _) => ValueTask.FromResult(expected));
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1, 2, 3]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.ChunksStored);
    }

    [Fact]
    public async Task IngestAsync_PipelineThrowsUnknown_ReturnsStorageFailed()
    {
        var pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (_, _) => throw new InvalidOperationException("db down"));
        var sut = CreateSut(pipeline);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.StorageFailed>(result.Error);
        Assert.Equal("db down", error.Inner.Message);
    }
}
```

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "PipelineIngestorResultTests" -v minimal`
Expected: PASS (implementation already done in Step 6)

### Step 12: Run full test suite

Run: `dotnet test --no-build -v minimal 2>&1 | tail -20`
Expected: All tests pass.

### Step 13: Commit

```bash
git add -A
git commit -m "feat: wrap IIngestor/IRetriever in Result<T,RagError> (ZeroAlloc.Results)"
```

---

## Task 3: Validation at Facade Boundary

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj` (add ZeroAlloc.Validation)
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs`
- Modify: `src/Rag.NET/Models/Options/ChunkingOptions.cs`
- Modify: `src/Rag.NET/Models/DocumentMetadata.cs`
- Modify: `src/Rag.NET/Models/RagError.cs` (replace hand-rolled `ValidationFailure` with ZeroAlloc's)
- Modify: `src/Rag.NET/Ingestion/PipelineIngestor.cs`
- Modify: `src/Rag.NET/Retrieval/PipelineRetriever.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/PipelineIngestorValidationTests.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/PipelineRetrieverValidationTests.cs`

### Step 1: Add ZeroAlloc.Validation package

In `src/Rag.NET/Rag.NET.csproj`:

```xml
<PackageReference Include="ZeroAlloc.Validation" Version="1.*" />
```

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: PASS

### Step 2: Write failing validation tests

Create `tests/Rag.NET.Tests/Ingestion/PipelineIngestorValidationTests.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Pipeline;
using NSubstitute;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Ingestion;

public class PipelineIngestorValidationTests
{
    private static PipelineIngestor CreateSut() => new()
    {
        Pipeline = new Pipeline<IngestionContext, IngestionResult>(
            (ctx, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 })),
        VectorStore = Substitute.For<IVectorStore>(),
        Bm25Index = Substitute.For<IBm25Index>(),
    };

    [Fact]
    public async Task IngestAsync_EmptyDocumentId_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        // DocumentId with empty string is invalid per [NotEmpty] on DocumentMetadata
        var metadata = new DocumentMetadata { DocumentId = new DocumentId(""), FileName = "f.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("DocumentId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestAsync_EmptyFileName_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "" };

        var result = await sut.IngestAsync(new MemoryStream([1]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("FileName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestAsync_ValidMetadata_Succeeds()
    {
        var sut = CreateSut();
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "file.txt" };

        var result = await sut.IngestAsync(new MemoryStream([1, 2, 3]), metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }
}
```

Create `tests/Rag.NET.Tests/Retrieval/PipelineRetrieverValidationTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class PipelineRetrieverValidationTests
{
    private static PipelineRetriever CreateSut() => new()
    {
        Pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]))
    };

    [Fact]
    public async Task RetrieveAsync_ZeroTopK_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { TopK = 0 };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("TopK", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RetrieveAsync_ValidOptions_Succeeds()
    {
        var sut = CreateSut();

        var result = await sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }
}
```

Run: `dotnet test --filter "PipelineIngestorValidationTests|PipelineRetrieverValidationTests" -v minimal`
Expected: FAIL — validation annotations don't exist yet.

### Step 3: Annotate models

**`src/Rag.NET/Models/Options/RetrievalOptions.cs`** — add `[Validate]` and constraint attributes:

```csharp
using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

[Validate]
public sealed record RetrievalOptions
{
    [GreaterThan(0)] public int TopK { get; init; } = 5;
    public double MinScore { get; init; } = 0.0;
    public IDictionary<string, string>? MetadataFilter { get; init; }
    public bool UseHybridSearch { get; init; }
    public bool UseLostInTheMiddleReordering { get; init; }
    public bool UseRedundancyFilter { get; init; }
    [Between(0.0f, 1.0f)] public float RedundancyThreshold { get; init; } = 0.95f;
    public bool UseMmr { get; init; } = false;
    [Between(0.0f, 1.0f)] public float MmrLambda { get; init; } = 0.5f;
    public int? MmrCandidateCount { get; init; }
    public bool UseMultiQuery { get; init; } = true;
    public bool UseReranking { get; init; } = true;
    public int? CandidateCount { get; init; }
    public bool UseHyde { get; init; } = true;
    public bool UseCacheEmbedding { get; init; } = true;
    public bool UseCacheResult { get; init; } = true;
    public bool UseParentDocument { get; init; } = true;
    internal string? EmbeddingTextOverride { get; init; }
}
```

**`src/Rag.NET/Models/Options/ChunkingOptions.cs`** — add validation:

```csharp
using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

[Validate]
public sealed class ChunkingOptions
{
    [GreaterThan(0)] public int MaxChunkSize { get; set; } = 512;
    [GreaterThan(0)] public int Overlap { get; set; } = 50;
}
```

**`src/Rag.NET/Models/DocumentMetadata.cs`** — add validation. The `DocumentId` property is a value object, not a string, so `[NotEmpty]` won't directly apply to it. Use a `[Must]` predicate or a custom check. The simplest approach: annotate the `FileName` with `[NotEmpty]` and for `DocumentId`, validate that `ToString()` is not empty using a `[Must]` attribute:

```csharp
using ZeroAlloc.Validation;

namespace Rag.NET.Models;

[Validate]
public sealed record DocumentMetadata
{
    [Must(nameof(DocumentIdNotEmpty))]
    public required DocumentId DocumentId { get; init; }

    [NotEmpty]
    public required string FileName { get; init; }

    public string? ContentType { get; init; }
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool DocumentIdNotEmpty(DocumentId id) => !string.IsNullOrEmpty(id.ToString());
}
```

Note: Check the ZeroAlloc.Validation docs for exact `[Must]` attribute syntax. If `[Must]` takes a method name string, use `nameof(DocumentIdNotEmpty)`. If it takes a different form, adjust accordingly.

### Step 4: Replace hand-rolled ValidationFailure with ZeroAlloc's

**`src/Rag.NET/Models/RagError.cs`** — remove the hand-rolled `ValidationFailure` record and import from ZeroAlloc.Validation:

```csharp
using ZeroAlloc.Validation;

namespace Rag.NET.Models;

public abstract record RagError
{
    public sealed record ValidationFailed(IReadOnlyList<ZeroAlloc.Validation.ValidationFailure> Failures) : RagError;
    public sealed record NoParserFound(string ContentType) : RagError;
    public sealed record StorageFailed(Exception Inner) : RagError;
    public sealed record NonSeekableStream() : RagError;
}
```

Delete the hand-rolled `ValidationFailure` record from `RagError.cs`. Fix any compilation errors — `RagErrorTests.cs` references `new ValidationFailure("TopK", "must be > 0")`. Update to use `ZeroAlloc.Validation.ValidationFailure`. Check the exact constructor signature from the Validation library source (likely `new ValidationFailure { PropertyName = "TopK", ErrorMessage = "must be > 0" }` or similar).

### Step 5: Wire validation into facades

**`src/Rag.NET/Ingestion/PipelineIngestor.cs`** — add validation at the top of `IngestAsync`:

```csharp
// At the top of IngestAsync, before the CanRead check:
var validationResult = new DocumentMetadataValidator().Validate(metadata);
if (!validationResult.IsValid)
    return Result<IngestionResult, RagError>.Failure(
        new RagError.ValidationFailed(validationResult.Failures.ToArray()));
```

**`src/Rag.NET/Retrieval/PipelineRetriever.cs`** — add validation at the top of `RetrieveAsync`:

```csharp
if (options is not null)
{
    var validationResult = new RetrievalOptionsValidator().Validate(options);
    if (!validationResult.IsValid)
        return Result<IReadOnlyList<SearchResult>, RagError>.Failure(
            new RagError.ValidationFailed(validationResult.Failures.ToArray()));
}
```

Note: `DocumentMetadataValidator` and `RetrievalOptionsValidator` are the source-generated classes emitted by ZeroAlloc.Validation when `[Validate]` is applied to `DocumentMetadata` and `RetrievalOptions`. They are generated at build time in the same namespace as the annotated types.

### Step 6: Run validation tests

Run: `dotnet test --filter "PipelineIngestorValidationTests|PipelineRetrieverValidationTests" -v minimal`
Expected: PASS (all tests)

### Step 7: Run full test suite

Run: `dotnet test -v minimal 2>&1 | tail -20`
Expected: All tests pass.

### Step 8: Commit

```bash
git add -A
git commit -m "feat: add ZeroAlloc.Validation guards at facade boundary"
```

---

## Task 4: Mediator Layer

**Files:**
- Create: `src/Rag.NET.Mediator/` (new project)
  - `Rag.NET.Mediator.csproj`
  - `Requests/IngestCommand.cs`
  - `Requests/RetrieveQuery.cs`
  - `Requests/DeleteCommand.cs`
  - `Handlers/IngestCommandHandler.cs`
  - `Handlers/RetrieveQueryHandler.cs`
  - `Handlers/DeleteCommandHandler.cs`
  - `DependencyInjection/MediatorServiceCollectionExtensions.cs`
- Modify: `src/Rag.NET.Api/Rag.NET.Api.csproj` (add project ref to Rag.NET.Mediator)
- Modify: `src/Rag.NET.Api/DependencyInjection/EndpointRouteBuilderExtensions.cs` (use IMediator)
- Create: `tests/Rag.NET.Mediator.Tests/` (new test project) or add tests to existing project
- Add: `src/Rag.NET.Mediator/` to the solution

### Step 1: Create the new project

Create `src/Rag.NET.Mediator/Rag.NET.Mediator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Mediator</RootNamespace>
    <PackageId>Rag.NET.Mediator</PackageId>
    <Description>ZeroAlloc.Mediator integration for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ZeroAlloc.Mediator" Version="1.*" />
    <PackageReference Include="ZeroAlloc.Mediator.Generator" Version="1.*" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>
</Project>
```

Add to solution:
```bash
dotnet sln add src/Rag.NET.Mediator/Rag.NET.Mediator.csproj
```

### Step 2: Write failing tests

Add `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` a project reference to `Rag.NET.Mediator`, then create `tests/Rag.NET.Tests/Mediator/MediatorHandlerTests.cs`:

```csharp
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Mediator.Handlers;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Mediator;

public class MediatorHandlerTests
{
    [Fact]
    public async Task IngestCommandHandler_DelegatesToIngestor()
    {
        var ingestor = Substitute.For<IIngestor>();
        var expected = Result<IngestionResult, RagError>.Success(
            new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 2 });
        ingestor.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var handler = new IngestCommandHandler(ingestor);
        var cmd = new IngestCommand(new MemoryStream(),
            new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt" });

        var result = await handler.Handle(cmd, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ChunksStored);
    }

    [Fact]
    public async Task RetrieveQueryHandler_DelegatesToRetriever()
    {
        var retriever = Substitute.For<IRetriever>();
        var chunks = new List<SearchResult>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(chunks)));

        var handler = new RetrieveQueryHandler(retriever);
        var query = new RetrieveQuery("my query");

        var result = await handler.Handle(query, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task DeleteCommandHandler_DelegatesToIngestor()
    {
        var ingestor = Substitute.For<IIngestor>();
        ingestor.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new DeleteCommandHandler(ingestor);
        var cmd = new DeleteCommand(new DocumentId("doc-1"));

        var result = await handler.Handle(cmd, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await ingestor.Received(1).DeleteAsync(Arg.Is<string>(s => s == "doc-1"), Arg.Any<CancellationToken>());
    }
}
```

Run: `dotnet build tests/Rag.NET.Tests/ 2>&1 | grep error | head -10`
Expected: FAIL — handler types do not exist.

### Step 3: Create request types

Create `src/Rag.NET.Mediator/Requests/IngestCommand.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Requests;

public sealed record IngestCommand(
    Stream Content,
    DocumentMetadata Metadata,
    IngestionOptions? Options = null,
    IProgress<IngestionProgress>? Progress = null)
    : IRequest<Result<IngestionResult, RagError>>;
```

Create `src/Rag.NET.Mediator/Requests/RetrieveQuery.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Requests;

public sealed record RetrieveQuery(string Query, RetrievalOptions? Options = null)
    : IRequest<Result<IReadOnlyList<SearchResult>, RagError>>;
```

Create `src/Rag.NET.Mediator/Requests/DeleteCommand.cs`:

```csharp
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Requests;

public sealed record DeleteCommand(DocumentId DocumentId)
    : IRequest<Result<Unit, RagError>>;
```

### Step 4: Create handlers

Create `src/Rag.NET.Mediator/Handlers/IngestCommandHandler.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Handlers;

public sealed class IngestCommandHandler(IIngestor ingestor)
    : IRequestHandler<IngestCommand, Result<IngestionResult, RagError>>
{
    public Task<Result<IngestionResult, RagError>> Handle(IngestCommand request, CancellationToken ct)
        => ingestor.IngestAsync(request.Content, request.Metadata, request.Options, request.Progress, ct);
}
```

Create `src/Rag.NET.Mediator/Handlers/RetrieveQueryHandler.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Handlers;

public sealed class RetrieveQueryHandler(IRetriever retriever)
    : IRequestHandler<RetrieveQuery, Result<IReadOnlyList<SearchResult>, RagError>>
{
    public Task<Result<IReadOnlyList<SearchResult>, RagError>> Handle(RetrieveQuery request, CancellationToken ct)
        => retriever.RetrieveAsync(request.Query, request.Options, ct);
}
```

Create `src/Rag.NET.Mediator/Handlers/DeleteCommandHandler.cs`:

```csharp
using Rag.NET.Abstractions;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Handlers;

public sealed class DeleteCommandHandler(IIngestor ingestor)
    : IRequestHandler<DeleteCommand, Result<Unit, RagError>>
{
    public async Task<Result<Unit, RagError>> Handle(DeleteCommand request, CancellationToken ct)
    {
        await ingestor.DeleteAsync(request.DocumentId, ct).ConfigureAwait(false);
        return Result<Unit, RagError>.Success(Unit.Value);
    }
}
```

### Step 5: Create DI extension

Create `src/Rag.NET.Mediator/DependencyInjection/MediatorServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Mediator.Handlers;
using ZeroAlloc.Mediator;

namespace Rag.NET.Mediator.DependencyInjection;

public static class MediatorServiceCollectionExtensions
{
    public static IServiceCollection AddRagNETMediator(this IServiceCollection services)
    {
        services.AddTransient<IngestCommandHandler>();
        services.AddTransient<RetrieveQueryHandler>();
        services.AddTransient<DeleteCommandHandler>();

        services.AddSingleton<IMediator>(sp =>
        {
            Mediator.Configure(config =>
            {
                config.SetFactory(() => sp.GetRequiredService<IngestCommandHandler>());
                config.SetFactory(() => sp.GetRequiredService<RetrieveQueryHandler>());
                config.SetFactory(() => sp.GetRequiredService<DeleteCommandHandler>());
            });
            return new MediatorService();
        });

        return services;
    }
}
```

### Step 6: Run handler tests

Run: `dotnet test --filter "MediatorHandlerTests" -v minimal`
Expected: PASS (all 3 tests)

### Step 7: Update Rag.NET.Api to use IMediator

In `src/Rag.NET.Api/Rag.NET.Api.csproj`, add project reference:

```xml
<ProjectReference Include="..\Rag.NET.Mediator\Rag.NET.Mediator.csproj" />
```

In `EndpointRouteBuilderExtensions.cs`, add `IMediator`-based endpoints alongside the existing `IRagPipeline` endpoints (or replace them — per design, `IRagPipeline` stays for direct access but API uses mediator):

```csharp
// Update /ingest to use IMediator when registered, fallback to IRagPipeline
app.MapPost($"{prefix}/ingest", async (IngestRequest req, IMediator mediator, CancellationToken ct) =>
{
    var docId = new DocumentId(req.DocumentId ?? Guid.NewGuid().ToString());
    var metadata = new DocumentMetadata
    {
        DocumentId = docId,
        FileName = req.FileName ?? "document.txt",
        ContentType = req.ContentType,
        Tags = req.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal)
    };
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(req.Content));
    var result = await mediator.Send(new IngestCommand(stream, metadata), ct).ConfigureAwait(false);
    return result.Match(
        onSuccess: r => Results.Ok(new IngestResponse(r.DocumentId.ToString(), r.ChunksStored)),
        onFailure: MapRagError);
});
```

Apply same pattern to `/retrieve` and `/documents/{documentId}` delete endpoint.

Note: `MapRagError` helper from Task 2 remains unchanged.

### Step 8: Run full test suite

Run: `dotnet test -v minimal 2>&1 | tail -20`
Expected: All tests pass.

### Step 9: Commit

```bash
git add -A
git commit -m "feat: add Rag.NET.Mediator project with IMediator dispatch layer"
```

---

## Task 5: Specification + FilterBehavior

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj` (add ZeroAlloc.Specification packages)
- Create: `src/Rag.NET/Retrieval/Specifications/MinScoreSpec.cs`
- Create: `src/Rag.NET/Retrieval/Specifications/HasTagSpec.cs`
- Create: `src/Rag.NET/Retrieval/Specifications/DocumentIdSpec.cs`
- Modify: `src/Rag.NET/Models/Options/RetrievalOptions.cs` (add `Filter` property)
- Create: `src/Rag.NET/Retrieval/Behaviors/FilterBehavior.cs`
- Modify: `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs` (register FilterBehavior)
- Create: `tests/Rag.NET.Tests/Retrieval/Behaviors/FilterBehaviorTests.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/Specifications/SpecificationTests.cs`

### Step 1: Add ZeroAlloc.Specification packages

In `src/Rag.NET/Rag.NET.csproj`:

```xml
<PackageReference Include="ZeroAlloc.Specification" Version="1.*" />
<PackageReference Include="ZeroAlloc.Specification.Generator" Version="1.*" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: PASS

### Step 2: Write failing spec tests

Create `tests/Rag.NET.Tests/Retrieval/Specifications/SpecificationTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Retrieval.Specifications;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Specifications;

public class SpecificationTests
{
    private static SearchResult MakeResult(string docId, double score, string? tagKey = null, string? tagValue = null)
    {
        var chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = 0 };
        if (tagKey is not null)
            chunk.Metadata[tagKey] = tagValue!;
        return new SearchResult { Chunk = chunk, Score = score };
    }

    [Fact]
    public void MinScoreSpec_PassesAboveThreshold()
    {
        var spec = new MinScoreSpec(0.8);
        Assert.True(spec.IsSatisfiedBy(MakeResult("d", 0.9)));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 0.7)));
    }

    [Fact]
    public void HasTagSpec_MatchesExactKeyValue()
    {
        var spec = new HasTagSpec("lang", "en");
        Assert.True(spec.IsSatisfiedBy(MakeResult("d", 1.0, "lang", "en")));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 1.0, "lang", "fr")));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 1.0)));
    }

    [Fact]
    public void DocumentIdSpec_MatchesById()
    {
        var spec = new DocumentIdSpec(new DocumentId("doc-1"));
        Assert.True(spec.IsSatisfiedBy(MakeResult("doc-1", 1.0)));
        Assert.False(spec.IsSatisfiedBy(MakeResult("doc-2", 1.0)));
    }

    [Fact]
    public void AndSpec_RequiresBoth()
    {
        var spec = new MinScoreSpec(0.8).And(new HasTagSpec("lang", "en"));
        Assert.True(spec.IsSatisfiedBy(MakeResult("d", 0.9, "lang", "en")));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 0.9, "lang", "fr"))); // tag fails
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 0.5, "lang", "en"))); // score fails
    }
}
```

Run: `dotnet test --filter "SpecificationTests" -v minimal`
Expected: FAIL — spec types don't exist.

### Step 3: Create built-in specifications

Create `src/Rag.NET/Retrieval/Specifications/MinScoreSpec.cs`:

```csharp
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct MinScoreSpec(double threshold) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) => r.Score >= threshold;
}
```

Create `src/Rag.NET/Retrieval/Specifications/HasTagSpec.cs`:

```csharp
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct HasTagSpec(string key, string value) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) =>
        r.Chunk.Metadata.TryGetValue(key, out var v) &&
        string.Equals(v, value, StringComparison.Ordinal);
}
```

Create `src/Rag.NET/Retrieval/Specifications/DocumentIdSpec.cs`:

```csharp
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct DocumentIdSpec(DocumentId id) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) => r.Chunk.DocumentId == id;
}
```

Run: `dotnet test --filter "SpecificationTests" -v minimal`
Expected: PASS (all 4 tests)

### Step 4: Write failing FilterBehavior tests

Create `tests/Rag.NET.Tests/Retrieval/Behaviors/FilterBehaviorTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Retrieval.Specifications;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class FilterBehaviorTests
{
    private static SearchResult MakeResult(string docId, double score) =>
        new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = score };

    private static RetrievalContext MakeCtx(ISpecification<SearchResult>? filter) =>
        new() { Query = "q", Options = new RetrievalOptions { Filter = filter } };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    [Fact]
    public async Task Filter_WhenNull_ReturnsAllResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("d1", 0.9), MakeResult("d2", 0.5) };
        var sut = new FilterBehavior();

        var output = await sut.HandleAsync(MakeCtx(null), ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task Filter_WithMinScore_RemovesBelowThreshold()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("d1", 0.9),
            MakeResult("d2", 0.5),
            MakeResult("d3", 0.85),
        };
        var sut = new FilterBehavior();

        var output = await sut.HandleAsync(MakeCtx(new MinScoreSpec(0.8)), ct, NextReturning(results));

        Assert.Equal(2, output.Count);
        Assert.All(output, r => Assert.True(r.Score >= 0.8));
    }

    [Fact]
    public async Task Filter_AndSpec_AppliesBothConditions()
    {
        var ct = TestContext.Current.CancellationToken;
        var r1 = new SearchResult
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d1"), ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = "en" } },
            Score = 0.9
        };
        var r2 = new SearchResult
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d2"), ChunkIndex = 0,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = "fr" } },
            Score = 0.9
        };
        var results = new List<SearchResult> { r1, r2 };
        var sut = new FilterBehavior();
        var spec = new MinScoreSpec(0.8).And(new HasTagSpec("lang", "en"));

        var output = await sut.HandleAsync(MakeCtx(spec), ct, NextReturning(results));

        Assert.Single(output);
        Assert.Equal(new DocumentId("d1"), output[0].Chunk.DocumentId);
    }
}
```

Run: `dotnet test --filter "FilterBehaviorTests" -v minimal`
Expected: FAIL — `FilterBehavior` and `ISpecification<T>` on `RetrievalOptions` don't exist.

### Step 5: Add Filter property to RetrievalOptions

In `src/Rag.NET/Models/Options/RetrievalOptions.cs`, add property after existing ones (before `EmbeddingTextOverride`):

```csharp
/// <summary>
/// Optional post-search filter. Only results satisfying this specification are returned.
/// Build complex filters with <c>spec.And(other)</c>, <c>spec.Or(other)</c>, <c>spec.Not()</c>.
/// </summary>
public ISpecification<SearchResult>? Filter { get; init; }
```

Add `using ZeroAlloc.Specification;` and `using Rag.NET.Models;` at the top.

### Step 6: Create FilterBehavior

Create `src/Rag.NET/Retrieval/Behaviors/FilterBehavior.cs`:

```csharp
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class FilterBehavior : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        if (ctx.Options.Filter is null)
            return results;

        return results.Where(r => ctx.Options.Filter.IsSatisfiedBy(r)).ToList();
    }
}
```

### Step 7: Register FilterBehavior in default pipeline

In `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs`, add `typeof(FilterBehavior)` **before** `typeof(VectorStoreBehavior)`:

```csharp
private readonly List<Type> _types =
[
    typeof(ResultCacheBehavior),
    typeof(LostInTheMiddleBehavior),
    typeof(MmrBehavior),
    typeof(RedundancyFilterBehavior),
    typeof(ParentDocumentRetrievalBehavior),
    typeof(RerankingBehavior),
    typeof(MultiQueryBehavior),
    typeof(HydeBehavior),
    typeof(EmbeddingCacheBehavior),
    typeof(FilterBehavior),       // ← new: post-search, pre-terminal
    typeof(VectorStoreBehavior),  // terminal
];
```

Update `PipelineBuilderTests.cs` — `RetrievalBuilder_DefaultContainsAllTenBehaviors` assertion changes from 10 to 11:

```csharp
Assert.Equal(11, types.Count);
```

Run: `dotnet test --filter "FilterBehaviorTests|SpecificationTests|PipelineBuilderTests" -v minimal`
Expected: PASS (all tests)

### Step 8: Run full test suite

Run: `dotnet test -v minimal 2>&1 | tail -20`
Expected: All tests pass.

### Step 9: Commit

```bash
git add -A
git commit -m "feat: add FilterBehavior + built-in specs (ZeroAlloc.Specification)"
```

---

## Final: Full suite verification

### Step 1: Release build

```bash
dotnet build -c Release 2>&1 | tail -10
```
Expected: Build succeeded, 0 Error(s)

### Step 2: Full test run

```bash
dotnet test -c Release -v minimal 2>&1 | tail -20
```
Expected: All tests pass.

### Step 3: Final commit

```bash
git add -A
git commit -m "chore: verify release build and full test suite for ZeroAlloc ecosystem integration"
```
