# OpenTelemetry Tracing & Metrics Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Instrument the Rag.NET core pipeline with `ActivitySource` spans and `Meter` counters/histograms, and remove the Debug-level `*Started`/`*Completed` log messages they replace.

**Architecture:** A single `static class RagTelemetry` owns the `ActivitySource` and `Meter` plus all instrument instances. Behaviors add spans inline — `StartActivity` returns `null` when no listener is attached, so all `?.` call sites are zero-cost no-ops. No new NuGet packages are added.

**Tech Stack:** `System.Diagnostics.ActivitySource`, `System.Diagnostics.Metrics` (both in-box since .NET 8), xUnit v3, `System.Diagnostics.Metrics.Testing` (in-box since .NET 9).

---

### Task 1: Add `RagTelemetry` static class

**Files:**
- Create: `src/Rag.NET/Telemetry/RagTelemetry.cs`

**Step 1: Create the file**

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rag.NET.Telemetry;

internal static class RagTelemetry
{
    internal const string SourceName = "Rag.NET";

    internal static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
    internal static readonly Meter Meter = new(SourceName, "1.0.0");

    // Histograms
    internal static readonly Histogram<double> IngestDuration =
        Meter.CreateHistogram<double>("ragnet.ingest.duration", "ms", "Total ingestion time per document");
    internal static readonly Histogram<double> EmbedDuration =
        Meter.CreateHistogram<double>("ragnet.embed.duration", "ms", "Embedding generation time per batch");
    internal static readonly Histogram<double> RetrieveDuration =
        Meter.CreateHistogram<double>("ragnet.retrieve.duration", "ms", "End-to-end retrieval time per query");
    internal static readonly Histogram<double> AskDuration =
        Meter.CreateHistogram<double>("ragnet.ask.duration", "ms", "Answer generation time per query");

    // Counters
    internal static readonly Counter<long> ChunksStored =
        Meter.CreateCounter<long>("ragnet.chunks.stored", "chunks", "Total chunks written to the vector store");
    internal static readonly Counter<long> ChunksRetrieved =
        Meter.CreateCounter<long>("ragnet.chunks.retrieved", "chunks", "Total chunks returned by retrieval");
    internal static readonly Counter<long> IngestErrors =
        Meter.CreateCounter<long>("ragnet.ingest.errors", "errors", "Total ingestion failures");
    internal static readonly Counter<long> RetrieveErrors =
        Meter.CreateCounter<long>("ragnet.retrieve.errors", "errors", "Total retrieval failures");
}
```

**Step 2: Build to verify no errors**

```bash
dotnet build src/Rag.NET/ -q
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 3: Commit**

```bash
git add src/Rag.NET/Telemetry/RagTelemetry.cs
git commit -m "feat(telemetry): add RagTelemetry ActivitySource and Meter"
```

---

### Task 2: Instrument `PipelineIngestor` — top-level ingest span + metrics

**Files:**
- Modify: `src/Rag.NET/Ingestion/PipelineIngestor.cs`

**Step 1: Write the failing test**

In `tests/Rag.NET.Tests/Telemetry/` create `IngestTelemetryTests.cs`:

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

public class IngestTelemetryTests
{
    [Fact]
    public async Task IngestAsync_EmitsIngestSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RagTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var pipeline = PipelineIngestorFactory.CreateWithFakeStore();
        var stream = new MemoryStream("hello world"u8.ToArray());
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("test-doc"),
            FileName = "test.txt",
            ContentType = "text/plain",
        };

        await pipeline.IngestAsync(stream, metadata);

        Assert.Contains(activities, a => a.OperationName == "ragnet.ingest");
        var span = activities.First(a => a.OperationName == "ragnet.ingest");
        Assert.Equal("test-doc", span.GetTagItem("document.id"));
        Assert.Equal("text/plain", span.GetTagItem("content.type"));
    }
}
```

Note: `PipelineIngestorFactory` is a test helper that already exists in `tests/Rag.NET.Tests/` — check `tests/Rag.NET.Tests/` for existing builder/factory helpers. If none exists for `PipelineIngestor`, create a minimal one that wires up a `TextDocumentParser`, `RecursiveChunkingStrategy`, and an in-memory `IVectorStore` stub.

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "IngestTelemetryTests" -v minimal
```

Expected: FAIL — no `ragnet.ingest` span emitted yet.

**Step 3: Instrument `PipelineIngestor.IngestAsync`**

Add using at top:
```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;
```

Wrap the pipeline execution in the method:

```csharp
public async Task<Result<IngestionResult, RagError>> IngestAsync(
    Stream document,
    DocumentMetadata metadata,
    IngestionOptions? options = null,
    IProgress<IngestionProgress>? progress = null,
    CancellationToken cancellationToken = default)
{
    // ... existing validation unchanged ...

    var ctx = new IngestionContext { /* ... unchanged ... */ };

    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ingest");
    activity?.SetTag("document.id", metadata.DocumentId.Value);
    activity?.SetTag("content.type", metadata.ContentType);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        RagTelemetry.IngestDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("document.id", metadata.DocumentId.Value));
        activity?.SetTag("chunk.count", result.ChunksStored);
        return Result<IngestionResult, RagError>.Success(result);
    }
    catch (NoParserFoundException ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        RagTelemetry.IngestErrors.Add(1);
        return Result<IngestionResult, RagError>.Failure(new RagError.NoParserFound(ex.ContentType));
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        RagTelemetry.IngestErrors.Add(1);
        return Result<IngestionResult, RagError>.Failure(new RagError.StorageFailed(ex));
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "IngestTelemetryTests" -v minimal
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/PipelineIngestor.cs tests/Rag.NET.Tests/Telemetry/IngestTelemetryTests.cs
git commit -m "feat(telemetry): add ragnet.ingest span and duration metric"
```

---

### Task 3: Instrument `EmbeddingBehavior` — embed span + duration

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs`
- Modify: `tests/Rag.NET.Tests/Telemetry/IngestTelemetryTests.cs`

**Step 1: Add test for embed span**

Add to `IngestTelemetryTests`:

```csharp
[Fact]
public async Task IngestAsync_EmitsEmbedSpan()
{
    var activities = new List<Activity>();
    using var listener = new ActivityListener
    {
        ShouldListenTo = s => s.Name == RagTelemetry.SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activities.Add,
    };
    ActivitySource.AddActivityListener(listener);

    var pipeline = PipelineIngestorFactory.CreateWithFakeStore();
    var stream = new MemoryStream("hello world"u8.ToArray());
    var metadata = new DocumentMetadata
    {
        DocumentId = new DocumentId("test-doc-embed"),
        FileName = "test.txt",
        ContentType = "text/plain",
    };

    await pipeline.IngestAsync(stream, metadata);

    Assert.Contains(activities, a => a.OperationName == "ragnet.embed");
}
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "EmitsEmbedSpan" -v minimal
```

**Step 3: Instrument `EmbeddingBehavior.HandleAsync`**

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;
// ... existing usings ...

public async ValueTask<IngestionResult> HandleAsync(
    IngestionContext ctx, CancellationToken ct,
    Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
{
    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.embed");
    activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
    activity?.SetTag("chunk.count", ctx.Chunks.Count);

    var sw = Stopwatch.StartNew();
    var texts = ctx.Chunks.Select(c => c.Text).ToList();
    var embeddings = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
    sw.Stop();

    RagTelemetry.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds);
    RagTelemetry.ChunksStored.Add(ctx.Chunks.Count);

    ctx.EmbeddedChunks.AddRange(
        ctx.Chunks.Zip(embeddings, (chunk, embedding) =>
            new EmbeddedChunk { Chunk = chunk, Embedding = embedding.Vector }));

    ctx.Progress?.Report(new()
    {
        Stage = IngestionProgressStage.Embedding,
        DocumentId = ctx.Metadata.DocumentId,
        Current = ctx.EmbeddedChunks.Count,
        Total = ctx.EmbeddedChunks.Count,
        Message = $"Generated {ctx.EmbeddedChunks.Count} embeddings",
    });

    return await next(ctx, ct).ConfigureAwait(false);
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "IngestTelemetryTests" -v minimal
```

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/Behaviors/EmbeddingBehavior.cs tests/Rag.NET.Tests/Telemetry/IngestTelemetryTests.cs
git commit -m "feat(telemetry): add ragnet.embed span and duration metric"
```

---

### Task 4: Instrument `ParseBehavior` and `ChunkingBehavior` — parse + chunk spans

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs`
- Modify: `src/Rag.NET/Ingestion/Behaviors/ChunkingBehavior.cs`

**Step 1: Add test**

Add to `IngestTelemetryTests`:

```csharp
[Fact]
public async Task IngestAsync_EmitsParseAndChunkSpans()
{
    var activities = new List<Activity>();
    using var listener = new ActivityListener
    {
        ShouldListenTo = s => s.Name == RagTelemetry.SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activities.Add,
    };
    ActivitySource.AddActivityListener(listener);

    var pipeline = PipelineIngestorFactory.CreateWithFakeStore();
    await pipeline.IngestAsync(
        new MemoryStream("hello world"u8.ToArray()),
        new DocumentMetadata { DocumentId = new DocumentId("d"), FileName = "f.txt", ContentType = "text/plain" });

    Assert.Contains(activities, a => a.OperationName == "ragnet.parse");
    Assert.Contains(activities, a => a.OperationName == "ragnet.chunk");
}
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "EmitsParseAndChunkSpans" -v minimal
```

**Step 3: Instrument `ParseBehavior.HandleAsync`**

Wrap the body:

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;

public async ValueTask<IngestionResult> HandleAsync(...)
{
    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.parse");
    activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
    activity?.SetTag("parser.type", /* ... */ parser.GetType().Name);

    // ... existing body unchanged, activity set after chunks are known ...
    // At the end before return:
    activity?.SetTag("section.count", ctx.Sections.Count);
    activity?.SetTag("chunk.count", ctx.Chunks.Count);

    return await next(ctx, ct).ConfigureAwait(false);
}
```

**Step 4: Instrument `ChunkingBehavior.HandleAsync`**

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;

public ValueTask<IngestionResult> HandleAsync(...)
{
    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.chunk");
    activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
    activity?.SetTag("chunk.count", ctx.Chunks.Count);

    if (ctx.Chunks.Count == 0)
        return ValueTask.FromResult(new IngestionResult { ... });

    ctx.Progress?.Report(...); // unchanged
    return next(ctx, ct);
}
```

**Step 5: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "IngestTelemetryTests" -v minimal
```

**Step 6: Commit**

```bash
git add src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs src/Rag.NET/Ingestion/Behaviors/ChunkingBehavior.cs tests/Rag.NET.Tests/Telemetry/IngestTelemetryTests.cs
git commit -m "feat(telemetry): add ragnet.parse and ragnet.chunk spans"
```

---

### Task 5: Instrument `StorageBehavior` — store span

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs`

No new test needed — `ragnet.store` will appear in the existing full-pipeline ingest test. Verify it appears:

**Step 1: Add assertion to existing test**

Add to `IngestAsync_EmitsIngestSpan` (or a new method):

```csharp
Assert.Contains(activities, a => a.OperationName == "ragnet.store");
var storeSpan = activities.First(a => a.OperationName == "ragnet.store");
Assert.NotNull(storeSpan.GetTagItem("chunk.count"));
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "IngestTelemetryTests" -v minimal
```

**Step 3: Instrument `StorageBehavior.HandleAsync`**

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;

public async ValueTask<IngestionResult> HandleAsync(...)
{
    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.store");
    activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
    activity?.SetTag("chunk.count", ctx.EmbeddedChunks.Count);
    activity?.SetTag("vector_store", VectorStore.GetType().Name);

    await VectorStore.StoreAsync(ctx.EmbeddedChunks, ct).ConfigureAwait(false);

    // ... rest unchanged ...
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "IngestTelemetryTests" -v minimal
```

**Step 5: Commit**

```bash
git add src/Rag.NET/Ingestion/Behaviors/StorageBehavior.cs tests/Rag.NET.Tests/Telemetry/IngestTelemetryTests.cs
git commit -m "feat(telemetry): add ragnet.store span"
```

---

### Task 6: Instrument `PipelineRetriever` — retrieve span + metrics

**Files:**
- Modify: `src/Rag.NET/Retrieval/PipelineRetriever.cs`
- Create: `tests/Rag.NET.Tests/Telemetry/RetrieveTelemetryTests.cs`

**Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

public class RetrieveTelemetryTests
{
    [Fact]
    public async Task RetrieveAsync_EmitsRetrieveSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RagTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var retriever = PipelineRetrieverFactory.CreateWithFakeStore();
        await retriever.RetrieveAsync("what is RAG?");

        Assert.Contains(activities, a => a.OperationName == "ragnet.retrieve");
        var span = activities.First(a => a.OperationName == "ragnet.retrieve");
        Assert.NotNull(span.GetTagItem("query.hash"));
        Assert.NotNull(span.GetTagItem("top_k"));
        Assert.NotNull(span.GetTagItem("result.count"));
    }
}
```

Note: `PipelineRetrieverFactory` — check existing test helpers. Create a minimal one wiring a fake `IVectorStore` stub that returns empty results if none exists.

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "RetrieveTelemetryTests" -v minimal
```

**Step 3: Instrument `PipelineRetriever.RetrieveAsync`**

Add a private helper for query hashing:

```csharp
private static string HashQuery(string query)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query));
    return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
}
```

Wrap the pipeline call:

```csharp
using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.retrieve");
activity?.SetTag("query.hash", HashQuery(query));
activity?.SetTag("top_k", options?.TopK ?? RetrievalOptions.DefaultTopK);

var sw = Stopwatch.StartNew();
try
{
    var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
    sw.Stop();
    activity?.SetTag("result.count", result.Count);
    RagTelemetry.RetrieveDuration.Record(sw.Elapsed.TotalMilliseconds);
    RagTelemetry.ChunksRetrieved.Add(result.Count);
    return Result<IReadOnlyList<SearchResult>, RagError>.Success(result);
}
catch (OperationCanceledException) { throw; }
catch (Exception ex)
{
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    RagTelemetry.RetrieveErrors.Add(1);
    return Result<IReadOnlyList<SearchResult>, RagError>.Failure(new RagError.StorageFailed(ex));
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "RetrieveTelemetryTests" -v minimal
```

**Step 5: Commit**

```bash
git add src/Rag.NET/Retrieval/PipelineRetriever.cs tests/Rag.NET.Tests/Telemetry/RetrieveTelemetryTests.cs
git commit -m "feat(telemetry): add ragnet.retrieve span and metrics"
```

---

### Task 7: Instrument `ChatAnswerEngine` — ask span + duration

**Files:**
- Modify: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`
- Create: `tests/Rag.NET.Tests/Telemetry/AskTelemetryTests.cs`

**Step 1: Write the failing test**

```csharp
public class AskTelemetryTests
{
    [Fact]
    public async Task AskAsync_EmitsAskSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RagTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var fakeChatClient = new FakeChatClient("Paris");
        var engine = new ChatAnswerEngine(fakeChatClient);
        var sources = Array.Empty<SearchResult>();

        await engine.AskAsync("Where is the Eiffel Tower?", sources);

        Assert.Contains(activities, a => a.OperationName == "ragnet.ask");
        var span = activities.First(a => a.OperationName == "ragnet.ask");
        Assert.Equal("0", span.GetTagItem("source.count")?.ToString());
    }
}
```

`FakeChatClient` — check if one already exists in `tests/Rag.NET.Tests/`. If not, create a minimal one returning a fixed `ChatResponse` with the given text.

**Step 2: Run to verify it fails**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "AskTelemetryTests" -v minimal
```

**Step 3: Instrument `ChatAnswerEngine.AskAsync`**

```csharp
using System.Diagnostics;
using Rag.NET.Telemetry;

public async Task<RagResponse> AskAsync(
    string query,
    IReadOnlyList<SearchResult> sources,
    RagOptions? options = null,
    CancellationToken cancellationToken = default)
{
    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ask");
    activity?.SetTag("source.count", sources.Count);
    activity?.SetTag("synthesis.strategy", (options?.SynthesisStrategy ?? SynthesisStrategy.Default).ToString());

    var sw = Stopwatch.StartNew();
    var (messages, chatOptions) = await BuildMessagesAsync(sources, query, options ?? new RagOptions(), cancellationToken).ConfigureAwait(false);
    var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
    sw.Stop();

    RagTelemetry.AskDuration.Record(sw.Elapsed.TotalMilliseconds);

    return new RagResponse { Answer = response.Text ?? string.Empty, Sources = sources };
}
```

**Step 4: Run tests**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "AskTelemetryTests" -v minimal
```

**Step 5: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs tests/Rag.NET.Tests/Telemetry/AskTelemetryTests.cs
git commit -m "feat(telemetry): add ragnet.ask span and duration metric"
```

---

### Task 8: Remove replaced Debug log messages from `RagPipelineLog`

**Files:**
- Modify: `src/Rag.NET/Logging/RagPipelineLog.cs`
- Modify: all call sites (see list below)

**Step 1: Remove these methods from `RagPipelineLog.cs`**

Delete the following `[LoggerMessage]` declarations:
- `IngestStarted`
- `IngestCompleted`
- `IngestFailed` — **KEEP** (error log, not replaced by span alone)
- `RetrieveStarted`
- `RetrieveCompleted`
- `AskStarted`
- `VectorStoreSearchCompleted`
- `RerankingCompleted`
- `RedundancyFilterCompleted`
- `MmrSelectionCompleted`
- `HydeDocumentGenerated`
- `QueryExpansionCompleted`
- `SelfQueryCompleted`
- `ParentDocumentRetrieved`
- `EmbeddingCacheMiss`
- `ResultCacheMiss`

**Keep all:** `*Failed`, `*Error`, `IngestFailed`, `ConversationSummaryFailed`, `MetadataExtractionCompleted`, `MetadataExtractionFailed`, `MmrCandidateCountLessThanTopK`.

**Step 2: Remove call sites**

Remove the corresponding `RagPipelineLog.*` calls from:
- `src/Rag.NET/Retrieval/Behaviors/EmbeddingCacheBehavior.cs` — remove `EmbeddingCacheMiss`
- `src/Rag.NET/Retrieval/Behaviors/EnsembleBehavior.cs` — remove `VectorStoreSearchCompleted`
- `src/Rag.NET/Retrieval/Behaviors/HydeBehavior.cs` — remove `HydeDocumentGenerated`
- `src/Rag.NET/Retrieval/Behaviors/MmrBehavior.cs` — remove `MmrSelectionCompleted`
- `src/Rag.NET/Retrieval/Behaviors/MultiQueryBehavior.cs` — remove `QueryExpansionCompleted`
- `src/Rag.NET/Retrieval/Behaviors/ParentDocumentRetrievalBehavior.cs` — remove `ParentDocumentRetrieved`
- `src/Rag.NET/Retrieval/Behaviors/RedundancyFilterBehavior.cs` — remove `RedundancyFilterCompleted`
- `src/Rag.NET/Retrieval/Behaviors/RerankingBehavior.cs` — remove `RerankingCompleted`
- `src/Rag.NET/Retrieval/Behaviors/ResultCacheBehavior.cs` — remove `ResultCacheMiss`
- `src/Rag.NET/Retrieval/Behaviors/VectorStoreBehavior.cs` — remove `VectorStoreSearchCompleted`
- `src/Rag.NET/SelfQuery/SelfQueryBehavior.cs` — remove `SelfQueryCompleted`

**Step 3: Build**

```bash
dotnet build src/Rag.NET/ -q
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. Fix any remaining compile errors (unused `using` statements).

**Step 4: Run all tests**

```bash
dotnet test tests/Rag.NET.Tests/ -q
```

Expected: all pass.

**Step 5: Commit**

```bash
git add src/
git commit -m "refactor(telemetry): remove Debug-level started/completed log messages replaced by spans"
```

---

### Task 9: Update `features.md` and verify full build

**Step 1: Mark feature done**

In `docs/reference/features.md`, find `### OpenTelemetry Tracing & Metrics` and add:

```
**Status:** ✅ Done
```

**Step 2: Build entire solution**

```bash
dotnet build -q
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 3: Run all tests**

```bash
dotnet test tests/Rag.NET.Tests/ -q
```

**Step 4: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark OpenTelemetry tracing & metrics as done"
```
