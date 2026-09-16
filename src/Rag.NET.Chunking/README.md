# Rag.NET.Chunking

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Chunking.svg?logo=nuget&label=Rag.NET.Chunking)](https://www.nuget.org/packages/Rag.NET.Chunking)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Chunking.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Chunking)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Advanced chunking strategies for Rag.NET: embedding-based semantic chunking, token-aware
windowing, late chunking, LLM proposition extraction, hierarchical merging and code-aware
chunking — pick per corpus what the core package's recursive default cannot express.

## Install

```bash
dotnet add package Rag.NET.Chunking
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the strategies register into.

## Setup

Inside your `AddRagNet(...)` builder callback — one strategy at a time; the last
registration wins:

```csharp
using Rag.NET.Chunking.Semantic;

rag.UseSemanticChunking();
```

## Example

Semantic chunking groups sentences by embedding similarity; token-aware chunking counts
real tokenizer tokens instead of characters:

```csharp
using Rag.NET.Chunking.Semantic;
using Rag.NET.Chunking.TokenAware;
using Rag.NET.Models.Options;

// Tuned semantic chunking:
rag.UseSemanticChunking(new SemanticChunkingOptions
{
    BreakpointPercentile = 0.25f,  // lower = more, smaller chunks
    MinChunkSize = 100,            // characters; undersized groups merge with neighbours
    MaxChunkSize = 1500,           // characters; oversized groups split at sentences
});

// Or: token-aware windows sized for your embedding model.
rag.UseTokenAwareChunking(o =>
{
    o.ModelName        = "gpt-4";  // selects the tokenizer encoding
    o.WindowSizeTokens = 256;
    o.OverlapTokens    = 32;
});
```

`UseHierarchicalMerging`, `UsePropositionChunking`, `UseLateChunking` (pair it with
`Rag.NET.Embeddings.Onnx` token embeddings) and `UseCodeChunking` follow the same shape.

## Full guide

- [Chunking](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/chunking.md)
