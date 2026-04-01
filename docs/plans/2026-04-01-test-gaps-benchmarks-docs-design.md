# Test Gaps, Benchmark, and Docs Update — Design

**Date:** 2026-04-01

---

## Goal

Three independent clean-up tasks: add missing vector store DI tests, add a `MindMapExtractor` benchmark, and update `docs/index.md` with packages added since the last diagram update.

---

## 1. Vector Store DI Tests

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/UsePgVectorTests.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseQdrantTests.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseAzureAISearchTests.cs`
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` (add 3 project references)

**Pattern:** Follow `UseSemanticChunkingTests.cs` — `BaseServices()` helper, `AddRagNet(rag => rag.UseXxx(...))`, assert concrete types resolve for each registered interface.

**PgVector** — `UsePgVector("Host=localhost", 1536)`:
- `IVectorStore` → `PgVectorStore`
- `ICollectionManageable` → `PgVectorStore`

**Qdrant** — `UseQdrant("localhost", 6333, "test", 1536)`:
- `IVectorStore` → `QdrantVectorStore`
- `ICollectionManageable` → `QdrantVectorStore`

**AzureAISearch** — `UseAzureAISearch(new Uri("https://example.search.windows.net"), "index", new AzureKeyCredential("key"))`:
- `IVectorStore` → `AzureAISearchVectorStore`
- `IHybridSearchable` → `AzureAISearchVectorStore`
- `ICollectionManageable` → `AzureAISearchVectorStore`

No `BaseServices()` dependencies needed beyond bare `ServiceCollection` — these extensions register self-contained stores.

---

## 2. MindMapExtractor Benchmark

**File:** `benchmarks/Rag.NET.Benchmarks/MindMapBenchmarks.cs`

**Pattern:** Follow `GraphRagBenchmarks.cs` — `[MemoryDiagnoser]`, stubbed `IChatClient` via `FakeChatClient`, in-memory `SqliteGraphStore`, `[GlobalSetup]` prepares JSON responses.

**Benchmarks:**
- `ExtractAsync_InMemoryOnly` — `MindMapExtractor` with `graphStore: null`, measures LLM stub + JSON parse + tree building
- `ExtractAsync_WithGraphStore` — `MindMapExtractor` with `SqliteGraphStore`, measures above + persistence write

**Params:** `[Params(1, 2, 3)] int Depth` — uses pre-built JSON fixtures of trees at each depth (1 root, 1+3, 1+3+9 nodes respectively).

No new project references needed — `Rag.NET.GraphRag` and `Rag.NET.Graph` are already referenced in `Rag.NET.Benchmarks.csproj`.

---

## 3. docs/index.md Update

**File:** `docs/index.md`

Add the following nodes to the Mermaid diagram:

**Under ABSTRACTIONS (chunking packages):**
```
ABSTRACTIONS --> CHUNKING_CS["Rag.NET.Chunking.CSharp<br>Roslyn-based C# chunking"]
```

**Under CORE (new packages):**
```
CORE --> GRAPHRAG["Rag.NET.GraphRag<br>GraphRAG · Mind-Map Extractor"]
CORE --> RERANK_CO["Rag.NET.Reranking.Cohere<br>Cohere reranking API"]
CORE --> RERANK_ON["Rag.NET.Reranking.Onnx<br>Local ONNX cross-encoder"]
CORE --> AUDIO["Rag.NET.Parsers.Audio<br>Whisper.net transcription"]
CORE --> AZBLOB["Rag.NET.DataProviders.AzureBlob<br>Azure Blob Storage"]
CORE --> BOX["Rag.NET.DataProviders.Box<br>Box"]
CORE --> DROPBOX["Rag.NET.DataProviders.Dropbox<br>Dropbox"]
CORE --> GDRIVE["Rag.NET.DataProviders.GoogleDrive<br>Google Drive"]
CORE --> ONEDRIVE["Rag.NET.DataProviders.OneDrive<br>OneDrive"]
CORE --> SHAREPOINT["Rag.NET.DataProviders.SharePoint<br>SharePoint"]
CORE --> WEB["Rag.NET.DataProviders.Web<br>Web crawler · Sitemap · RSS"]
```

Also update the packages reference table below the diagram if it lists packages, to include the same additions.
