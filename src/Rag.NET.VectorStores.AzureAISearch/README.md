# Rag.NET.VectorStores.AzureAISearch

Azure AI Search vector store for Rag.NET: vector search over a search index, with
native hybrid (vector + BM25) queries and OData metadata filtering.

## Install

```bash
dotnet add package Rag.NET.VectorStores.AzureAISearch
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the store registers into.

## Setup

Inside your `AddRagNet(...)` builder callback — the credential is an `AzureKeyCredential`
(from Azure.Core, referenced transitively via Azure.Search.Documents):

```csharp
using Rag.NET.AzureAISearch;

rag.UseAzureAISearch(
    endpoint:         new Uri("https://my-search.search.windows.net"),
    indexName:        "my-rag-index",
    credential:       searchCredential,
    vectorDimensions: 1536);
```

## Example

Create the index once at startup, then hybrid search runs inside the service rather than
client-side:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.AzureAISearch;

var store = provider.GetRequiredService<ICollectionManageable>() as AzureAISearchVectorStore;
await store!.InitializeAsync();

var results = await pipeline.RetrieveAsync("ISO 27001 audit requirements", new RetrievalOptions
{
    TopK            = 10,
    UseHybridSearch = true,
});
```

## Full guide

- [Vector stores](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
