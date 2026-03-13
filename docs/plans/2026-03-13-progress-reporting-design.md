# Progress Reporting Design

**Goal:** Surface fine-grained ingestion progress to callers via `IProgress<IngestionProgress>` so CLI tools, UI apps, and SignalR endpoints can show live feedback without polling.

**Approach:** Add `IProgress<IngestionProgress>?` as an optional parameter to `IRagPipeline.IngestAsync` and `RagPipeline.IngestAsync`. Reports at 4 stages: Parsing, Chunking, Embedding, Storing. Null-safe — omitting the parameter has zero overhead.

---

## Section 1: Architecture

A new `IngestionProgress` record and `IngestionProgressStage` enum live in `src/Rag.NET/Models/`. The `IRagPipeline` interface gets the new parameter (with default `null`) — binary-compatible change for existing callers. `RagPipeline.IngestAsync` calls `progress?.Report(...)` after each stage completes.

No new abstractions, no new packages, no DI changes.

---

## Section 2: Components

**New files:**
- `src/Rag.NET/Models/IngestionProgressStage.cs` — enum: `Parsing`, `Chunking`, `Embedding`, `Storing`
- `src/Rag.NET/Models/IngestionProgress.cs` — record with: `IngestionProgressStage Stage`, `string DocumentId`, `int? Current`, `int? Total`, `string Message`

**Modified files:**
- `src/Rag.NET/Abstractions/IRagPipeline.cs` — add `IProgress<IngestionProgress>? progress = null` to `IngestAsync`
- `src/Rag.NET/Pipeline/RagPipeline.cs` — add parameter, call `progress?.Report(...)` at 4 points

**Test file:**
- `tests/Rag.NET.Tests/Pipeline/RagPipelineProgressTests.cs`

---

## Section 3: Data Flow

**`IngestAsync` with progress parameter:**

```
[Parsing]   progress.Report({ Stage=Parsing,   DocumentId=..., Current=null, Total=null,           Message="Parsing document" })
[Chunking]  progress.Report({ Stage=Chunking,  DocumentId=..., Current=chunks.Count, Total=null,   Message="Chunked into N chunks" })
[Embedding] progress.Report({ Stage=Embedding, DocumentId=..., Current=chunks.Count, Total=chunks.Count, Message="Generating embeddings" })
[Storing]   progress.Report({ Stage=Storing,   DocumentId=..., Current=chunks.Count, Total=chunks.Count, Message="Storing N chunks" })
```

Callers subscribe via:
```csharp
var progress = new Progress<IngestionProgress>(p =>
    Console.WriteLine($"[{p.Stage}] {p.Message}"));

await pipeline.IngestAsync(stream, metadata, progress: progress);
```

---

## Section 4: Error Handling

- `IProgress<T>.Report` is fire-and-forget — no exception propagation from progress callbacks into the pipeline
- If `IngestAsync` throws mid-way (e.g., embedding API failure after `Chunking` reported), stages reported up to that point are correct and truthful — no cleanup needed
- Early exit (0 chunks): `Parsing` and `Chunking` are reported; `Embedding` and `Storing` are skipped — accurate

---

## Section 5: Testing

Unit tests using a fake `IVectorStore`, `IEmbeddingGenerator`, and `IDocumentParser`:

- Capture all reported stages into a `List<IngestionProgress>`
- Assert all 4 stages appear in order: `Parsing → Chunking → Embedding → Storing`
- Assert `DocumentId` matches the ingested document on every report
- Assert `Current`/`Total` values are correct at `Chunking` and `Storing` stages
- Assert null progress (omitted) does not throw
- Assert 0-chunk document (empty parse result) only reports `Parsing` and `Chunking`
