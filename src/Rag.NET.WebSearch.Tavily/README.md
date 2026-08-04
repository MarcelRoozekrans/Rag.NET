# Rag.NET.WebSearch.Tavily

Tavily web search for Rag.NET's corrective RAG (CRAG): when retrieval scores poorly
against your own corpus, the pipeline falls back to (or blends in) live web results
instead of answering from weak context.

## Install

```bash
dotnet add package Rag.NET.WebSearch.Tavily
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.WebSearch.Tavily;

services.AddTavilyWebSearch(
    apiKey: Environment.GetEnvironmentVariable("TAVILY_API_KEY")!);
```

## Example

With the search provider registered, CRAG is switched on per retrieval:

```csharp
using Rag.NET.Models;

var results = await pipeline.RetrieveAsync("latest LTS release of .NET", new RetrievalOptions
{
    UseCrag            = true,
    CragScoreThreshold = 0.5f,                     // fall back below this score
    CragFallbackMode   = CragFallbackMode.Replace, // or Augment: blend web + corpus
});
```

Corpus answers stay authoritative when they score well; the web only fills the gaps.

## Full guide

- [Retrieval (corrective RAG)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/retrieval.md)
