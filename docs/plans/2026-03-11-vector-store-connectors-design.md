# Vector Store Connectors Design (Qdrant + Azure AI Search)

## Overview

Add two new vector store implementations to Rag.NET: Qdrant (open-source, self-hosted) and Azure AI Search (enterprise, managed). Both implement the existing `IVectorStore` interface for interchangeability, plus optional capability interfaces for advanced features.

## Decisions

- **Stores:** Qdrant + Azure AI Search
- **Interface strategy:** Same `IVectorStore` + optional capability interfaces (`IHybridSearchable`, `ICollectionManageable`)
- **Testing:** Testcontainers for Qdrant, CI-only (env-gated) for Azure AI Search
- **No breaking changes** to existing `IVectorStore` or `RagPipeline`

## New Packages

| Package | Purpose | Key Dependency |
|---|---|---|
| `Rag.NET.Qdrant` | Qdrant vector store | `Qdrant.Client` |
| `Rag.NET.AzureAISearch` | Azure AI Search vector store | `Azure.Search.Documents` |

## Optional Capability Interfaces

Added to core `Rag.NET` package alongside existing `IVectorStore`:

```csharp
public interface IHybridSearchable
{
    Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);
}

public interface ICollectionManageable
{
    Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken ct = default);
    Task DeleteCollectionAsync(string name, CancellationToken ct = default);
    Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default);
}
```

## DI Registration

```csharp
// Qdrant
services.AddRagNet(rag => rag
    .UseQdrant("http://localhost:6334", collectionName: "my-docs"));

// Azure AI Search
services.AddRagNet(rag => rag
    .UseAzureAISearch(
        new Uri("https://my-search.search.windows.net"),
        indexName: "my-docs",
        new AzureKeyCredential("key")));
```

## Qdrant Implementation

- Uses `Qdrant.Client` NuGet package
- Maps `EmbeddedChunk` to Qdrant points with payload (text, document_id, chunk_index, metadata)
- Cosine similarity (matching pgvector behavior)
- Auto-creates collection on `InitializeAsync` if it doesn't exist
- Implements: `IVectorStore`, `IHybridSearchable`, `ICollectionManageable`
- Tests: Testcontainers with `qdrant/qdrant` image

## Azure AI Search Implementation

- Uses `Azure.Search.Documents` NuGet package
- Maps to search index with fields: id, document_id, chunk_index, text, metadata, embedding
- Auto-creates index on `InitializeAsync` if it doesn't exist
- Implements: `IVectorStore`, `IHybridSearchable`
- Hybrid search combines `VectorSearch` + `SearchText`
- Tests: Gated behind `AZURE_SEARCH_ENDPOINT` env var, skipped locally

## Solution Structure (additions)

```
src/
  Rag.NET/
    Abstractions/
      IHybridSearchable.cs
      ICollectionManageable.cs
  Rag.NET.Qdrant/
    QdrantVectorStore.cs
    QdrantBuilderExtensions.cs
    Rag.NET.Qdrant.csproj
  Rag.NET.AzureAISearch/
    AzureAISearchVectorStore.cs
    AzureAISearchBuilderExtensions.cs
    Rag.NET.AzureAISearch.csproj
tests/
  Rag.NET.Qdrant.Tests/
    QdrantVectorStoreTests.cs
    Rag.NET.Qdrant.Tests.csproj
  Rag.NET.AzureAISearch.Tests/
    AzureAISearchVectorStoreTests.cs
    Rag.NET.AzureAISearch.Tests.csproj
```
