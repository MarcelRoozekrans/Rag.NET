# Vector Store Connectors Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Qdrant and Azure AI Search vector store implementations with optional capability interfaces (IHybridSearchable, ICollectionManageable).

**Architecture:** New capability interfaces in core package. Each connector is a separate NuGet package implementing `IVectorStore` plus optional interfaces. Both follow the existing `PgVectorStore` patterns: constructor-based setup, `InitializeAsync` for schema creation, builder extension for DI.

**Tech Stack:** Qdrant.Client, Azure.Search.Documents, Testcontainers (Qdrant), xUnit, NSubstitute

---

### Task 1: Add Capability Interfaces to Core

**Files:**
- Create: `src/Rag.NET/Abstractions/IHybridSearchable.cs`
- Create: `src/Rag.NET/Abstractions/ICollectionManageable.cs`

**Step 1: Create IHybridSearchable.cs**

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

public interface IHybridSearchable
{
    Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);
}
```

**Step 2: Create ICollectionManageable.cs**

```csharp
namespace Rag.NET.Abstractions;

public interface ICollectionManageable
{
    Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET/Rag.NET.csproj`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Rag.NET/Abstractions/IHybridSearchable.cs src/Rag.NET/Abstractions/ICollectionManageable.cs
git commit -m "feat: add IHybridSearchable and ICollectionManageable capability interfaces"
```

---

### Task 2: Qdrant Project Scaffolding

**Files:**
- Create: `src/Rag.NET.Qdrant/Rag.NET.Qdrant.csproj`
- Create: `tests/Rag.NET.Qdrant.Tests/Rag.NET.Qdrant.Tests.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Create src/Rag.NET.Qdrant/Rag.NET.Qdrant.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Qdrant</RootNamespace>
    <PackageId>Rag.NET.Qdrant</PackageId>
    <Description>Qdrant vector store for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Qdrant.Client" Version="1.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create tests/Rag.NET.Qdrant.Tests/Rag.NET.Qdrant.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Qdrant\Rag.NET.Qdrant.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Testcontainers" Version="4.*" />
  </ItemGroup>

</Project>
```

**Step 3: Add projects to Rag.NET.slnx**

Add inside the existing `<Folder Name="/src/">` and `<Folder Name="/tests/">`:

```xml
<!-- In /src/ folder -->
<Project Path="src/Rag.NET.Qdrant/Rag.NET.Qdrant.csproj" />

<!-- In /tests/ folder -->
<Project Path="tests/Rag.NET.Qdrant.Tests/Rag.NET.Qdrant.Tests.csproj" />
```

**Step 4: Verify build**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded.

**Step 5: Commit**

```bash
git add src/Rag.NET.Qdrant/ tests/Rag.NET.Qdrant.Tests/ Rag.NET.slnx
git commit -m "feat: scaffold Qdrant project and test project"
```

---

### Task 3: Qdrant Vector Store Implementation (TDD)

**Files:**
- Create: `tests/Rag.NET.Qdrant.Tests/QdrantVectorStoreTests.cs`
- Create: `src/Rag.NET.Qdrant/QdrantVectorStore.cs`

**Step 1: Write the failing integration tests**

Tests use Testcontainers to spin up Qdrant. Note: Qdrant uses generic Testcontainers (not a PostgreSql-specific one), so we use `ContainerBuilder`.

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Qdrant.Tests;

public class QdrantVectorStoreTests : IAsyncLifetime
{
    private readonly IContainer _qdrant = new ContainerBuilder()
        .WithImage("qdrant/qdrant:latest")
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6334))
        .Build();

    private QdrantVectorStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        await _qdrant.StartAsync(TestContext.Current.CancellationToken);
        var port = _qdrant.GetMappedPublicPort(6334);
        _sut = new QdrantVectorStore($"http://localhost:{port}", "test-collection", vectorDimensions: 3);
        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _qdrant.DisposeAsync();
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
        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_RespectsMinScore()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "close match", DocumentId = "doc-1", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "far match", DocumentId = "doc-1", ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 0.0f, 1.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10, MinScore = 0.9 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("close match", results[0].Chunk.Text);
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = _sut;

        await manageable.CreateCollectionAsync("temp-collection", 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync("temp-collection", TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync("temp-collection", TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync("temp-collection", TestContext.Current.CancellationToken));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Qdrant.Tests --no-build 2>&1 || true`
Expected: Compilation error — `QdrantVectorStore` does not exist.

**Step 3: Write QdrantVectorStore implementation**

```csharp
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Qdrant;

public sealed class QdrantVectorStore : IVectorStore, ICollectionManageable, IDisposable
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly int _vectorDimensions;

    public QdrantVectorStore(string endpoint, string collectionName, int vectorDimensions = 1536)
    {
        _client = new QdrantClient(new Uri(endpoint));
        _collectionName = collectionName;
        _vectorDimensions = vectorDimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exists = await _client.CollectionExistsAsync(_collectionName, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            await _client.CreateCollectionAsync(
                _collectionName,
                new VectorParams { Size = (ulong)_vectorDimensions, Distance = Distance.Cosine },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var points = new List<PointStruct>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var pointId = Guid.NewGuid().ToString();

            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = pointId },
                Vectors = chunk.Embedding.ToArray(),
                Payload =
                {
                    ["text"] = chunk.Chunk.Text,
                    ["document_id"] = chunk.Chunk.DocumentId,
                    ["chunk_index"] = chunk.Chunk.ChunkIndex,
                    ["metadata"] = JsonSerializer.Serialize(chunk.Chunk.Metadata),
                },
            });
        }

        await _client.UpsertAsync(_collectionName, points, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = await _client.SearchAsync(
            _collectionName,
            queryEmbedding.ToArray(),
            limit: (ulong)options.TopK,
            scoreThreshold: (float)options.MinScore,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results
            .Select(point =>
            {
                var metadata = point.Payload.TryGetValue("metadata", out var metaValue)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(metaValue.StringValue) ?? []
                    : [];

                return new SearchResult
                {
                    Chunk = new TextChunk
                    {
                        Text = point.Payload["text"].StringValue,
                        DocumentId = point.Payload["document_id"].StringValue,
                        ChunkIndex = (int)point.Payload["chunk_index"].IntegerValue,
                        Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                    },
                    Score = point.Score,
                };
            })
            .ToList();
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await _client.DeleteAsync(
            _collectionName,
            Qdrant.Client.Grpc.Conditions.Match("document_id", documentId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        await _client.CreateCollectionAsync(
            name,
            new VectorParams { Size = (ulong)vectorDimensions, Distance = Distance.Cosine },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _client.DeleteCollectionAsync(name, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return await _client.CollectionExistsAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Qdrant.Tests -v minimal`
Expected: All 4 tests pass (requires Docker running).

**Step 5: Commit**

```bash
git add src/Rag.NET.Qdrant/QdrantVectorStore.cs tests/Rag.NET.Qdrant.Tests/QdrantVectorStoreTests.cs
git commit -m "feat: add Qdrant vector store with integration tests"
```

---

### Task 4: Qdrant DI Builder Extension

**Files:**
- Create: `src/Rag.NET.Qdrant/QdrantBuilderExtensions.cs`

**Step 1: Create QdrantBuilderExtensions.cs**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Qdrant;

public static class QdrantBuilderExtensions
{
    public static RagBuilder UseQdrant(
        this RagBuilder builder,
        string endpoint,
        string collectionName,
        int vectorDimensions = 1536)
    {
        var store = new QdrantVectorStore(endpoint, collectionName, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Rag.NET.Qdrant/Rag.NET.Qdrant.csproj`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/Rag.NET.Qdrant/QdrantBuilderExtensions.cs
git commit -m "feat: add Qdrant DI builder extension"
```

---

### Task 5: Azure AI Search Project Scaffolding

**Files:**
- Create: `src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj`
- Create: `tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Create src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.AzureAISearch</RootNamespace>
    <PackageId>Rag.NET.AzureAISearch</PackageId>
    <Description>Azure AI Search vector store for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Azure.Search.Documents" Version="11.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.AzureAISearch\Rag.NET.AzureAISearch.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**Step 3: Add projects to Rag.NET.slnx**

Add inside the existing folders:

```xml
<!-- In /src/ folder -->
<Project Path="src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj" />

<!-- In /tests/ folder -->
<Project Path="tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj" />
```

**Step 4: Verify build**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded.

**Step 5: Commit**

```bash
git add src/Rag.NET.AzureAISearch/ tests/Rag.NET.AzureAISearch.Tests/ Rag.NET.slnx
git commit -m "feat: scaffold Azure AI Search project and test project"
```

---

### Task 6: Azure AI Search Vector Store Implementation

**Files:**
- Create: `src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs`

The Azure AI Search store maps chunks to a search index with vector fields. It uses `SearchClient` for queries and `SearchIndexClient` for index management.

**Step 1: Create the search document model (internal)**

Create `src/Rag.NET.AzureAISearch/RagChunkDocument.cs`:

```csharp
using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace Rag.NET.AzureAISearch;

internal sealed class RagChunkDocument
{
    [SimpleField(IsKey = true, IsFilterable = true)]
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName("document_id")]
    public required string DocumentId { get; init; }

    [SimpleField]
    [JsonPropertyName("chunk_index")]
    public required int ChunkIndex { get; init; }

    [SearchableField]
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [SimpleField]
    [JsonPropertyName("metadata")]
    public required string Metadata { get; init; }

    [VectorSearchField(
        VectorSearchDimensions = 1536,
        VectorSearchProfileName = "default-profile")]
    [JsonPropertyName("embedding")]
    public required IReadOnlyList<float> Embedding { get; init; }
}
```

Note: The `VectorSearchDimensions` attribute value is a compile-time constant. The actual dimensions are configured on the index, not the model. This attribute is only used for index creation via `FieldBuilder`.

**Step 2: Create AzureAISearchVectorStore.cs**

```csharp
using System.Text.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AzureAISearch;

public sealed class AzureAISearchVectorStore : IVectorStore, IHybridSearchable
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly string _indexName;
    private readonly int _vectorDimensions;

    public AzureAISearchVectorStore(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions = 1536)
    {
        _indexClient = new SearchIndexClient(endpoint, credential);
        _searchClient = new SearchClient(endpoint, indexName, credential);
        _indexName = indexName;
        _vectorDimensions = vectorDimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunk_index", SearchFieldDataType.Int32),
            new SearchableField("text"),
            new SimpleField("metadata", SearchFieldDataType.String),
            new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = _vectorDimensions,
                VectorSearchProfileName = "default-profile",
            },
        };

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));

        var index = new SearchIndex(_indexName)
        {
            Fields = fields,
            VectorSearch = vectorSearch,
        };

        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var documents = chunks.Select(chunk => new RagChunkDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            DocumentId = chunk.Chunk.DocumentId,
            ChunkIndex = chunk.Chunk.ChunkIndex,
            Text = chunk.Chunk.Text,
            Metadata = JsonSerializer.Serialize(chunk.Chunk.Metadata),
            Embedding = chunk.Embedding.ToArray(),
        }).ToList();

        var batch = IndexDocumentsBatch.Upload(documents);
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Azure AI Search indexing is near real-time; brief wait for consistency in tests
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = options.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = options.TopK,
                        Fields = { "embedding" },
                    },
                },
            },
        };

        return await ExecuteSearchAsync(searchOptions, options.MinScore, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = options.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = options.TopK,
                        Fields = { "embedding" },
                    },
                },
            },
            QueryType = SearchQueryType.Simple,
            SearchMode = SearchMode.Any,
        };

        var response = await _searchClient.SearchAsync<RagChunkDocument>(
            textQuery, searchOptions, cancellationToken).ConfigureAwait(false);

        return await MapResultsAsync(response, options.MinScore, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        // Find all documents with this document_id
        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Filter = $"document_id eq '{documentId}'",
            Select = { "id" },
            Size = 1000,
        };

        var response = await _searchClient.SearchAsync<RagChunkDocument>(
            null, searchOptions, cancellationToken).ConfigureAwait(false);

        var idsToDelete = new List<string>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            idsToDelete.Add(result.Document.Id);
        }

        if (idsToDelete.Count > 0)
        {
            var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
            await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<SearchResult>> ExecuteSearchAsync(
        Azure.Search.Documents.SearchOptions searchOptions,
        double minScore,
        CancellationToken cancellationToken)
    {
        var response = await _searchClient.SearchAsync<RagChunkDocument>(
            null, searchOptions, cancellationToken).ConfigureAwait(false);

        return await MapResultsAsync(response, minScore, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SearchResult>> MapResultsAsync(
        Response<SearchResults<RagChunkDocument>> response,
        double minScore,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();

        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var score = result.Score ?? 0.0;
            if (score < minScore)
            {
                continue;
            }

            var doc = result.Document;
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(doc.Metadata) ?? [];

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    DocumentId = doc.DocumentId,
                    ChunkIndex = doc.ChunkIndex,
                    Text = doc.Text,
                    Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                },
                Score = score,
            });
        }

        return results;
    }
}
```

**Step 2 note:** The `RagChunkDocument` class may need adjustment depending on actual `Azure.Search.Documents` API. The `VectorSearchField` attribute with `VectorSearchDimensions` is set at index creation time via `InitializeAsync`, not via the attribute (which uses a compile-time constant). If the attribute causes issues, remove it and rely on the manual field definition in `InitializeAsync`.

**Step 3: Verify build**

Run: `dotnet build src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Rag.NET.AzureAISearch/RagChunkDocument.cs src/Rag.NET.AzureAISearch/AzureAISearchVectorStore.cs
git commit -m "feat: add Azure AI Search vector store implementation"
```

---

### Task 7: Azure AI Search DI Builder Extension

**Files:**
- Create: `src/Rag.NET.AzureAISearch/AzureAISearchBuilderExtensions.cs`

**Step 1: Create AzureAISearchBuilderExtensions.cs**

```csharp
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.AzureAISearch;

public static class AzureAISearchBuilderExtensions
{
    public static RagBuilder UseAzureAISearch(
        this RagBuilder builder,
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions = 1536)
    {
        var store = new AzureAISearchVectorStore(endpoint, indexName, credential, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<IHybridSearchable>(store);
        return builder;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Rag.NET.AzureAISearch/Rag.NET.AzureAISearch.csproj`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/Rag.NET.AzureAISearch/AzureAISearchBuilderExtensions.cs
git commit -m "feat: add Azure AI Search DI builder extension"
```

---

### Task 8: Azure AI Search Integration Tests (CI-gated)

**Files:**
- Create: `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`

These tests only run when `AZURE_SEARCH_ENDPOINT` and `AZURE_SEARCH_API_KEY` environment variables are set.

**Step 1: Create the test file**

```csharp
using Azure;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

[Collection("AzureAISearch")]
public class AzureAISearchVectorStoreTests : IAsyncLifetime
{
    private readonly string? _endpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT");
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_API_KEY");
    private AzureAISearchVectorStore? _sut;
    private string _indexName = $"ragnet-test-{Guid.NewGuid():N}"[..24];

    public async ValueTask InitializeAsync()
    {
        if (_endpoint is null || _apiKey is null)
        {
            return;
        }

        _sut = new AzureAISearchVectorStore(
            new Uri(_endpoint),
            _indexName,
            new AzureKeyCredential(_apiKey),
            vectorDimensions: 3);

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup: delete the test index
        if (_sut is not null && _endpoint is not null && _apiKey is not null)
        {
            var indexClient = new Azure.Search.Documents.Indexes.SearchIndexClient(
                new Uri(_endpoint), new AzureKeyCredential(_apiKey));

            try
            {
                await indexClient.DeleteIndexAsync(_indexName, TestContext.Current.CancellationToken);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        Skip.If(_sut is null, "AZURE_SEARCH_ENDPOINT and AZURE_SEARCH_API_KEY not set");

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

        await _sut!.StoreAsync(chunks, TestContext.Current.CancellationToken);

        // Azure AI Search indexing is near real-time; wait for consistency
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
        Skip.If(_sut is null, "AZURE_SEARCH_ENDPOINT and AZURE_SEARCH_API_KEY not set");

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = "doc-to-delete", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut!.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }
}
```

**Step 2: Verify build**

Run: `dotnet build tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs
git commit -m "feat: add Azure AI Search integration tests (CI-gated)"
```

---

### Task 9: Final Verification

**Step 1: Build entire solution**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

**Step 2: Run all unit tests (excluding CI-gated Azure tests)**

Run: `dotnet test Rag.NET.slnx -v minimal`
Expected: All tests pass (Azure tests skipped locally).

**Step 3: Commit any remaining changes**

```bash
git add -A
git commit -m "chore: final cleanup for vector store connectors"
```
