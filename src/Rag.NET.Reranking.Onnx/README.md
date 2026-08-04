# Rag.NET.Reranking.Onnx

Local cross-encoder reranking for Rag.NET on ONNX Runtime: retrieved chunks are re-scored
against the query by a cross-encoder model (ms-marco MiniLM and friends) on your own
hardware — no reranking API, no per-call cost.

## Install

```bash
dotnet add package Rag.NET.Reranking.Onnx
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the reranker registers into.

## Setup

Inside your `AddRagNet(...)` builder callback, point at an exported cross-encoder and its
vocabulary:

```csharp
using Rag.NET.Reranking.Onnx;

rag.UseOnnxReranking(o =>
{
    o.ModelPath = "models/ms-marco-MiniLM-L-6-v2.onnx";
    o.VocabPath = "models/vocab.txt";
});
```

## Example

```csharp
using Rag.NET.Reranking.Onnx;

rag.UseOnnxReranking(o =>
{
    o.ModelPath = "models/ms-marco-MiniLM-L-6-v2.onnx";
    o.VocabPath = "models/vocab.txt";
    o.MaxLength = 512;  // default: query + chunk token budget per pair
});
```

Reranking then applies to every retrieval with `UseReranking = true` (the
`RetrievalOptions` default). Prefer a managed service? See `Rag.NET.Reranking.Cohere`.

## Full guide

- [Post-retrieval](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/post-retrieval.md)
