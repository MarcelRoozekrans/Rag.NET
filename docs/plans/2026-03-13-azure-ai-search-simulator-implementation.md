# Azure AI Search Simulator Integration Tests Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace env-var-gated `AzureAISearchVectorStoreTests` with Testcontainers using `ghcr.io/ellerbach/azure-ai-search-simulator:latest` so all 4 Azure AI Search tests always run in CI without any Azure subscription.

**Architecture:** Inline `ContainerBuilder` in `AzureAISearchVectorStoreTests` — identical pattern to `QdrantVectorStoreTests`. Container starts in `InitializeAsync`, provides HTTP endpoint on a random host port, disposes in `DisposeAsync`. No new abstractions.

**Tech Stack:** `Testcontainers` NuGet package (already used in Qdrant + pgvector tests), `azure-ai-search-simulator` Docker image (`ghcr.io/ellerbach/azure-ai-search-simulator:latest`).

---

### Task 1: Add Testcontainers Package Reference

**Files:**
- Modify: `tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj`

**Reference:** See `tests/Rag.NET.Qdrant.Tests/Rag.NET.Qdrant.Tests.csproj` for existing pattern — Qdrant uses the base `Testcontainers` package directly.

**Step 1: Add the package reference**

Open `tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj` and add inside the existing `<ItemGroup>`:

```xml
<PackageReference Include="Testcontainers" Version="4.*" />
```

**Step 2: Restore packages**

```bash
dotnet restore tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj
```

Expected: restore completes with no errors.

**Step 3: Commit**

```bash
git add tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj
git commit -m "test: add Testcontainers package to AzureAISearch tests"
```

---

### Task 2: Rewrite Test Class to Use Testcontainers

**Files:**
- Modify: `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

**Reference:** Read `tests/Rag.NET.Qdrant.Tests/QdrantVectorStoreTests.cs` first — the structure of the new class mirrors it exactly.

**Step 1: Verify current tests fail (skip) without env vars**

```bash
dotnet test tests/Rag.NET.AzureAISearch.Tests --no-build -v normal 2>&1 | grep -E "Skip|Pass|Fail"
```

Expected: all 4 tests show as skipped.

**Step 2: Replace the test class**

Overwrite `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs` with:

```csharp
using Azure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

[Collection("AzureAISearch")]
public class AzureAISearchVectorStoreTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder()
        .WithImage("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8080))
        .Build();

    private AzureAISearchVectorStore _sut = null!;
    private readonly string _indexName = $"ragnet-test-{Guid.NewGuid():N}"[..24];

    public async ValueTask InitializeAsync()
    {
        await _simulator.StartAsync(TestContext.Current.CancellationToken);
        var port = _simulator.GetMappedPublicPort(8080);

        _sut = new AzureAISearchVectorStore(
            new Uri($"http://localhost:{port}"),
            _indexName,
            new AzureKeyCredential("test-key"),
            vectorDimensions: 3);

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _simulator.DisposeAsync();
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = "doc-1", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = "doc-1", ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("cats are great", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = "doc-to-delete", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc", DocumentId = "doc-filter-1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = "doc-filter-2", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
                },
                Embedding = new float[] { 0.9f, 0.1f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("engineering doc", results[0].Chunk.Text);
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = (ICollectionManageable)_sut;
        var tempIndex = $"temp-{Guid.NewGuid():N}"[..24];

        await manageable.CreateCollectionAsync(tempIndex, 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync(tempIndex, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));
    }
}
```

**Step 3: Build the test project**

```bash
dotnet build tests/Rag.NET.AzureAISearch.Tests --no-restore
```

Expected: build succeeds with 0 errors.

**Step 4: Run the tests**

```bash
dotnet test tests/Rag.NET.AzureAISearch.Tests -v normal
```

Expected: all 4 tests pass. Docker must be running. First run pulls the image (~200 MB) so may take 1–2 minutes.

> **If a test fails:** Check the simulator logs with `docker logs <container-id>`. The most common issue is the vector search endpoint not matching — the simulator may require `api-version` query parameter. If `SearchAsync` returns an error, read `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs` to see what API version is sent and cross-check with the simulator's `docs/API-REFERENCE.md`.

**Step 5: Commit**

```bash
git add tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "test: replace env-var-gated AzureAISearch tests with Testcontainers simulator"
```
