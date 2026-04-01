# Test Gaps, Benchmark, and Docs Update — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add missing PgVector/Qdrant/AzureAISearch DI tests, add a MindMapExtractor benchmark, and update docs/index.md with all packages added since the last diagram update.

**Architecture:** Three independent tasks — DI tests follow the existing `UseXxx` pattern in `tests/Rag.NET.Tests/DependencyInjection/`, benchmark follows `GraphRagBenchmarks.cs` (stubbed LLM, in-memory SQLite graph store), docs update is a targeted edit to `docs/index.md`.

**Tech Stack:** xUnit v3, NSubstitute, BenchmarkDotNet, `Rag.NET.Graph.SqliteGraphStore`, `Rag.NET.GraphRag.MindMapExtractor`.

---

### Task 1: PgVector DI test

**Files:**
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` (add project reference)
- Create: `tests/Rag.NET.Tests/DependencyInjection/UsePgVectorTests.cs`

**Step 1: Add project reference**

Open `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` and add inside the existing `<ItemGroup>`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.VectorStores.PgVector\Rag.NET.VectorStores.PgVector.csproj" />
```

**Step 2: Write the failing test**

Create `tests/Rag.NET.Tests/DependencyInjection/UsePgVectorTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.VectorStores.PgVector;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UsePgVectorTests
{
    [Fact]
    public void UsePgVector_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost"))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UsePgVector_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost"))
            .BuildServiceProvider();

        Assert.IsType<PgVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UsePgVector_CustomDimensions_Registered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector("Host=localhost", 768))
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IVectorStore>());
    }
}
```

**Step 3: Run to verify it fails**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "UsePgVectorTests" -v minimal
```

Expected: compile error — `PgVectorStore` or `UsePgVector` not found (missing project reference).

**Step 4: Run after adding the project reference**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "UsePgVectorTests" -v minimal
```

Expected: PASS (3 tests)

> **Note:** `UsePgVector` is defined in namespace `Rag.NET.PgVector` (the `RootNamespace` in the csproj). The extension method is on `IRagBuilder`. Check `src/Rag.NET.VectorStores.PgVector/PgVectorBuilderExtensions.cs` if the using is unclear.

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/Rag.NET.Tests.csproj tests/Rag.NET.Tests/DependencyInjection/UsePgVectorTests.cs
git commit -m "test: add UsePgVector DI registration tests"
```

---

### Task 2: Qdrant DI test

**Files:**
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseQdrantTests.cs`

**Step 1: Add project reference**

Add to `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.VectorStores.Qdrant\Rag.NET.VectorStores.Qdrant.csproj" />
```

**Step 2: Write the failing test**

Create `tests/Rag.NET.Tests/DependencyInjection/UseQdrantTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.VectorStores.Qdrant;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseQdrantTests
{
    [Fact]
    public void UseQdrant_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test"))
            .BuildServiceProvider();

        Assert.IsType<QdrantVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseQdrant_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test"))
            .BuildServiceProvider();

        Assert.IsType<QdrantVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UseQdrant_CustomDimensions_Registered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQdrant("localhost", 6333, "test", 768))
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IVectorStore>());
    }
}
```

**Step 3: Run to verify it fails, then passes**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "UseQdrantTests" -v minimal
```

Expected: PASS (3 tests)

> **Note:** `UseQdrant` is in namespace `Rag.NET.Qdrant`. Check `src/Rag.NET.VectorStores.Qdrant/QdrantBuilderExtensions.cs`.

**Step 4: Commit**

```bash
git add tests/Rag.NET.Tests/Rag.NET.Tests.csproj tests/Rag.NET.Tests/DependencyInjection/UseQdrantTests.cs
git commit -m "test: add UseQdrant DI registration tests"
```

---

### Task 3: AzureAISearch DI test

**Files:**
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseAzureAISearchTests.cs`

**Step 1: Add project reference**

Add to `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`:
```xml
<ProjectReference Include="..\..\src\Rag.NET.VectorStores.AzureAISearch\Rag.NET.VectorStores.AzureAISearch.csproj" />
```

**Step 2: Write the failing test**

Create `tests/Rag.NET.Tests/DependencyInjection/UseAzureAISearchTests.cs`:

```csharp
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.AzureAISearch;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseAzureAISearchTests
{
    private static readonly Uri s_endpoint = new("https://example.search.windows.net");
    private static readonly AzureKeyCredential s_credential = new("fake-key");

    [Fact]
    public void UseAzureAISearch_RegistersIVectorStore()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseAzureAISearch_RegistersIHybridSearchable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<IHybridSearchable>());
    }

    [Fact]
    public void UseAzureAISearch_RegistersICollectionManageable()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential))
            .BuildServiceProvider();

        Assert.IsType<AzureAISearchVectorStore>(sp.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UseAzureAISearch_CustomDimensions_Registered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseAzureAISearch(s_endpoint, "index", s_credential, 768))
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IVectorStore>());
    }
}
```

**Step 3: Run to verify it fails, then passes**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "UseAzureAISearchTests" -v minimal
```

Expected: PASS (4 tests)

> **Note:** `UseAzureAISearch` is in namespace `Rag.NET.AzureAISearch`. Check `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchBuilderExtensions.cs`. `AzureAISearchVectorStore` is `internal` — check whether `InternalsVisibleTo` is set for `Rag.NET.Tests` in the csproj. If not, only assert the interface types rather than `IsType<AzureAISearchVectorStore>`.

**Step 4: Run full test suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v minimal
```

Expected: all pass, no regressions.

**Step 5: Commit**

```bash
git add tests/Rag.NET.Tests/Rag.NET.Tests.csproj tests/Rag.NET.Tests/DependencyInjection/UseAzureAISearchTests.cs
git commit -m "test: add UseAzureAISearch DI registration tests"
```

---

### Task 4: MindMapExtractor benchmark

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/MindMapBenchmarks.cs`

No csproj changes needed — `Rag.NET.GraphRag` and `Rag.NET.Graph` are already referenced.

**Step 1: Create the benchmark file**

Create `benchmarks/Rag.NET.Benchmarks/MindMapBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Graph;
using Rag.NET.GraphRag;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the overhead of MindMapExtractor:
/// - JSON parse and tree building cost (no graph store)
/// - Persistence write cost (SQLite in-memory)
/// Parameterised by tree depth: 1 (root only), 2 (root + 3 children), 3 (root + 3 + 9).
/// All LLM calls are stubbed via FakeChatClient.
/// </summary>
[MemoryDiagnoser]
public class MindMapBenchmarks
{
    [Params(1, 2, 3)]
    public int Depth { get; set; }

    private MindMapExtractor _extractorNoStore = null!;
    private MindMapExtractor _extractorWithStore = null!;
    private SqliteGraphStore _graphStore = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new MindMapOptions { MaxDepth = Depth };
        var fakeClient = new FakeMindMapChatClient(Depth);
        _graphStore = new SqliteGraphStore(":memory:");
        _extractorNoStore = new MindMapExtractor(fakeClient, graphStore: null, options);
        _extractorWithStore = new MindMapExtractor(fakeClient, _graphStore, options);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _graphStore.DisposeAsync().ConfigureAwait(false);

    [Benchmark(Baseline = true)]
    public async Task<MindMapNode> Extract_InMemoryOnly()
        => await _extractorNoStore.ExtractAsync("benchmark document text", "bench-doc", default)
            .ConfigureAwait(false);

    [Benchmark]
    public async Task<MindMapNode> Extract_WithGraphStore()
        => await _extractorWithStore.ExtractAsync("benchmark document text", "bench-doc", default)
            .ConfigureAwait(false);

    // ── Fake chat client ────────────────────────────────────────────────

    private sealed class FakeMindMapChatClient(int depth) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, BuildJson(depth))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private static string BuildJson(int remainingDepth)
        {
            if (remainingDepth <= 1)
                return """{"title":"Leaf","summary":"Leaf node.","children":[]}""";

            var children = string.Join(",",
                Enumerable.Range(1, 3).Select(i =>
                    $"{{\"title\":\"Child {i}\",\"summary\":\"Child {i} summary.\",\"children\":[{BuildChildrenJson(remainingDepth - 2)}]}}"));
            return $"{{\"title\":\"Root\",\"summary\":\"Root summary.\",\"children\":[{children}]}}";
        }

        private static string BuildChildrenJson(int remainingDepth)
        {
            if (remainingDepth <= 0) return string.Empty;
            return string.Join(",",
                Enumerable.Range(1, 3).Select(i =>
                    $"{{\"title\":\"Node {i}\",\"summary\":\"Node {i}.\",\"children\":[]}}"));
        }
    }
}
```

**Step 2: Build to verify it compiles**

```
dotnet build benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj -c Release -v minimal
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add benchmarks/Rag.NET.Benchmarks/MindMapBenchmarks.cs
git commit -m "bench: add MindMapExtractor benchmark (depth 1/2/3, with/without graph store)"
```

---

### Task 5: Update docs/index.md

**Files:**
- Modify: `docs/index.md`

**Step 1: Update the Mermaid diagram**

In `docs/index.md`, after the line:
```
    ABSTRACTIONS --> MEM["Rag.NET.Memory<br>Persistent cross-session memory"]
```

Add:
```
    ABSTRACTIONS --> CHUNKING_CS["Rag.NET.Chunking.CSharp<br>Roslyn-based C# chunking"]
```

After the line:
```
    CORE --> AIRTABLE["Rag.NET.DataProviders.Airtable<br>Airtable rows"]
```

Add:
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

Also add style entries for the new nodes after the existing style block:
```
    style CHUNKING_CS fill:#e8f4fd,stroke:#4a90d9
    style GRAPHRAG fill:#e8f4fd,stroke:#4a90d9
    style RERANK_CO fill:#e8f4fd,stroke:#4a90d9
    style RERANK_ON fill:#e8f4fd,stroke:#4a90d9
    style AUDIO fill:#e8f4fd,stroke:#4a90d9
    style AZBLOB fill:#e8f4fd,stroke:#4a90d9
    style BOX fill:#e8f4fd,stroke:#4a90d9
    style DROPBOX fill:#e8f4fd,stroke:#4a90d9
    style GDRIVE fill:#e8f4fd,stroke:#4a90d9
    style ONEDRIVE fill:#e8f4fd,stroke:#4a90d9
    style SHAREPOINT fill:#e8f4fd,stroke:#4a90d9
    style WEB fill:#e8f4fd,stroke:#4a90d9
```

**Step 2: Update the packages table**

In the packages table below the diagram, add rows after `| \`Rag.NET.Chunking.TokenAware\` | ... |`:

```markdown
| `Rag.NET.Chunking.CSharp` | `CSharpChunkingStrategy` — Roslyn-based semantic chunking for C# source files |
```

Add rows after the `Rag.NET.Parsers.PowerPoint` row:

```markdown
| `Rag.NET.Parsers.Audio` | WAV/MP3/FLAC transcription via Whisper.net (local, no API key required) |
```

Add rows after the `Rag.NET.Mediator` row:

```markdown
| `Rag.NET.GraphRag` | GraphRAG entity extraction, community detection, local/global search, Mind-Map Extractor |
| `Rag.NET.Reranking.Cohere` | `CohereReranker` — hosted cross-encoder reranking via Cohere API |
| `Rag.NET.Reranking.Onnx` | `OnnxReranker` — local ONNX cross-encoder reranking (no API key) |
```

Add rows after the `Rag.NET.DataProviders.Airtable` row:

```markdown
| `Rag.NET.DataProviders.AzureBlob` | Azure Blob Storage — ETag/LastModified delta sync |
| `Rag.NET.DataProviders.Box` | Box — events cursor delta sync |
| `Rag.NET.DataProviders.Dropbox` | Dropbox — cursor-based delta sync |
| `Rag.NET.DataProviders.GoogleDrive` | Google Drive — pageToken change stream |
| `Rag.NET.DataProviders.OneDrive` | OneDrive via Microsoft Graph — deltaLink token |
| `Rag.NET.DataProviders.SharePoint` | SharePoint via Microsoft Graph — deltaLink token |
| `Rag.NET.DataProviders.Web` | Web crawler, Sitemap loader, RSS/Atom feed loader |
```

**Step 3: Commit**

```bash
git add docs/index.md
git commit -m "docs: add missing packages to index.md diagram and table"
```

---

### Task 6: Final verification

**Step 1: Run full test suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v minimal
```

Expected: all pass (598 + 9 new = ~607 tests).

**Step 2: Build benchmarks**

```
dotnet build benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj -c Release -v minimal
```

Expected: 0 errors, 0 warnings.
