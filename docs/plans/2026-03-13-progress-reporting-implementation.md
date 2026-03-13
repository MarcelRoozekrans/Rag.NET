# Progress Reporting Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `IProgress<IngestionProgress>` as an optional parameter to `IRagPipeline.IngestAsync` so callers can receive live stage notifications during document ingestion.

**Architecture:** Two new model types (`IngestionProgressStage` enum, `IngestionProgress` record) in `src/Rag.NET/Models/`. `IRagPipeline.IngestAsync` gains `IProgress<IngestionProgress>? progress = null` before `CancellationToken`. `RagPipeline` calls `progress?.Report(...)` after each of the 4 stages: Parsing, Chunking, Embedding, Storing. Existing callers are unaffected — all new parameters have defaults.

**Tech Stack:** No new packages. `IProgress<T>` is a standard .NET interface.

**Important:** The `progress` parameter must be added **before** `CancellationToken` in the signature. Several existing tests pass `CancellationToken` positionally as the 4th argument — those calls must be updated to use the named `cancellationToken:` parameter.

---

### Task 1: Create the Progress Models

**Files:**
- Create: `src/Rag.NET/Models/IngestionProgressStage.cs`
- Create: `src/Rag.NET/Models/IngestionProgress.cs`

**Step 1: Create the enum**

Create `src/Rag.NET/Models/IngestionProgressStage.cs`:

```csharp
namespace Rag.NET.Models;

public enum IngestionProgressStage
{
    Parsing,
    Chunking,
    Embedding,
    Storing,
}
```

**Step 2: Create the record**

Create `src/Rag.NET/Models/IngestionProgress.cs`:

```csharp
namespace Rag.NET.Models;

public sealed record IngestionProgress
{
    public required IngestionProgressStage Stage { get; init; }
    public required string DocumentId { get; init; }
    public int? Current { get; init; }
    public int? Total { get; init; }
    public required string Message { get; init; }
}
```

**Step 3: Build to verify no errors**

```bash
dotnet build src/Rag.NET --no-restore
```

Expected: builds with 0 errors.

**Step 4: Commit**

```bash
git add src/Rag.NET/Models/IngestionProgressStage.cs src/Rag.NET/Models/IngestionProgress.cs
git commit -m "feat: add IngestionProgress record and IngestionProgressStage enum"
```

---

### Task 2: Update the Interface and Fix Existing Tests

**Files:**
- Modify: `src/Rag.NET/Abstractions/IRagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Context:** Adding `IProgress<IngestionProgress>? progress = null` before `CancellationToken` shifts the parameter positions. The existing `RagPipelineTests` has three tests that pass `CancellationToken` as the 4th positional argument — they must be changed to use `cancellationToken:` named syntax.

Find these three calls in `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`:

```csharp
// Line containing:
await _sut.IngestAsync(stream, metadata, options: null, TestContext.Current.CancellationToken);
// And:
await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = true }, TestContext.Current.CancellationToken);
// And:
await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = false }, TestContext.Current.CancellationToken);
```

**Step 1: Update the interface**

Replace the content of `src/Rag.NET/Abstractions/IRagPipeline.cs` with:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IRagPipeline
{
    Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> RetrieveAsync(
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

**Step 2: Fix the three positional CancellationToken calls in RagPipelineTests**

In `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`, make these replacements (each is one line change):

Change:
```csharp
await _sut.IngestAsync(stream, metadata, options: null, TestContext.Current.CancellationToken);
```
To:
```csharp
await _sut.IngestAsync(stream, metadata, options: null, cancellationToken: TestContext.Current.CancellationToken);
```

Change:
```csharp
await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = true }, TestContext.Current.CancellationToken);
```
To:
```csharp
await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken);
```

Change:
```csharp
await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = false }, TestContext.Current.CancellationToken);
```
To:
```csharp
await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = false }, cancellationToken: TestContext.Current.CancellationToken);
```

**Step 3: Build to verify the interface change is picked up**

```bash
dotnet build tests/Rag.NET.Tests --no-restore
```

Expected: build errors that `RagPipeline` does not implement the updated interface — that is correct and expected at this step. The tests themselves should compile cleanly.

**Step 4: Commit**

```bash
git add src/Rag.NET/Abstractions/IRagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: add IProgress<IngestionProgress> parameter to IRagPipeline.IngestAsync"
```

---

### Task 3: Update RagPipeline and Add Progress Tests

**Files:**
- Modify: `src/Rag.NET/Pipeline/RagPipeline.cs`
- Modify: `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs`

**Step 1: Write the failing progress tests**

Add to `tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs` (at the end of the class, before the `ToAsyncEnumerable` helper):

```csharp
[Fact]
public async Task IngestAsync_WithProgress_ReportsAllFourStagesInOrder()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-progress", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-progress", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-progress", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    var reported = new List<IngestionProgress>();
    var progress = new Progress<IngestionProgress>(p => reported.Add(p));

    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Hello world"));
    await _sut.IngestAsync(stream, metadata, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

    // Progress<T>.Report is async — flush via Task.Yield
    await Task.Yield();

    Assert.Equal(4, reported.Count);
    Assert.Equal(IngestionProgressStage.Parsing, reported[0].Stage);
    Assert.Equal(IngestionProgressStage.Chunking, reported[1].Stage);
    Assert.Equal(IngestionProgressStage.Embedding, reported[2].Stage);
    Assert.Equal(IngestionProgressStage.Storing, reported[3].Stage);
    Assert.All(reported, p => Assert.Equal("doc-progress", p.DocumentId));
}

[Fact]
public async Task IngestAsync_WithNullProgress_DoesNotThrow()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Hello"));
    var ex = await Record.ExceptionAsync(() =>
        _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));

    Assert.Null(ex);
}

[Fact]
public async Task IngestAsync_WithProgress_ReportsChunkCount()
{
    var metadata = new DocumentMetadata { DocumentId = "doc-count", FileName = "test.txt", ContentType = "text/plain" };
    var section = new DocumentSection { Text = "text", DocumentId = "doc-count", SectionIndex = 0 };
    var chunk = new TextChunk { Text = "text", DocumentId = "doc-count", ChunkIndex = 0 };
    var embedding = new Embedding<float>(new float[] { 0.1f });

    _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(section));
    _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(chunk));
    _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

    var reported = new List<IngestionProgress>();
    var progress = new Progress<IngestionProgress>(p => reported.Add(p));

    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("text"));
    await _sut.IngestAsync(stream, metadata, progress: progress, cancellationToken: TestContext.Current.CancellationToken);
    await Task.Yield();

    var chunkingReport = reported.First(p => p.Stage == IngestionProgressStage.Chunking);
    Assert.Equal(1, chunkingReport.Current);

    var storingReport = reported.First(p => p.Stage == IngestionProgressStage.Storing);
    Assert.Equal(1, storingReport.Current);
    Assert.Equal(1, storingReport.Total);
}
```

Also add `using Rag.NET.Models;` to the top of `RagPipelineTests.cs` if not already present.

**Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests --no-build --filter "WithProgress" -v normal
```

Expected: build errors — `RagPipeline` doesn't implement the new interface signature yet.

**Step 3: Update RagPipeline.IngestAsync**

In `src/Rag.NET/Pipeline/RagPipeline.cs`, update the `IngestAsync` signature and add `progress?.Report(...)` calls at each stage.

Replace the existing `IngestAsync` method with:

```csharp
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
    }

    var chunks = new List<TextChunk>();

    await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
    {
        await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }
    }

    progress?.Report(new IngestionProgress
    {
        Stage = IngestionProgressStage.Parsing,
        DocumentId = metadata.DocumentId,
        Message = "Parsing complete",
    });

    foreach (ref var chunk in CollectionsMarshal.AsSpan(chunks))
    {
        foreach (var tag in metadata.Tags)
        {
            chunk.Metadata.TryAdd(tag.Key, tag.Value);
        }
        chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
        chunk.Metadata.TryAdd("file_name", metadata.FileName);
    }

    progress?.Report(new IngestionProgress
    {
        Stage = IngestionProgressStage.Chunking,
        DocumentId = metadata.DocumentId,
        Current = chunks.Count,
        Message = $"Chunked into {chunks.Count} chunks",
    });

    if (chunks.Count == 0)
    {
        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };
    }

    var texts = chunks.Select(c => c.Text).ToList();
    var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

    var embeddedChunks = chunks
        .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk
        {
            Chunk = chunk,
            Embedding = embedding.Vector,
        })
        .ToList();

    progress?.Report(new IngestionProgress
    {
        Stage = IngestionProgressStage.Embedding,
        DocumentId = metadata.DocumentId,
        Current = embeddedChunks.Count,
        Total = embeddedChunks.Count,
        Message = $"Generated {embeddedChunks.Count} embeddings",
    });

    await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);

    progress?.Report(new IngestionProgress
    {
        Stage = IngestionProgressStage.Storing,
        DocumentId = metadata.DocumentId,
        Current = embeddedChunks.Count,
        Total = embeddedChunks.Count,
        Message = $"Stored {embeddedChunks.Count} chunks",
    });

    return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
}
```

**Step 4: Build and run all tests**

```bash
dotnet build tests/Rag.NET.Tests --no-restore
dotnet test tests/Rag.NET.Tests --no-build -v normal 2>&1 | grep -E "Passed|Failed|passed|failed"
```

Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/Pipeline/RagPipeline.cs tests/Rag.NET.Tests/Pipeline/RagPipelineTests.cs
git commit -m "feat: report ingestion progress via IProgress<IngestionProgress> in RagPipeline"
```
