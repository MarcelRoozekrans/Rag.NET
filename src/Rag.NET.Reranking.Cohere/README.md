# Rag.NET.Reranking.Cohere

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Reranking.Cohere.svg?logo=nuget&label=Rag.NET.Reranking.Cohere)](https://www.nuget.org/packages/Rag.NET.Reranking.Cohere)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Reranking.Cohere.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Reranking.Cohere)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
