# Rag.NET.QueryTechniques

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.QueryTechniques.svg?logo=nuget&label=Rag.NET.QueryTechniques)](https://www.nuget.org/packages/Rag.NET.QueryTechniques)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.QueryTechniques.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.QueryTechniques)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Query-side retrieval techniques for Rag.NET: HyDE (hypothetical document embeddings),
multi-query expansion, and contextual compression of retrieved chunks.

## Install

```bash
dotnet add package Rag.NET.QueryTechniques
```

The core `Rag.NET` package references this one, so most applications already have it;
install it directly only when composing a custom builder.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.QueryTechniques;

rag.UseHyde()
   .UseMultiQueryRetrieval();
```

Both are then switched per request via `RetrievalOptions.UseHyde` /
`RetrievalOptions.UseMultiQuery`.

## Example

Contextual compression trims each retrieved chunk to the sentences that matter for the
query before the prompt is built:

```csharp
using Rag.NET.QueryTechniques;

rag.UseContextualCompression(o =>
{
    o.KeepTopSentences = 3;  // default: extractive, no extra LLM call
});
```

Switch `o.Strategy` to the LLM-abstractive compressor when you want summarised rather
than extracted context.

## Full guide

- [Retrieval](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
