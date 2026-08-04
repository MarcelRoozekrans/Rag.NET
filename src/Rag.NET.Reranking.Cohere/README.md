# Rag.NET.Reranking.Cohere

Cohere Rerank integration for Rag.NET: retrieved chunks are re-scored by Cohere's
managed cross-encoder (`rerank-english-v3.0` by default) before answer synthesis — the
quality of a cross-encoder without hosting one.

## Install

```bash
dotnet add package Rag.NET.Reranking.Cohere
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the reranker registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Reranking.Cohere;

rag.UseCohereReranking(o =>
{
    o.ApiKey = Environment.GetEnvironmentVariable("COHERE_API_KEY")!;
});
```

## Example

```csharp
using Rag.NET.Reranking.Cohere;

rag.UseCohereReranking(o =>
{
    o.ApiKey = Environment.GetEnvironmentVariable("COHERE_API_KEY")!;
    o.Model  = "rerank-english-v3.0";  // default; multilingual models available
    o.TopN   = 5;                      // default: results kept after reranking
});
```

Reranking then applies to every retrieval with `UseReranking = true` (the
`RetrievalOptions` default). Prefer fully local reranking? See `Rag.NET.Reranking.Onnx`.

## Full guide

- [Post-retrieval](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/post-retrieval.md)
