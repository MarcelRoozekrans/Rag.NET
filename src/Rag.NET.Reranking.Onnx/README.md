# Rag.NET.Reranking.Onnx

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Reranking.Onnx.svg?logo=nuget&label=Rag.NET.Reranking.Onnx)](https://www.nuget.org/packages/Rag.NET.Reranking.Onnx)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Reranking.Onnx.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Reranking.Onnx)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
