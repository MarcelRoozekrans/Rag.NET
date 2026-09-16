# Rag.NET.AnswerEngines

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.AnswerEngines.svg?logo=nuget&label=Rag.NET.AnswerEngines)](https://www.nuget.org/packages/Rag.NET.AnswerEngines)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.AnswerEngines.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.AnswerEngines)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Alternative answer synthesis engines for Rag.NET: MapReduce for large context sets,
Refine for iterative drafts, FLARE for confidence-gated re-retrieval, and a dispatching
engine that picks per question — each with self-assessment confidence scoring.

## Install

```bash
dotnet add package Rag.NET.AnswerEngines
```

## Setup

```csharp
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag.UseMapReduceAnswerEngine());
```

The engine replaces the default single-prompt synthesis; retrieval is untouched.

## Example

FLARE re-retrieves mid-generation whenever the model's own confidence drops below the
threshold:

```csharp
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag.UseFlare(o =>
{
    o.ConfidenceThreshold = 0.6;  // default: re-retrieve below this
    o.MaxRetrievals       = 3;    // default: cap the loop
}));
```

`UseRefineAnswerEngine()` folds chunks into an evolving draft one at a time;
`UseDispatchingAnswerEngine()` routes each question to the engine its shape suits.

## Full guide

- [Retrieval and answering](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
